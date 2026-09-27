namespace Highlights.Core.Configuration;

/// <summary>LLM stages (analyze, plan, describe): prompts, editing modes and output language.</summary>
public sealed class AnalyzeOptions
{
    public const string SectionName = "Analyze";

    /// <summary>Editing modes (*.json); relative paths are resolved against the application directory.</summary>
    public string ModesDirectory { get; set; } = "Modes";

    /// <summary>Prompt templates (analyze.*.md, plan.*.md, describe.*.md).</summary>
    public string PromptsDirectory { get; set; } = "Prompts";

    /// <summary>Mode used when the project has none set.</summary>
    public string DefaultMode { get; set; } = "story";

    /// <summary>Language of titles, captions, descriptions and summaries.</summary>
    public string OutputLanguage { get; set; } = "English";

    /// <summary>"loud" events weaker than this (dB above baseline) are left out of the LLM timeline.</summary>
    public double MinLoudPeakDb { get; set; } = 14;

    public string ResolveDirectory(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path), AppContext.BaseDirectory);
}
