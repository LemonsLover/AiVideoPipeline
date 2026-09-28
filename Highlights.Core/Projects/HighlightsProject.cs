using System.Text.Json.Serialization;
using Highlights.Core.Media;

namespace Highlights.Core.Projects;

/// <summary>Contents of project.json: the video, per-project settings and the state of every stage.</summary>
public sealed class HighlightsProject
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Absolute path of the source video at the time the project was created.</summary>
    public required string VideoPath { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Link the video was imported from (YouTube etc.), if any.</summary>
    public string? SourceUrl { get; set; }

    public MediaInfo? Media { get; set; }

    public ProjectSettings Settings { get; set; } = new();

    public Dictionary<string, StageState> Stages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every paid LLM call made for this project.</summary>
    public List<LlmUsageRecord> LlmUsage { get; set; } = [];

    public decimal TotalLlmCostUsd => LlmUsage.Sum(u => u.CostUsd ?? 0);

    /// <summary>Project directory; not serialized, set by <see cref="IProjectStore"/>.</summary>
    [JsonIgnore]
    public string Directory { get; set; } = "";

    public string PathOf(string artifact) => Path.Combine(Directory, artifact);

    public StageState GetStage(string name)
    {
        if (!Stages.TryGetValue(name, out var state))
            Stages[name] = state = new StageState();
        return state;
    }

    /// <summary>
    /// The video path, falling back to a file with the same name next to the project directory
    /// (handles the video + project folder being moved together).
    /// </summary>
    public string ResolveVideoPath()
    {
        if (File.Exists(VideoPath))
            return VideoPath;
        var parent = Path.GetDirectoryName(Path.GetFullPath(Directory));
        var sibling = parent is null ? null : Path.Combine(parent, Path.GetFileName(VideoPath));
        return sibling is not null && File.Exists(sibling) ? sibling : VideoPath;
    }
}

/// <summary>Per-project overrides; null means "use the global setting from appsettings.json".</summary>
public sealed class ProjectSettings
{
    /// <summary>Selected voice track: 0-based index among audio streams (ffmpeg <c>0:a:N</c>).</summary>
    public int? AudioTrack { get; set; }

    public string? WhisperModel { get; set; }

    public string? Language { get; set; }

    /// <summary>Editing mode id (file name in Modes/, e.g. "story", "funny", "bestof").</summary>
    public string? Mode { get; set; }

    /// <summary>Plan: target video length in minutes (null/0 = the mode's default for the session length).</summary>
    public double? TargetMinutes { get; set; }

    /// <summary>Render: on-screen title captions (null = Render:Titles).</summary>
    public bool? Titles { get; set; }

    /// <summary>Plan/render: on-screen context captions (null = the mode's default).</summary>
    public bool? ContextCaptions { get; set; }
}

public sealed record LlmUsageRecord(
    string Stage, string Model, int PromptTokens, int CompletionTokens, decimal? CostUsd, DateTimeOffset At);

public enum StageStatus { Pending, Running, Completed, Failed, Cancelled }

public sealed class StageState
{
    public StageStatus Status { get; set; }
    public string? InputsHash { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, string>? Details { get; set; }
}
