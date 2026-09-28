using System.Text.Json;
using Highlights.Core.Configuration;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Stages.Planning;

/// <summary>
/// An editing mode (Modes/&lt;id&gt;.json): what the plan stage selects and how tightly clips are cut.
/// Editable without recompiling; copy a file to create a new mode.
/// </summary>
public sealed record EditMode
{
    public required string Name { get; init; }

    /// <summary>One line for the UI.</summary>
    public string Description { get; init; } = "";

    /// <summary>Mode-specific editing instructions inserted into the plan prompt.</summary>
    public required string Instructions { get; init; }

    /// <summary>Default target length as a fraction of the session, clamped to [MinMinutes, MaxMinutes].</summary>
    public double TargetFraction { get; init; } = 0.2;
    public double MinMinutes { get; init; } = 4;
    public double MaxMinutes { get; init; } = 15;

    /// <summary>Longest single clip the plan may contain (after inner cuts).</summary>
    public int MaxClipSeconds { get; init; } = 90;

    /// <summary>On-screen context captions ("20 minutes later…") by default.</summary>
    public bool ContextCaptions { get; init; } = true;

    /// <summary>Margin added around the planned in/out points (the plan already includes the setup).</summary>
    public double PrePaddingSeconds { get; init; } = 1;
    public double PostPaddingSeconds { get; init; } = 1.5;

    /// <summary>Pauses in speech longer than this are cut out of clips (0 disables), unless an audio event happens.</summary>
    public double MaxSilenceSeconds { get; init; } = 2;

    /// <summary>How much of a trimmed pause is kept on each side of the cut.</summary>
    public double KeepSilenceSeconds { get; init; } = 0.4;

    public double TargetMinutes(double sessionSeconds, double? overrideMinutes) =>
        overrideMinutes is > 0
            ? overrideMinutes.Value
            : Math.Round(Math.Clamp(sessionSeconds / 60 * TargetFraction, MinMinutes, MaxMinutes), 1);
}

/// <summary>
/// Editing modes: built-in ones in Modes/ next to the executable (the defaults), user-edited copies in
/// %USERPROFILE%\.highlights\modes (they win; a new file there adds a mode).
/// </summary>
public sealed class EditModeStore(IOptionsMonitor<AnalyzeOptions> options)
{
    public string Directory => options.CurrentValue.ResolveDirectory(options.CurrentValue.ModesDirectory);

    public IReadOnlyList<string> List() =>
        new[] { Directory, HighlightsConfiguration.UserModesDirectory }
            .Where(System.IO.Directory.Exists)
            .SelectMany(d => System.IO.Directory.EnumerateFiles(d, "*.json"))
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToList();

    public string ResolveId(HighlightsProject project) => project.Settings.Mode ?? options.CurrentValue.DefaultMode;

    public string DefaultMode => options.CurrentValue.DefaultMode;

    /// <summary>The mode file in effect: the user's edited copy if there is one, else the built-in one.</summary>
    public string PathOf(string id) => File.Exists(UserPathOf(id)) ? UserPathOf(id) : DefaultPathOf(id);

    public string DefaultPathOf(string id) => Path.Combine(Directory, id + ".json");

    public static string UserPathOf(string id) => Path.Combine(HighlightsConfiguration.UserModesDirectory, id + ".json");

    public bool IsOverridden(string id) => File.Exists(UserPathOf(id));

    public EditMode Load(string id) => LoadFrom(PathOf(id), id);

    /// <summary>The built-in version (null for a mode that only exists as a user file).</summary>
    public EditMode? LoadDefault(string id) => File.Exists(DefaultPathOf(id)) ? LoadFrom(DefaultPathOf(id), id) : null;

    /// <summary>Saves an edited mode; identical to the built-in one removes the override.</summary>
    public void Save(string id, EditMode mode)
    {
        if (LoadDefault(id) is { } builtIn && JsonSerializer.Serialize(builtIn, JsonDefaults.Options) == JsonSerializer.Serialize(mode, JsonDefaults.Options))
        {
            Reset(id);
            return;
        }
        System.IO.Directory.CreateDirectory(HighlightsConfiguration.UserModesDirectory);
        File.WriteAllText(UserPathOf(id), JsonSerializer.Serialize(mode, JsonDefaults.Options));
    }

    public void Reset(string id) => File.Delete(UserPathOf(id));

    private EditMode LoadFrom(string path, string id)
    {
        if (!File.Exists(path))
            throw new PipelineException($"Editing mode '{id}' not found in {Directory}. Available: {string.Join(", ", List())}");
        try
        {
            return JsonSerializer.Deserialize<EditMode>(File.ReadAllText(path), JsonDefaults.Options)
                   ?? throw new PipelineException($"{path} is empty.");
        }
        catch (JsonException ex)
        {
            throw new PipelineException($"{path}: {ex.Message}", ex);
        }
    }
}
