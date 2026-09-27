namespace Highlights.Core.Stages.Signals;

/// <summary>Contents of signals.json. All times are seconds from the start of the video.</summary>
public sealed record AudioSignals
{
    public required string Model { get; init; }
    public required double DurationSeconds { get; init; }
    public required LoudnessTrack Loudness { get; init; }
    public required ClassTrack Classes { get; init; }

    /// <summary>Merged intervals: loudness spikes ("loud") and active class groups ("laughter", ...).</summary>
    public required IReadOnlyList<SignalEvent> Events { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>RMS level in dBFS, one value per <see cref="StepSeconds"/>.</summary>
public sealed record LoudnessTrack(double StepSeconds, double MedianDb, double P95Db, float[] Db);

/// <summary>Per-group scores (0..1), one value per YAMNet patch: patch i covers [i·Step, i·Step + Window).</summary>
public sealed record ClassTrack(double StepSeconds, double WindowSeconds, IReadOnlyDictionary<string, float[]> Groups);

/// <summary>
/// <paramref name="Peak"/> is the max group score for class events, or dB above the baseline for "loud".
/// </summary>
public sealed record SignalEvent(string Type, double Start, double End, double Peak);
