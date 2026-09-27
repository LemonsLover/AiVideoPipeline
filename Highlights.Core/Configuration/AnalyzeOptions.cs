namespace Highlights.Core.Configuration;

public sealed class AnalyzeOptions
{
    public const string SectionName = "Analyze";

    /// <summary>Game profiles (*.json); relative paths are resolved against the application directory.</summary>
    public string ProfilesDirectory { get; set; } = "Profiles";

    /// <summary>Prompt templates (analyze.system.md, analyze.user.md).</summary>
    public string PromptsDirectory { get; set; } = "Prompts";

    /// <summary>Profile used when the project has no game set.</summary>
    public string DefaultProfile { get; set; } = "generic";

    /// <summary>Language of titles, descriptions and the summary.</summary>
    public string OutputLanguage { get; set; } = "English";

    /// <summary>"loud" events weaker than this (dB above baseline) are left out of the LLM timeline.</summary>
    public double MinLoudPeakDb { get; set; } = 14;

    public string ResolveDirectory(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path), AppContext.BaseDirectory);
}
