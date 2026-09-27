namespace Highlights.Core.Stages.Transcription;

/// <summary>Contents of transcript.json. All times are seconds from the start of the video.</summary>
public sealed record Transcript
{
    public required string Model { get; init; }

    /// <summary>Requested language ("auto" or a code).</summary>
    public required string Language { get; init; }

    public string? DetectedLanguage { get; init; }
    public string? Runtime { get; init; }
    public double DurationSeconds { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public required IReadOnlyList<TranscriptSegment> Segments { get; init; }
}

public sealed record TranscriptSegment
{
    public required double Start { get; init; }
    public required double End { get; init; }
    public required string Text { get; init; }
    public double? Probability { get; init; }
    public IReadOnlyList<TranscriptWord>? Words { get; init; }
}

public sealed record TranscriptWord(double Start, double End, string Text, double Probability);
