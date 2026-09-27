using System.Text.Json;
using Highlights.Core.Configuration;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Stages.Analysis;

/// <summary>A game profile (Profiles/&lt;id&gt;.json): game context and moment categories for the LLM prompt.</summary>
public sealed record GameProfile
{
    public required string Name { get; init; }

    /// <summary>What the game is and what typically happens in it (goes into the system prompt).</summary>
    public string Context { get; init; } = "";

    /// <summary>Category id → what counts as such a moment. Only these categories are requested.</summary>
    public required Dictionary<string, string> Categories { get; init; }

    public int ExpectedMomentsPerHour { get; init; } = 20;
    public int MaxMomentSeconds { get; init; } = 60;

    /// <summary>Extra instructions appended to the system prompt (players' nicknames, running jokes, ...).</summary>
    public string ExtraInstructions { get; init; } = "";
}

public sealed class GameProfileStore(IOptions<AnalyzeOptions> options)
{
    public string Directory => options.Value.ResolveDirectory(options.Value.ProfilesDirectory);

    public IReadOnlyList<string> List() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateFiles(Directory, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Order().ToList()
            : [];

    public string ResolveId(HighlightsProject project) => project.Settings.Game ?? options.Value.DefaultProfile;

    public string PathOf(string id) => Path.Combine(Directory, id + ".json");

    public GameProfile Load(string id)
    {
        var path = PathOf(id);
        if (!File.Exists(path))
            throw new PipelineException($"Game profile '{id}' not found in {Directory}. Available: {string.Join(", ", List())}");
        try
        {
            var profile = JsonSerializer.Deserialize<GameProfile>(File.ReadAllText(path), JsonDefaults.Options)
                          ?? throw new PipelineException($"{path} is empty.");
            if (profile.Categories.Count == 0)
                throw new PipelineException($"{path}: at least one category is required.");
            return profile;
        }
        catch (JsonException ex)
        {
            throw new PipelineException($"{path}: {ex.Message}", ex);
        }
    }
}
