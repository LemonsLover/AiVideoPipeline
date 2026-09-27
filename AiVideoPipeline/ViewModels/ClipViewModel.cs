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

    public string Id => Clip.Id;
    public int Score => Clip.Score;
    public string Category => Clip.Category;
    public string OriginalTitle => Clip.Title;

    public string Details => string.Join("\n\n", Moments.Select(m => $"{m.Title} ({m.Category}, {m.Score})\n{m.Description}\n«{m.Quote}»"));

    public bool Included
    {
        get => Edit.Included;
        set => Update(() => Edit.Included = value);
    }

    /// <summary>Edited title; empty means the LLM's title.</summary>
    public string Title
    {
        get => Edit.Title ?? Clip.Title;
        set => Update(() => Edit.Title = string.IsNullOrWhiteSpace(value) || value.Trim() == Clip.Title ? null : value.Trim());
    }

    public double Start => Math.Clamp(Clip.Start + Edit.StartOffset, 0, VideoDuration);
    public double End => Math.Clamp(Clip.End + Edit.EndOffset, 0, VideoDuration);
    public double Duration => End - Start;
    public bool IsTrimmed => Edit.StartOffset != 0 || Edit.EndOffset != 0;

    public void MoveStart(double seconds) => SetStart(Start + seconds);
    public void MoveEnd(double seconds) => SetEnd(End + seconds);

    public void SetStart(double seconds) =>
        Update(() => Edit.StartOffset = Math.Round(Math.Clamp(seconds, 0, End - ReviewService.MinClipSeconds) - Clip.Start, 2));

    public void SetEnd(double seconds) =>
        Update(() => Edit.EndOffset = Math.Round(Math.Clamp(seconds, Start + ReviewService.MinClipSeconds, VideoDuration) - Clip.End, 2));

    public void Reset() => Update(() =>
    {
        Edit.Included = true;
        Edit.StartOffset = Edit.EndOffset = 0;
        Edit.Title = null;
    });

    private void Update(Action apply)
    {
        apply();
        OnPropertyChanged(string.Empty); // every derived property may change
        _changed();
    }
}
