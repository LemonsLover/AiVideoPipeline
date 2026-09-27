using CommunityToolkit.Mvvm.ComponentModel;
using Highlights.Core.Stages.Analysis;
using Highlights.Core.Stages.Postprocessing;
using Highlights.Core.Stages.Review;

namespace AiVideoPipeline.ViewModels;

/// <summary>A planned clip with the user's review edit applied on top (edits are written to review.json).</summary>
public sealed partial class ClipViewModel : ObservableObject
{
    private readonly Action _changed;

    public ClipViewModel(Clip clip, ClipEdit edit, IReadOnlyList<Moment> moments, double videoDuration, Action changed)
    {
        Clip = clip;
        Edit = edit;
        Moments = moments;
        VideoDuration = videoDuration;
        _changed = changed;
    }

    public Clip Clip { get; }
    public ClipEdit Edit { get; }
    public IReadOnlyList<Moment> Moments { get; }
    public double VideoDuration { get; }

    /// <summary>The clip as it will be rendered (edges moved, title and caption replaced).</summary>
    public Clip Final => ReviewService.ApplyEdit(Clip, Edit, VideoDuration);

    public string Id => Clip.Id;
    public int Score => Clip.Score;
    public string Category => Clip.Category;

    public string Details => string.Join("\n\n", Moments.Select(m =>
        $"{m.Title} ({m.Category}, fun {m.Score}, story {m.StoryImportance})\n{m.Description}\n«{m.Quote}»"));

    public bool Included
    {
        get => Edit.Included;
        set => Update(() => Edit.Included = value);
    }

    /// <summary>Edited title; empty means the planned one.</summary>
    public string Title
    {
        get => Edit.Title ?? Clip.Title;
        set => Update(() => Edit.Title = string.IsNullOrWhiteSpace(value) || value.Trim() == Clip.Title ? null : value.Trim());
    }

    /// <summary>Context caption shown at the clip start; clearing it removes the planned caption.</summary>
    public string Caption
    {
        get => Edit.Caption ?? Clip.Caption ?? "";
        set => Update(() => Edit.Caption = value.Trim() == (Clip.Caption ?? "") ? null : value.Trim());
    }

    public double Start => Final.Start;
    public double End => Final.End;

    /// <summary>Length in the video (pauses and cut-out stretches removed).</summary>
    public double Duration => Final.Duration;

    public string PiecesText => Clip.Segments.Count == 1
        ? "1 piece"
        : $"{Clip.Segments.Count} pieces · {Clip.End - Clip.Start - Clip.Duration:0}s cut out";

    public bool IsTrimmed => Edit.StartOffset != 0 || Edit.EndOffset != 0;

    public void MoveStart(double seconds) => SetStart(Start + seconds);
    public void MoveEnd(double seconds) => SetEnd(End + seconds);

    public void SetStart(double seconds) =>
        Update(() => Edit.StartOffset = Math.Round(
            Math.Clamp(seconds, 0, Clip.Segments[0].End - ReviewService.MinClipSeconds) - Clip.Segments[0].Start, 2));

    public void SetEnd(double seconds) =>
        Update(() => Edit.EndOffset = Math.Round(
            Math.Clamp(seconds, Clip.Segments[^1].Start + ReviewService.MinClipSeconds, VideoDuration) - Clip.Segments[^1].End, 2));

    public void Reset() => Update(() =>
    {
        Edit.Included = true;
        Edit.StartOffset = Edit.EndOffset = 0;
        Edit.Title = null;
        Edit.Caption = null;
    });

    private void Update(Action apply)
    {
        apply();
        OnPropertyChanged(string.Empty); // every derived property may change
        _changed();
    }
}
