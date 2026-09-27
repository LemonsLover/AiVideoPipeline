namespace Highlights.Core.Configuration;

/// <summary>Where downloaded ML models (Whisper, YAMNet) are stored.</summary>
public sealed class ModelsOptions
{
    public const string SectionName = "Models";

    /// <summary>Empty = %LOCALAPPDATA%\Highlights\models.</summary>
    public string? Directory { get; set; }

    public string EffectiveDirectory =>
        string.IsNullOrWhiteSpace(Directory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Highlights", "models")
            : Environment.ExpandEnvironmentVariables(Directory);
}
