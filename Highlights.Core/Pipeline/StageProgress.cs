namespace Highlights.Core.Pipeline;

/// <summary>
/// Progress of a pipeline stage. <paramref name="Step"/> distinguishes sub-steps of one stage
/// (e.g. model download inside Transcribe). <paramref name="Fraction"/> is 0..1 or null when unknown.
/// </summary>
public sealed record StageProgress(string Stage, double? Fraction, string? Message = null, string? Step = null);
