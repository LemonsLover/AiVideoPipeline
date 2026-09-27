namespace Highlights.Core.Stages.Analysis;

/// <summary>
/// Contents of moments.json: a beat sheet of the whole session — every notable moment (funny, tense, story beats)
/// with its setup and context. Mode-independent; the plan stage cuts a video out of it.
/// </summary>
public sealed record MomentsDocument
{
    public required string Model { get; init; }

    /// <summary>The game, as recognized from the conversation.</summary>
    public string Game { get; init; } = "";

    public required string Language { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<Moment> Moments { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record Moment
{
    /// <summary>Stable id ("m001", ...) for plan, review and render references.</summary>
    public required string Id { get; init; }

    /// <summary>The moment itself (what happens and the reaction).</summary>
    public required double Start { get; init; }
    public required double End { get; init; }

    /// <summary>Earliest point a viewer must see to understand the moment (≤ Start).</summary>
    public double? SetupStart { get; init; }

    /// <summary>funny, reaction, epic, story or banter.</summary>
    public required string Category { get; init; }

    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Quote { get; init; }

    /// <summary>0–10: how entertaining the moment is on its own.</summary>
    public required int Score { get; init; }

    /// <summary>0–10: how much the session's story needs this beat to be understood.</summary>
    public int StoryImportance { get; init; }

    /// <summary>What a viewer must know that isn't visible in the moment itself (for captions).</summary>
    public string? ContextNote { get; init; }

    public double EffectiveSetupStart => Math.Min(SetupStart ?? Start, Start);
}

/// <summary>Shape the LLM returns (validated, then converted to <see cref="MomentsDocument"/>).</summary>
internal sealed record LlmBeatsAnswer(string Game, string Summary, List<LlmBeat> Beats);

internal sealed record LlmBeat(
    double SetupStart, double Start, double End, string Kind, string Title, string Description, string Quote,
    int Score, int StoryImportance, string ContextNote);
