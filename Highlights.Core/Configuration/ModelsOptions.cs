namespace Highlights.Core.Configuration;

/// <summary>Where downloaded ML models (Whisper, YAMNet) are stored.</summary>
public sealed class ModelsOptions
{
    public const string SectionName = "Models";

    /// <summary>Empty = a "models" folder next to the exe; a relative path is relative to the exe's folder.</summary>
    public string? Directory { get; set; }

    public string EffectiveDirectory =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            string.IsNullOrWhiteSpace(Directory) ? "models" : Environment.ExpandEnvironmentVariables(Directory)));

    /// <summary>Where earlier versions downloaded models (on the system drive).</summary>
    public static string LegacyDirectory => Path.Combine(HighlightsConfiguration.UserDirectory, "models");

    /// <summary>
    /// If <paramref name="path"/> (a file or folder under <paramref name="modelsDirectory"/>) is missing but an earlier
    /// version downloaded it to <see cref="LegacyDirectory"/>, moves it over instead of downloading again. Returns true when moved.
    /// </summary>
    public static bool TryAdoptLegacy(string path, string modelsDirectory)
    {
        var relative = Path.GetRelativePath(modelsDirectory, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return false;
        var legacy = Path.Combine(LegacyDirectory, relative);
        if (string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            if (File.Exists(legacy) && !File.Exists(path))
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Move(legacy, path); // copies across drives, then deletes the original
                return true;
            }
            if (System.IO.Directory.Exists(legacy))
            {
                // File by file: Directory.Move can't cross drives.
                foreach (var file in System.IO.Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(path, Path.GetRelativePath(legacy, file));
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(file, target, overwrite: true);
                }
                System.IO.Directory.Delete(legacy, recursive: true);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the model is downloaded instead.
        }
        return false;
    }
}
