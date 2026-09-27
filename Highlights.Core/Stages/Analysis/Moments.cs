namespace Highlights.Core.Stages.Analysis;

/// <summary>Contents of moments.json: candidate highlight moments found by the LLM.</summary>
public sealed record MomentsDocument
{
    public required string Model { get; init; }
    public required string Profile { get; init; }
    public required string Language { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<Moment> Moments { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record Moment
{
    /// <summary>Stable id ("m001", ...) for review and render references.</summary>
    public required string Id { get; init; }

    public required double Start { get; init; }
    public required double End { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Quote { get; init; }

    /// <summary>0–10: how strongly the moment deserves to be in the video.</summary>
    public required int Score { get; init; }
}

/// <summary>Shape the LLM returns (validated, then converted to <see cref="MomentsDocument"/>).</summary>
internal sealed record LlmMomentsAnswer(string Summary, List<LlmMoment> Moments);

internal sealed record LlmMoment(
    double Start, double End, string Category, string Title, string Description, string Quote, int Score);
