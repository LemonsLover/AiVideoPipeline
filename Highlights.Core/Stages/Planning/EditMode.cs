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

public sealed class EditModeStore(IOptions<AnalyzeOptions> options)
{
    public string Directory => options.Value.ResolveDirectory(options.Value.ModesDirectory);

    public IReadOnlyList<string> List() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateFiles(Directory, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Order().ToList()
            : [];

    public string ResolveId(HighlightsProject project) => project.Settings.Mode ?? options.Value.DefaultMode;

    public string PathOf(string id) => Path.Combine(Directory, id + ".json");

    public EditMode Load(string id)
    {
        var path = PathOf(id);
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
