using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Highlights.Core.Llm;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Planning;
using Microsoft.Extensions.Configuration;

namespace Highlights.Core.Settings;

/// <summary>A setting's value as text ("true"/"false" for booleans, comma-separated for lists, "" = not set).</summary>
public sealed record SettingValue(SettingField Field, string Value, string Default)
{
    public bool IsModified => Normalize(Value) != Normalize(Default);

    internal static string Normalize(string value) => value.Replace("\r\n", "\n").Trim();
}

/// <summary>
/// Reads and writes the pipeline step settings. Defaults come from appsettings.json, the built-in prompts and modes;
/// changes go to %USERPROFILE%\.highlights (pipeline.json, prompts\, modes\), so the built-in files stay untouched.
/// </summary>
public sealed class SettingsStore(IConfiguration configuration, PromptTemplates prompts, EditModeStore modes)
{
    private readonly IConfigurationRoot _defaults = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .Build();

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Current and default values; mode fields refer to the project's mode (or the default mode).</summary>
    public IReadOnlyList<SettingValue> Load(StageSettings stage, HighlightsProject? project)
    {
        var modeId = ModeId(project);
        JsonObject? mode = null, modeDefault = null;
        if (stage.Fields.Any(f => f.Source == SettingSource.Mode))
        {
            mode = ToJson(modes.Load(modeId));
            modeDefault = modes.LoadDefault(modeId) is { } d ? ToJson(d) : mode;
        }

        return stage.Fields.Select(f => f.Source switch
        {
            SettingSource.Prompt => new SettingValue(f, prompts.Read(f.Key), prompts.ReadDefault(f.Key)),
            SettingSource.Mode => new SettingValue(f, Text(mode![f.Key]), Text(modeDefault![f.Key])),
            _ => new SettingValue(f, Read(configuration, f), Read(_defaults, f)),
        }).ToList();
    }

    /// <summary>Saves the given values (unchanged ones included is fine); values equal to the default remove the override.</summary>
    public void Save(StageSettings stage, HighlightsProject? project, IReadOnlyDictionary<SettingField, string> values)
    {
        var pipeline = LoadPipelineJson();
        var modeId = ModeId(project);
        JsonObject? mode = null;

        foreach (var (field, value) in values)
        {
            switch (field.Source)
            {
                case SettingSource.Prompt:
                    prompts.Save(field.Key, value);
                    break;

                case SettingSource.Mode:
                    mode ??= ToJson(modes.Load(modeId));
                    mode[field.Key] = ToNode(field, value);
                    break;

                default:
                    var isDefault = SettingValue.Normalize(value) == SettingValue.Normalize(Read(_defaults, field));
                    SetPath(pipeline, field.Key, isDefault ? null : ToNode(field, value));
                    break;
            }
        }

        if (mode is not null)
            modes.Save(modeId, mode.Deserialize<EditMode>(JsonDefaults.Options)!);

        Prune(pipeline);
        Directory.CreateDirectory(HighlightsConfiguration.UserDirectory);
        if (pipeline.Count == 0)
            File.Delete(HighlightsConfiguration.PipelineSettingsPath);
        else
            File.WriteAllText(HighlightsConfiguration.PipelineSettingsPath, pipeline.ToJsonString(WriteOptions));

        // Apply now rather than waiting for the file watcher.
        (configuration as IConfigurationRoot)?.Reload();
    }

    /// <summary>Resets every setting of the step (and its prompts / mode fields) to the defaults.</summary>
    public void Reset(StageSettings stage, HighlightsProject? project) =>
        Save(stage, project, Load(stage, project).ToDictionary(v => v.Field, v => v.Default));

    public string ModeId(HighlightsProject? project) => project is null ? modes.DefaultMode : modes.ResolveId(project);

    private static string Read(IConfiguration config, SettingField f)
    {
        if (f.Kind == SettingKind.List)
            return string.Join(", ", config.GetSection(f.Key).GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)));
        var value = config[f.Key] ?? "";
        return f.Kind == SettingKind.Bool ? (bool.TryParse(value, out var b) && b ? "true" : "false") : value;
    }

    private static JsonObject ToJson(EditMode mode) => (JsonObject)JsonSerializer.SerializeToNode(mode, JsonDefaults.Options)!;

    private static string Text(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        JsonValue v => v.ToString(),
        _ => node.ToJsonString(),
    };

    /// <summary>Typed JSON for a value: numbers and booleans as such (the config binder needs them parseable).</summary>
    private static JsonNode? ToNode(SettingField f, string value)
    {
        var v = value.Trim();
        switch (f.Kind)
        {
            case SettingKind.Bool:
                return JsonValue.Create(v.Equals("true", StringComparison.OrdinalIgnoreCase));
            case SettingKind.Integer:
                return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? JsonValue.Create(i) : null;
            case SettingKind.Number:
                return double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : null;
            case SettingKind.List:
                var items = v.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return items.Length == 0 ? null : new JsonArray(items.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
            default:
                return f.Optional && v.Length == 0 ? null : JsonValue.Create(value);
        }
    }

    private static JsonObject LoadPipelineJson()
    {
        var path = HighlightsConfiguration.PipelineSettingsPath;
        if (!File.Exists(path))
            return [];
        try
        {
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".broken", overwrite: true); // keep the user's text, start clean
            return [];
        }
    }

    /// <summary>Sets "A:B:C" in a JSON tree (null removes it); case-insensitive like configuration keys.</summary>
    private static void SetPath(JsonObject root, string key, JsonNode? value)
    {
        var parts = key.Split(':');
        var node = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var name = Existing(node, parts[i]) ?? parts[i];
            if (node[name] is not JsonObject child)
            {
                if (value is null)
                    return;
                node[name] = child = [];
            }
            node = child;
        }
        var last = Existing(node, parts[^1]) ?? parts[^1];
        if (value is null)
            node.Remove(last);
        else
            node[last] = value;
    }

    private static string? Existing(JsonObject node, string name) =>
        node.Select(p => p.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes objects left empty after resets.</summary>
    private static void Prune(JsonObject node)
    {
        foreach (var key in node.Select(p => p.Key).ToList())
        {
            if (node[key] is not JsonObject child)
                continue;
            Prune(child);
            if (child.Count == 0)
                node.Remove(key);
        }
    }
}
