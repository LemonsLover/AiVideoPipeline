using System.Globalization;
using System.Text.Json.Nodes;
using Highlights.Core.Configuration;
using Highlights.Core.Llm;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Signals;
using Highlights.Core.Stages.Transcription;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Stages.Analysis;

/// <summary>
/// Asks the LLM to map the whole session into beats (transcript + audio events timeline) → moments.json.
/// Independent of the editing mode, so switching modes only re-runs the plan.
/// </summary>
public sealed class AnalyzeStage(
    StructuredLlm llm, PromptTemplates templates, IOptionsMonitor<AnalyzeOptions> options, IOptionsMonitor<OpenRouterOptions> openRouter)
    : IPipelineStage
{
    public static readonly string[] Kinds = ["funny", "reaction", "epic", "story", "banter"];

    private const string SystemTemplate = "analyze.system.md";
    private const string UserTemplate = "analyze.user.md";
    private const int MaxBeatSeconds = 150;

    public string Name => StageNames.Analyze;
    public IReadOnlyList<string> DependsOn => [StageNames.Transcribe, StageNames.AudioSignals, StageNames.Vision];
    public IReadOnlyList<string> Artifacts => [ProjectLayout.MomentsFile];

    public string DescribeInputs(HighlightsProject project)
    {
        var o = options.CurrentValue;
        return string.Join('|', templates.HashOf(SystemTemplate), templates.HashOf(UserTemplate), o.OutputLanguage,
            o.MinLoudPeakDb, string.Join(',', openRouter.CurrentValue.ModelsFor(Name)), openRouter.CurrentValue.For(Name).Temperature ?? openRouter.CurrentValue.Temperature,
            openRouter.CurrentValue.For(Name).MaxOutputTokens);
    }

    public async Task<IReadOnlyDictionary<string, string>> RunAsync(
        HighlightsProject project, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new StageProgress(Name, null, "building prompt"));
        var (duration, messages) = await BuildPromptAsync(project, cancellationToken);

        var result = await llm.CompleteAsync<LlmBeatsAnswer>(
            messages, Schema, answer => Validate(answer, duration), progress, Name,
            (model, usage) => project.LlmUsage.Add(new LlmUsageRecord(
                Name, model, usage.PromptTokens, usage.CompletionTokens, usage.CostUsd, DateTimeOffset.Now)),
            cancellationToken);

        var moments = result.Value.Beats
            .OrderBy(b => b.Start)
            .Select((b, i) => new Moment
            {
                Id = $"m{i + 1:000}",
                SetupStart = Math.Round(Math.Min(b.SetupStart, b.Start), 1),
                Start = Math.Round(b.Start, 1),
                End = Math.Round(b.End, 1),
                Category = b.Kind,
                Title = b.Title.Trim(),
                Description = b.Description.Trim(),
                Quote = b.Quote.Trim(),
                Score = b.Score,
                StoryImportance = b.StoryImportance,
                ContextNote = string.IsNullOrWhiteSpace(b.ContextNote) ? null : b.ContextNote.Trim(),
            })
            .ToList();

        await JsonDefaults.WriteAtomicAsync(project.PathOf(ProjectLayout.MomentsFile), new MomentsDocument
        {
            Model = result.Model,
            Game = result.Value.Game.Trim(),
            Language = options.CurrentValue.OutputLanguage,
            Summary = result.Value.Summary.Trim(),
            Moments = moments,
        }, cancellationToken);

        progress.Report(new StageProgress(Name, 1, $"{moments.Count} beats"));
        return new Dictionary<string, string>
        {
            ["model"] = result.Model,
            ["game"] = result.Value.Game.Trim(),
            ["moments"] = moments.Count.ToString(CultureInfo.InvariantCulture),
            ["promptTokens"] = result.PromptTokens.ToString(CultureInfo.InvariantCulture),
            ["completionTokens"] = result.CompletionTokens.ToString(CultureInfo.InvariantCulture),
            ["costUsd"] = result.CostUsd?.ToString("0.####", CultureInfo.InvariantCulture) ?? "unknown",
        };
    }

    /// <summary>Builds the prompt (also used by the CLI's --dry-run to inspect it without calling the LLM).</summary>
    public async Task<(double Duration, IReadOnlyList<LlmMessage> Messages)> BuildPromptAsync(
        HighlightsProject project, CancellationToken cancellationToken)
    {
        var o = options.CurrentValue;
        var transcript = await JsonDefaults.ReadAsync<Transcript>(project.PathOf(ProjectLayout.TranscriptFile), cancellationToken);
        var signals = await JsonDefaults.ReadAsync<AudioSignals>(project.PathOf(ProjectLayout.SignalsFile), cancellationToken);
        var duration = Math.Max(transcript.DurationSeconds, signals.DurationSeconds);

        var values = new Dictionary<string, string>
        {
            ["speech_language"] = transcript.DetectedLanguage ?? transcript.Language,
            ["output_language"] = o.OutputLanguage,
            ["max_beat_seconds"] = MaxBeatSeconds.ToString(CultureInfo.InvariantCulture),
            ["duration_seconds"] = duration.ToString("0", CultureInfo.InvariantCulture),
            ["timeline"] = TimelineBuilder.Build(transcript, signals, o.MinLoudPeakDb,
                await TimelineBuilder.LoadVisionAsync(project, cancellationToken)),
        };
        IReadOnlyList<LlmMessage> messages =
        [
            LlmMessage.System(await templates.RenderAsync(SystemTemplate, values, cancellationToken)),
            LlmMessage.User(await templates.RenderAsync(UserTemplate, values, cancellationToken)),
        ];
        return (duration, messages);
    }

    internal static string? Validate(LlmBeatsAnswer answer, double duration)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(answer.Summary))
            errors.Add("summary is empty");
        if (answer.Beats.Count == 0)
            errors.Add("no beats: map the session even if it is quiet");

        for (var i = 0; i < answer.Beats.Count && errors.Count < 15; i++)
        {
            var b = answer.Beats[i];
            var at = $"beats[{i}] ({b.Start:0.#}s \"{b.Title}\")";
            if (b.Start < 0 || b.End > duration + 1 || b.SetupStart < 0)
                errors.Add($"{at}: times must be within 0..{duration:0} s");
            if (b.End <= b.Start)
                errors.Add($"{at}: end must be greater than start");
            else if (b.End - b.Start > MaxBeatSeconds * 1.3)
                errors.Add($"{at}: {b.End - b.Start:0} s is too long (max {MaxBeatSeconds} s); split it into several beats");
            if (b.SetupStart > b.Start)
                errors.Add($"{at}: setupStart must be <= start");
            if (!Kinds.Contains(b.Kind))
                errors.Add($"{at}: unknown kind '{b.Kind}' (allowed: {string.Join(", ", Kinds)})");
            if (b.Score is < 0 or > 10 || b.StoryImportance is < 0 or > 10)
                errors.Add($"{at}: score and storyImportance must be 0..10");
            if (string.IsNullOrWhiteSpace(b.Title) || b.Title.Length > 80)
                errors.Add($"{at}: title must be 1..60 characters");
        }

        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    private static readonly LlmJsonSchema Schema = BuildSchema();

    private static LlmJsonSchema BuildSchema()
    {
        static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

        var beat = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("setupStart", "start", "end", "kind", "title", "description", "quote", "score",
                "storyImportance", "contextNote"),
            ["properties"] = new JsonObject
            {
                ["setupStart"] = Prop("number", "Earliest time (s) a viewer must see to understand the beat; <= start"),
                ["start"] = Prop("number", "Start of the beat itself (s)"),
                ["end"] = Prop("number", "End of the beat including the reaction (s)"),
                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Kinds.Select(k => (JsonNode)k).ToArray()) },
                ["title"] = Prop("string", "Short catchy title"),
                ["description"] = Prop("string", "1-2 sentences: what happens"),
                ["quote"] = Prop("string", "Key line in the original language"),
                ["score"] = Prop("integer", "0-10 entertainment value on its own"),
                ["storyImportance"] = Prop("integer", "0-10 how much the session's story needs this beat"),
                ["contextNote"] = Prop("string", "What a viewer must know that isn't shown in the beat; empty if nothing"),
            },
        };

        return new LlmJsonSchema("session_beats", new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("game", "summary", "beats"),
            ["properties"] = new JsonObject
            {
                ["game"] = Prop("string", "The game being played, as recognized from the conversation"),
                ["summary"] = Prop("string", "3-5 sentence summary of the whole session"),
                ["beats"] = new JsonObject { ["type"] = "array", ["items"] = beat },
            },
        });
    }
}
