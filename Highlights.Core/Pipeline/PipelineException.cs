namespace Highlights.Core.Pipeline;

/// <summary>An expected, user-facing error (missing input, bad configuration, tool failure).</summary>
public class PipelineException(string message, Exception? inner = null) : Exception(message, inner);
