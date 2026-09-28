using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using AiVideoPipeline.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Highlights.Core;
using Highlights.Core.Configuration;
using Highlights.Core.Media;
using Microsoft.Extensions.Options;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Analysis;
using Highlights.Core.Stages.Planning;
using Highlights.Core.Stages.Postprocessing;
using Highlights.Core.Stages.Review;

namespace AiVideoPipeline.ViewModels;

public sealed record LogEntry(DateTime Time, string Text, bool IsError);

/// <remarks>ToString is what the ComboBox selection box shows.</remarks>
public sealed record ModeOption(string Id, string Name, string Description)
{
    public override string ToString() => Name;
}

/// <remarks>ToString is what the ComboBox selection box shows.</remarks>
public sealed record TrackOption(int Index, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IProjectStore _store;
    private readonly PipelineRunner _runner;
    private readonly ReviewService _reviews;
    private readonly EditModeStore _modes;
    private readonly IDialogService _dialogs;
    private readonly YouTubeImporter _importer;
    private readonly IOptionsMonitor<ImportOptions> _importOptions;

    private CancellationTokenSource? _cts;
    private ReviewDocument? _review;
    private CancellationTokenSource? _pendingSave;
    private bool _loadingSettings;
    private readonly HashSet<string> _startedStages = [];

    public MainViewModel(IProjectStore store, PipelineRunner runner, ReviewService reviews, EditModeStore modes,
        IDialogService dialogs, PlayerService player, EnvironmentCheck environment,
        YouTubeImporter importer, IOptionsMonitor<ImportOptions> importOptions)
    {
        _store = store;
        _runner = runner;
        _reviews = reviews;
        _modes = modes;
        _dialogs = dialogs;
        _importer = importer;
        _importOptions = importOptions;
        Player = player;

        Stages =
        [
            new(StageNames.Extract, "Extract voice", false),
            new(StageNames.Transcribe, "Transcribe", false),
            new(StageNames.AudioSignals, "Audio signals", false),
            new(StageNames.Analyze, "Map the session (LLM)", true),
            new(StageNames.Plan, "Plan the cut (LLM)", true),
            new(StageNames.Postprocess, "Build clips", false),
            new(StageNames.Review, "Apply review", false),
            new(StageNames.Render, "Render video", false),
            new(StageNames.Describe, "YouTube text (LLM)", true),
        ];
        var checks = environment.Run();
        foreach (var check in checks)
            AddLog(check.Text, isError: !check.Ok);
        if (checks.FirstOrDefault(c => !c.Ok) is { } problem)
            StatusText = $"Setup problem: {problem.Text}";

        Modes = [.. _modes.List().Select(id => { var m = _modes.Load(id); return new ModeOption(id, m.Name, m.Description); })];
    }

    public PlayerService Player { get; }
    public ObservableCollection<StageViewModel> Stages { get; }
    public ObservableCollection<ClipViewModel> Clips { get; } = [];
    public ObservableCollection<LogEntry> Log { get; } = [];
    public ObservableCollection<TrackOption> Tracks { get; } = [];
    public IReadOnlyList<ModeOption> Modes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProjectOpen), nameof(WindowTitle), nameof(ProjectInfo))]
    [NotifyCanExecuteChangedFor(nameof(RunStageCommand), nameof(BuildVideoCommand), nameof(OpenOutputCommand))]
    public partial HighlightsProject? Project { get; private set; }

    public bool IsProjectOpen => Project is not null;
    public string WindowTitle => Project is null ? "Highlights Studio" : $"{Path.GetFileName(Project.ResolveVideoPath())} — Highlights Studio";

    public string ProjectInfo => Project?.Media is { } m
        ? $"{Format(m.DurationSeconds)} · {m.Video?.Width}×{m.Video?.Height} · {m.AudioTracks.Count} audio track(s)"
        : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(RunStageCommand), nameof(BuildVideoCommand), nameof(CancelCommand), nameof(OpenCommand), nameof(ImportCommand))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "Open a gameplay recording to start.";

    [ObservableProperty]
    public partial double? StatusProgress { get; private set; }

    [ObservableProperty]
    public partial string CostText { get; private set; } = "";

    [ObservableProperty]
    public partial string ClipsSummary { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayClipCommand), nameof(NudgeCommand), nameof(SetStartAtPlayheadCommand),
        nameof(SetEndAtPlayheadCommand), nameof(ResetClipCommand))]
    public partial ClipViewModel? SelectedClip { get; set; }

    // ---- Project settings (saved to project.json) ----

    [ObservableProperty]
    public partial TrackOption? SelectedTrack { get; set; }

    [ObservableProperty]
    public partial ModeOption? SelectedMode { get; set; }

    [ObservableProperty]
    public partial bool ContextCaptions { get; set; } = true;

    /// <summary>0 = no target, every moment above the threshold.</summary>
    [ObservableProperty]
    public partial double TargetMinutes { get; set; }

    [ObservableProperty]
    public partial bool Titles { get; set; }

    partial void OnSelectedTrackChanged(TrackOption? value) => SaveSettings(p => p.Settings.AudioTrack = value?.Index);
    partial void OnSelectedModeChanged(ModeOption? value) => SaveSettings(p => p.Settings.Mode = value?.Id);
    partial void OnContextCaptionsChanged(bool value) => SaveSettings(p => p.Settings.ContextCaptions = value);
    partial void OnTargetMinutesChanged(double value) => SaveSettings(p => p.Settings.TargetMinutes = Math.Max(0, value));
    partial void OnTitlesChanged(bool value) => SaveSettings(p => p.Settings.Titles = value);

    partial void OnSelectedClipChanged(ClipViewModel? value)
    {
        if (value is not null)
            Player.Seek(value.Start);
    }

    // ---- Opening ----

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task OpenAsync()
    {
        if (_dialogs.PickVideoOrProject() is { } path)
            await OpenPathAsync(path);
    }

    /// <summary>Downloads a video by link (yt-dlp) and opens its project.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ImportAsync()
    {
        if (_dialogs.AskImport(_importOptions.CurrentValue.EffectiveDownloadDirectory) is not { } request)
            return;

        await FlushReviewAsync();
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<StageProgress>(p =>
        {
            StatusText = $"Import: {p.Message}";
            StatusProgress = p.Fraction;
        });
        AddLog($"Importing {request.Url}");
        HighlightsProject? project = null;
        try
        {
            project = await Task.Run(() => _importer.ImportAsync(request.Url, request.Folder, progress, _cts.Token));
            AddLog($"Downloaded {project.ResolveVideoPath()}");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Import cancelled.";
            AddLog("Import cancelled", isError: true);
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {FirstLine(ex.Message)}";
            AddLog($"Import failed: {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
            StatusProgress = null;
            _cts.Dispose();
            _cts = null;
        }

        if (project is not null)
            await OpenPathAsync(project.Directory);
    }

    public async Task OpenPathAsync(string path)
    {
        try
        {
            await FlushReviewAsync();
            HighlightsProject project;
            if (IsVideo(path) && !File.Exists(Path.Combine(ProjectLayout.ProjectDirectoryFor(path), ProjectLayout.ProjectFile)))
            {
                StatusText = "Reading media info…";
                project = await _store.CreateAsync(path);
                AddLog($"Created project {project.Directory}");
            }
            else
            {
                project = await _store.LoadAsync(path);
                AddLog($"Opened project {project.Directory}");
            }

            Project = project;
            LoadSettings();
            Player.Load(project.ResolveVideoPath());
            RefreshStages();
            await LoadClipsAsync();
            StatusText = project.Settings.AudioTrack is null
                ? "Pick the voice track, then build the video."
                : "Ready.";
        }
        catch (Exception ex) when (ex is PipelineException or IOException or UnauthorizedAccessException)
        {
            AddLog(ex.Message, isError: true);
            _dialogs.ShowError(ex.Message);
        }
    }

    private static bool IsVideo(string path) =>
        File.Exists(path) && !Path.GetFileName(path).Equals(ProjectLayout.ProjectFile, StringComparison.OrdinalIgnoreCase);

    private void LoadSettings()
    {
        var p = Project!;
        _loadingSettings = true;
        try
        {
            Tracks.Clear();
            foreach (var t in p.Media?.AudioTracks ?? [])
                Tracks.Add(new TrackOption(t.Index,
                    $"#{t.Index} {t.Title ?? "(no title)"} · {t.Codec} {t.ChannelLayout ?? $"{t.Channels}ch"}{(t.Language is null ? "" : $" · {t.Language}")}"));
            SelectedTrack = Tracks.FirstOrDefault(t => t.Index == p.Settings.AudioTrack);
            var modeId = _modes.ResolveId(p);
            SelectedMode = Modes.FirstOrDefault(m => m.Id == modeId);
            ContextCaptions = p.Settings.ContextCaptions ?? (SelectedMode is null || _modes.Load(modeId).ContextCaptions);
            TargetMinutes = p.Settings.TargetMinutes ?? 0;
            Titles = p.Settings.Titles ?? false;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private async void SaveSettings(Action<HighlightsProject> apply)
    {
        if (_loadingSettings || Project is null || IsBusy)
            return;
        apply(Project);
        try
        {
            await _store.SaveAsync(Project);
        }
        catch (IOException ex)
        {
            AddLog($"Could not save project settings: {ex.Message}", isError: true);
        }
        RefreshStages();
    }

    // ---- Running stages ----

    /// <summary>Runs one stage (and whatever it depends on). An up-to-date stage is re-run (forced).</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunStageAsync(StageViewModel stage)
    {
        var force = stage.State == StageState.UpToDate;
        if (force && stage.UsesLlm && !_dialogs.Confirm($"\"{stage.Title}\" is up to date. Ask the LLM again (costs money)?", "Re-run"))
            return;
        await RunAsync((progress, ct) => _runner.RunAsync(Project!, stage.Name, force, progress, ct), stage.Title);
    }

    /// <summary>Runs everything needed for highlights.mp4 and youtube.txt.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task BuildVideoAsync() =>
        await RunAsync(async (progress, ct) =>
        {
            await _runner.RunAsync(Project!, StageNames.Render, false, progress, ct);
            await _runner.RunAsync(Project!, StageNames.Describe, false, progress, ct);
        }, "Build video");

    private bool CanRun() => Project is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Cancelling…";
    }

    private async Task RunAsync(Func<IProgress<StageProgress>, CancellationToken, Task> work, string title)
    {
        await FlushReviewAsync();
        IsBusy = true;
        _cts = new CancellationTokenSource();
        _startedStages.Clear();
        var progress = new Progress<StageProgress>(OnProgress);
        AddLog($"{title}: started");
        try
        {
            // Some steps (spectrogram, model loading) are CPU-bound; keep them off the UI thread.
            await Task.Run(() => work(progress, _cts.Token));
            StatusText = $"{title}: done.";
            AddLog($"{title}: done");
        }
        catch (OperationCanceledException)
        {
            StatusText = $"{title}: cancelled.";
            AddLog($"{title}: cancelled", isError: true);
        }
        catch (Exception ex)
        {
            StatusText = $"{title} failed: {FirstLine(ex.Message)}";
            AddLog($"{title} failed: {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
            StatusProgress = null;
            _cts.Dispose();
            _cts = null;
            RefreshStages();
            await LoadClipsAsync();
        }
    }

    private void OnProgress(StageProgress p)
    {
        var stage = Stages.FirstOrDefault(s => s.Name == p.Stage);
        if (stage is null)
            return;
        if (p.Message == "up to date")
        {
            // Skipped dependency: keep its summary line.
            stage.State = StageState.UpToDate;
            return;
        }

        if (_startedStages.Add(p.Stage))
        {
            AddLog($"{stage.Title}…");
            // Stages run one after another, so whatever was running before has finished.
            foreach (var other in Stages.Where(s => s != stage && s.State == StageState.Running))
            {
                other.State = StageState.UpToDate;
                other.Progress = null;
            }
        }

        stage.State = StageState.Running;
        stage.Progress = p.Fraction;
        if (p.Message is not null)
            stage.Detail = p.Step is null ? p.Message : $"{p.Step}: {p.Message}";
        StatusText = $"{stage.Title}: {stage.Detail}";
        StatusProgress = p.Fraction;
    }

    private void RefreshStages()
    {
        if (Project is null)
            return;
        foreach (var s in Stages)
        {
            var state = Project.Stages.GetValueOrDefault(s.Name);
            s.Progress = null;
            if (_runner.IsUpToDate(Project, _runner.GetStage(s.Name)))
            {
                s.State = StageState.UpToDate;
                s.Detail = Summarize(s.Name, state?.Details);
            }
            else if (state?.Status is StageStatus.Failed or StageStatus.Cancelled)
            {
                s.State = StageState.Failed;
                s.Detail = state.Status == StageStatus.Cancelled ? "cancelled" : FirstLine(state.Error ?? "failed");
            }
            else
            {
                s.State = state?.Status == StageStatus.Completed ? StageState.Outdated : StageState.NotRun;
                s.Detail = state?.Status == StageStatus.Completed ? "inputs changed — will re-run" : null;
            }
        }
        CostText = Project.LlmUsage.Count == 0 ? "" : $"LLM cost: ${Project.TotalLlmCostUsd.ToString("0.###", CultureInfo.InvariantCulture)}";
        OpenOutputCommand.NotifyCanExecuteChanged();
    }

    private static string? Summarize(string stage, IReadOnlyDictionary<string, string>? d)
    {
        if (d is null)
            return null;
        string? V(string key) => d.GetValueOrDefault(key);
        return stage switch
        {
            StageNames.Transcribe => $"{V("segments")} segments · {V("language")} · {V("runtime")}",
            StageNames.AudioSignals => string.Join(" · ", d.Where(x => x.Key.StartsWith("events.")).Select(x => $"{x.Value} {x.Key[7..]}")),
            StageNames.Analyze => $"{V("moments")} beats · {V("game")}",
            StageNames.Plan => $"{V("mode")} · {V("clips")} clips · {Format(double.Parse(V("plannedSeconds") ?? "0", CultureInfo.InvariantCulture))} · {V("captions")} captions",
            StageNames.Postprocess => $"{V("clips")} clips · {V("segments")} pieces · {Format(double.Parse(V("totalSeconds") ?? "0", CultureInfo.InvariantCulture))}",
            StageNames.Review => $"{V("clips")} clips · {Format(double.Parse(V("totalSeconds") ?? "0", CultureInfo.InvariantCulture))}",
            StageNames.Render => $"{Format(double.Parse(V("durationSeconds") ?? "0", CultureInfo.InvariantCulture))} · {V("encoder")} · {V("loudness")}",
            StageNames.Describe => $"{V("chapters")} chapters",
            _ => null,
        };
    }

    // ---- Review ----

    private async Task LoadClipsAsync()
    {
        var selectedKey = SelectedClip is { } s ? ReviewService.KeyOf(s.Clip) : null;
        Clips.Clear();
        _review = null;
        ClipsSummary = "";
        if (Project is null || !File.Exists(Project.PathOf(ProjectLayout.ClipsFile)) || !File.Exists(Project.PathOf(ProjectLayout.MomentsFile)))
            return;

        try
        {
            var clips = await JsonDefaults.ReadAsync<ClipsDocument>(Project.PathOf(ProjectLayout.ClipsFile));
            var moments = (await JsonDefaults.ReadAsync<MomentsDocument>(Project.PathOf(ProjectLayout.MomentsFile))).Moments
                .ToDictionary(m => m.Id);
            _review = await _reviews.LoadAsync(Project);
            var duration = Project.Media?.DurationSeconds ?? double.MaxValue;

            foreach (var clip in clips.Clips)
            {
                var key = ReviewService.KeyOf(clip);
                if (!_review.Clips.TryGetValue(key, out var edit))
                    _review.Clips[key] = edit = new ClipEdit();
                var clipMoments = clip.MomentIds.Where(moments.ContainsKey).Select(id => moments[id]).ToList();
                Clips.Add(new ClipViewModel(clip, edit, clipMoments, duration, OnClipChanged));
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            AddLog($"Could not load clips: {ex.Message}", isError: true);
        }

        SelectedClip = Clips.FirstOrDefault(c => ReviewService.KeyOf(c.Clip) == selectedKey);
        UpdateClipsSummary();
    }

    private void OnClipChanged()
    {
        UpdateClipsSummary();
        ScheduleReviewSave();
    }

    private void UpdateClipsSummary()
    {
        var included = Clips.Where(c => c.Included).ToList();
        ClipsSummary = Clips.Count > 0
            ? $"{included.Count} of {Clips.Count} clips · {Format(included.Sum(c => c.Duration))}"
            : Project is not null && File.Exists(Project.PathOf(ProjectLayout.ClipsFile))
                ? "The plan has no clips — try another mode or target."
                : "No clips yet — run \"Build clips\".";
    }

    /// <summary>Saves review.json shortly after the last edit (edits come in bursts while nudging).</summary>
    private async void ScheduleReviewSave()
    {
        _pendingSave?.Cancel();
        var cts = _pendingSave = new CancellationTokenSource();
        try
        {
            await Task.Delay(500, cts.Token);
            await SaveReviewAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task FlushReviewAsync()
    {
        if (_pendingSave is { IsCancellationRequested: false } pending)
        {
            pending.Cancel();
            await SaveReviewAsync();
        }
    }

    private async Task SaveReviewAsync()
    {
        _pendingSave = null;
        if (Project is null || _review is null)
            return;
        try
        {
            await _reviews.SaveAsync(Project, _review);
            // SaveAsync drops default (unedited) entries; the view models still reference their edit objects.
            foreach (var c in Clips)
                _review.Clips.TryAdd(ReviewService.KeyOf(c.Clip), c.Edit);
        }
        catch (IOException ex)
        {
            AddLog($"Could not save review.json: {ex.Message}", isError: true);
        }
        if (!IsBusy)
            RefreshStages();
    }

    [RelayCommand]
    private void PlayPause() => Player.TogglePlay();

    [RelayCommand(CanExecute = nameof(HasSelectedClip))]
    private void PlayClip()
    {
        if (SelectedClip is { } c)
            Player.PlaySegments(c.Final.Segments.Select(r => (r.Start, r.End)).ToList());
    }

    /// <summary>Parameter "start:-1", "end:+0.2", …</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedClip))]
    private void Nudge(string parameter)
    {
        if (SelectedClip is not { } c || parameter.Split(':') is not [var edge, var amount])
            return;
        var seconds = double.Parse(amount, CultureInfo.InvariantCulture);
        if (edge == "start")
        {
            c.MoveStart(seconds);
            Player.Seek(c.Start);
        }
        else
        {
            c.MoveEnd(seconds);
            Player.Seek(Math.Max(c.Start, c.End - 3)); // show the last seconds of the clip
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedClip))]
    private void SetStartAtPlayhead() => SelectedClip?.SetStart(Player.Time);

    [RelayCommand(CanExecute = nameof(HasSelectedClip))]
    private void SetEndAtPlayhead() => SelectedClip?.SetEnd(Player.Time);

    [RelayCommand(CanExecute = nameof(HasSelectedClip))]
    private void ResetClip() => SelectedClip?.Reset();

    private bool HasSelectedClip() => SelectedClip is not null;

    // ---- Shell ----

    [RelayCommand(CanExecute = nameof(HasOutput))]
    private void OpenOutput() => _dialogs.OpenInShell(Project!.PathOf(ProjectLayout.VideoOutputFile));

    private bool HasOutput() => Project is not null && File.Exists(Project.PathOf(ProjectLayout.VideoOutputFile));

    [RelayCommand]
    private void OpenYouTubeText()
    {
        if (Project is not null && File.Exists(Project.PathOf(ProjectLayout.YouTubeFile)))
            _dialogs.OpenInShell(Project.PathOf(ProjectLayout.YouTubeFile));
    }

    [RelayCommand]
    private void OpenProjectFolder()
    {
        if (Project is not null)
            _dialogs.OpenInShell(Project.Directory);
    }

    [RelayCommand]
    private void OpenSettings() => _dialogs.OpenInShell(HighlightsConfiguration.UserSettingsPath);

    // ---- Helpers ----

    private void AddLog(string text, bool isError = false)
    {
        Log.Add(new LogEntry(DateTime.Now, text, isError));
        if (Log.Count > 500)
            Log.RemoveAt(0);
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    public static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
