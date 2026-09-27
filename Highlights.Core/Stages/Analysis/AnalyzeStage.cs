using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Highlights.Core.Configuration;
using Highlights.Core.Llm;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Signals;
using Highlights.Core.Stages.Transcription;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Stages.Analysis;

/// <summary>Asks the LLM to find highlight moments in the transcript + audio events timeline → moments.json.</summary>
public sealed class AnalyzeStage(
    StructuredLlm llm, GameProfileStore profiles, IOptions<AnalyzeOptions> options, IOptions<OpenRouterOptions> openRouter)
    : IPipelineStage
{
    private const string SystemTemplate = "analyze.system.md";
    private const string UserTemplate = "analyze.user.md";

    public string Name => StageNames.Analyze;
    public IReadOnlyList<string> DependsOn => [StageNames.Transcribe, StageNames.AudioSignals];
    public IReadOnlyList<string> Artifacts => [ProjectLayout.MomentsFile];

    public string DescribeInputs(HighlightsProject project)
    {
        var o = options.Value;
        var id = profiles.ResolveId(project);
        profiles.Load(id); // validates the profile
        return string.Join('|', id, FileHash(profiles.PathOf(id)), FileHash(TemplatePath(SystemTemplate)),
            FileHash(TemplatePath(UserTemplate)), o.OutputLanguage, o.MinLoudPeakDb, string.Join(',', openRouter.Value.EffectiveModels));
    }

    public async Task<IReadOnlyDictionary<string, string>> RunAsync(
        HighlightsProject project, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        var o = options.Value;
        progress.Report(new StageProgress(Name, null, "building prompt"));
        var (profileId, profile, duration, messages) = await BuildPromptAsync(project, cancellationToken);

        var result = await llm.CompleteAsync<LlmMomentsAnswer>(
            messages, BuildSchema(profile), answer => Validate(answer, profile, duration), progress, Name,
            (model, usage) => project.LlmUsage.Add(new LlmUsageRecord(
                Name, model, usage.PromptTokens, usage.CompletionTokens, usage.CostUsd, DateTimeOffset.Now)),
            cancellationToken);

        var moments = result.Value.Moments
            .OrderBy(m => m.Start)
            .Select((m, i) => new Moment
            {
                Id = $"m{i + 1:000}",
                Start = Math.Round(m.Start, 1),
                End = Math.Round(m.End, 1),
                Category = m.Category,
                Title = m.Title.Trim(),
                Description = m.Description.Trim(),
                Quote = m.Quote.Trim(),
                Score = m.Score,
            })
            .ToList();

        await JsonDefaults.WriteAtomicAsync(project.PathOf(ProjectLayout.MomentsFile), new MomentsDocument
        {
            Model = result.Model,
            Profile = profileId,
            Language = o.OutputLanguage,
            Summary = result.Value.Summary.Trim(),
            Moments = moments,
        }, cancellationToken);

        progress.Report(new StageProgress(Name, 1, $"{moments.Count} moments"));
        return new Dictionary<string, string>
        {
            ["model"] = result.Model,
            ["profile"] = profileId,
            ["moments"] = moments.Count.ToString(CultureInfo.InvariantCulture),
            ["promptTokens"] = result.PromptTokens.ToString(CultureInfo.InvariantCulture),
            ["completionTokens"] = result.CompletionTokens.ToString(CultureInfo.InvariantCulture),
            ["costUsd"] = result.CostUsd?.ToString("0.####", CultureInfo.InvariantCulture) ?? "unknown",
        };
    }

    /// <summary>Builds the prompt (also used by the CLI's --dry-run to inspect it without calling the LLM).</summary>
    public async Task<(string ProfileId, GameProfile Profile, double Duration, IReadOnlyList<LlmMessage> Messages)> BuildPromptAsync(
        HighlightsProject project, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var profileId = profiles.ResolveId(project);
        var profile = profiles.Load(profileId);
        var transcript = await JsonDefaults.ReadAsync<Transcript>(project.PathOf(ProjectLayout.TranscriptFile), cancellationToken);
        var signals = await JsonDefaults.ReadAsync<AudioSignals>(project.PathOf(ProjectLayout.SignalsFile), cancellationToken);
        var duration = Math.Max(transcript.DurationSeconds, signals.DurationSeconds);

        var timeline = TimelineBuilder.Build(transcript, signals, o.MinLoudPeakDb);
        var values = new Dictionary<string, string>
        {
            ["game_name"] = profile.Name,
            ["game_context"] = profile.Context,
            ["categories"] = string.Join('\n', profile.Categories.Select(c => $"- {c.Key}: {c.Value}")),
            ["speech_language"] = transcript.DetectedLanguage ?? transcript.Language,
            ["output_language"] = o.OutputLanguage,
            ["max_moment_seconds"] = profile.MaxMomentSeconds.ToString(CultureInfo.InvariantCulture),
            ["expected_per_hour"] = profile.ExpectedMomentsPerHour.ToString(CultureInfo.InvariantCulture),
            ["extra_instructions"] = profile.ExtraInstructions,
            ["duration_seconds"] = duration.ToString("0", CultureInfo.InvariantCulture),
            ["timeline"] = timeline,
        };
        IReadOnlyList<LlmMessage> messages =
        [
            LlmMessage.System(Render(await File.ReadAllTextAsync(TemplatePath(SystemTemplate), cancellationToken), values)),
            LlmMessage.User(Render(await File.ReadAllTextAsync(TemplatePath(UserTemplate), cancellationToken), values)),
        ];
        return (profileId, profile, duration, messages);
    }

    internal static string? Validate(LlmMomentsAnswer answer, GameProfile profile, double duration)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(answer.Summary))
            errors.Add("summary is empty");

        for (var i = 0; i < answer.Moments.Count && errors.Count < 15; i++)
        {
            var m = answer.Moments[i];
            var at = $"moments[{i}] ({m.Start:0.#}s \"{m.Title}\")";
            if (m.Start < 0 || m.End > duration + 1)
                errors.Add($"{at}: times must be within 0..{duration:0} s");
            if (m.End <= m.Start)
                errors.Add($"{at}: end must be greater than start");
            else if (m.End - m.Start > profile.MaxMomentSeconds * 1.5)
                errors.Add($"{at}: {m.End - m.Start:0} s is too long (max {profile.MaxMomentSeconds} s); split it or tighten it");
            if (!profile.Categories.ContainsKey(m.Category))
                errors.Add($"{at}: unknown category '{m.Category}' (allowed: {string.Join(", ", profile.Categories.Keys)})");
            if (m.Score is < 0 or > 10)
                errors.Add($"{at}: score must be 0..10");
            if (string.IsNullOrWhiteSpace(m.Title) || m.Title.Length > 80)
                errors.Add($"{at}: title must be 1..60 characters");
        }

        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    internal static LlmJsonSchema BuildSchema(GameProfile profile)
    {
        static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

        var moment = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("start", "end", "category", "title", "description", "quote", "score"),
            ["properties"] = new JsonObject
            {
                ["start"] = Prop("number", "Start time in seconds (from the timeline)"),
                ["end"] = Prop("number", "End time in seconds"),
                ["category"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(profile.Categories.Keys.Select(k => (JsonNode)k).ToArray()),
                },
                ["title"] = Prop("string", "Short catchy title for the on-screen caption and YouTube chapter"),
                ["description"] = Prop("string", "1-2 sentences: what happens and why it is good"),
                ["quote"] = Prop("string", "Key line in the original language"),
                ["score"] = Prop("integer", "0-10, how strongly the moment deserves to be in the video"),
            },
        };

        return new LlmJsonSchema("highlight_moments", new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("summary", "moments"),
            ["properties"] = new JsonObject
            {
                ["summary"] = Prop("string", "2-4 sentence summary of the whole session"),
                ["moments"] = new JsonObject { ["type"] = "array", ["items"] = moment },
            },
        });
    }

    private static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        var sb = new StringBuilder(template);
        foreach (var (key, value) in values)
            sb.Replace("{{" + key + "}}", value);
        return sb.ToString();
    }

    private string TemplatePath(string name)
    {
        var path = Path.Combine(options.Value.ResolveDirectory(options.Value.PromptsDirectory), name);
        return File.Exists(path) ? path : throw new PipelineException($"Prompt template not found: {path}");
    }

    private static string FileHash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))[..12];
}
