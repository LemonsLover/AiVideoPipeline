namespace Highlights.Core.Stages.Planning;

/// <summary>Contents of plan.json: the cut an LLM editor made from the beats for one editing mode.</summary>
public sealed record PlanDocument
{
    public required string Mode { get; init; }
    public required string ModeName { get; init; }
    public required string Model { get; init; }
    public required double TargetMinutes { get; init; }
    public required bool ContextCaptions { get; init; }

    /// <summary>Planned footage length (clips minus inner cuts), before padding and silence trimming.</summary>
    public required double PlannedSeconds { get; init; }

    public required IReadOnlyList<PlannedClip> Clips { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record PlannedClip
{
    public required string Id { get; init; }

    /// <summary>Beats (moments.json ids) the clip shows; empty for a pure setup/bridge clip.</summary>
    public required IReadOnlyList<string> MomentIds { get; init; }

    /// <summary>In/out points in source time.</summary>
    public required double Start { get; init; }
    public required double End { get; init; }

    /// <summary>Uninteresting stretches inside [Start, End] to remove.</summary>
    public required IReadOnlyList<TimeRange> Cuts { get; init; }

    /// <summary>On-screen context line at the start of the clip; null when none is needed.</summary>
    public string? Caption { get; init; }

    public required string Title { get; init; }

    public double KeptSeconds => new TimeRange(Start, End).Duration - Cuts.Sum(c => c.Duration);
}

internal sealed record LlmPlanAnswer(List<LlmPlannedClip> Clips);

internal sealed record LlmPlannedClip(
    List<string> BeatIds, double Start, double End, List<LlmCut> Cuts, string Caption, string Title);

internal sealed record LlmCut(double Start, double End);
