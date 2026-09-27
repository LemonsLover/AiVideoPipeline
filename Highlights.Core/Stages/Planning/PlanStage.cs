using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Highlights.Core.Configuration;
using Highlights.Core.Llm;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Analysis;
using Highlights.Core.Stages.Signals;
using Highlights.Core.Stages.Transcription;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Stages.Planning;

/// <summary>
/// The LLM editor: from the beat sheet and the full timeline, cuts a video in the chosen editing mode —
/// clips with in/out points, inner cuts, context captions and titles → plan.json.
/// </summary>
public sealed class PlanStage(
    StructuredLlm llm, EditModeStore modes, PromptTemplates templates, IOptions<AnalyzeOptions> options,
    IOptions<OpenRouterOptions> openRouter) : IPipelineStage
{
    private const string SystemTemplate = "plan.system.md";
    private const string UserTemplate = "plan.user.md";

    /// <summary>Accepted deviation of the planned length from the target before asking the model to fix it.</summary>
    private const double MinTargetRatio = 0.7, MaxTargetRatio = 1.35;

    public string Name => StageNames.Plan;
    public IReadOnlyList<string> DependsOn => [StageNames.Analyze];
    public IReadOnlyList<string> Artifacts => [ProjectLayout.PlanFile];

    public string DescribeInputs(HighlightsProject project)
    {
        var id = modes.ResolveId(project);
        var mode = modes.Load(id);
        return string.Join('|', id, PromptTemplates.FileHash(modes.PathOf(id)), project.Settings.TargetMinutes,
            Captions(project, mode), templates.HashOf(SystemTemplate), templates.HashOf(UserTemplate),
            options.Value.OutputLanguage, string.Join(',', openRouter.Value.EffectiveModels));
    }

    public async Task<IReadOnlyDictionary<string, string>> RunAsync(
        HighlightsProject project, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new StageProgress(Name, null, "building prompt"));
        var prompt = await BuildPromptAsync(project, cancellationToken);
        var (modeId, mode, captions, target, duration, beats) = prompt.Context;

        var result = await llm.CompleteAsync<LlmPlanAnswer>(
            prompt.Messages, Schema, answer => Validate(answer, beats, mode, target, duration), progress, Name,
            (model, usage) => project.LlmUsage.Add(new LlmUsageRecord(
                Name, model, usage.PromptTokens, usage.CompletionTokens, usage.CostUsd, DateTimeOffset.Now)),
            cancellationToken);

        var clips = result.Value.Clips
            .OrderBy(c => c.Start)
            .Select((c, i) => new PlannedClip
            {
                Id = $"p{i + 1:00}",
                MomentIds = c.BeatIds.Where(beats.Contains).Distinct().ToList(),
                Start = Math.Round(c.Start, 2),
                End = Math.Round(c.End, 2),
                Cuts = c.Cuts.Select(x => new TimeRange(Math.Round(x.Start, 2), Math.Round(x.End, 2)))
                    .Where(x => x.Start > c.Start && x.End < c.End && x.Duration > 0)
                    .OrderBy(x => x.Start).ToList(),
                Caption = captions && !string.IsNullOrWhiteSpace(c.Caption) ? c.Caption.Trim() : null,
                Title = c.Title.Trim(),
            })
            .ToList();

        var planned = Math.Round(clips.Sum(c => c.KeptSeconds), 1);
        await JsonDefaults.WriteAtomicAsync(project.PathOf(ProjectLayout.PlanFile), new PlanDocument
        {
            Mode = modeId,
            ModeName = mode.Name,
            Model = result.Model,
            TargetMinutes = target,
            ContextCaptions = captions,
            PlannedSeconds = planned,
            Clips = clips,
        }, cancellationToken);

        progress.Report(new StageProgress(Name, 1, $"{clips.Count} clips, {TimeSpan.FromSeconds(planned):m\\:ss} planned"));
        return new Dictionary<string, string>
        {
            ["mode"] = mode.Name,
            ["model"] = result.Model,
            ["clips"] = clips.Count.ToString(CultureInfo.InvariantCulture),
            ["plannedSeconds"] = planned.ToString(CultureInfo.InvariantCulture),
            ["targetMinutes"] = target.ToString(CultureInfo.InvariantCulture),
            ["captions"] = clips.Count(c => c.Caption is not null).ToString(CultureInfo.InvariantCulture),
            ["costUsd"] = result.CostUsd?.ToString("0.####", CultureInfo.InvariantCulture) ?? "unknown",
        };
    }

    public sealed record PromptContext(
        string ModeId, EditMode Mode, bool Captions, double TargetMinutes, double Duration, IReadOnlySet<string> BeatIds);

    public sealed record Prompt(IReadOnlyList<LlmMessage> Messages, PromptContext Context);

    /// <summary>Builds the prompt (also used by the CLI's --dry-run).</summary>
    public async Task<Prompt> BuildPromptAsync(HighlightsProject project, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var modeId = modes.ResolveId(project);
        var mode = modes.Load(modeId);
        var captions = Captions(project, mode);
        var moments = await JsonDefaults.ReadAsync<MomentsDocument>(project.PathOf(ProjectLayout.MomentsFile), cancellationToken);
        var transcript = await JsonDefaults.ReadAsync<Transcript>(project.PathOf(ProjectLayout.TranscriptFile), cancellationToken);
        var signals = await JsonDefaults.ReadAsync<AudioSignals>(project.PathOf(ProjectLayout.SignalsFile), cancellationToken);
        var duration = Math.Max(transcript.DurationSeconds, signals.DurationSeconds);
        var target = mode.TargetMinutes(duration, project.Settings.TargetMinutes);

        var values = new Dictionary<string, string>
        {
            ["game"] = string.IsNullOrWhiteSpace(moments.Game) ? "unknown" : moments.Game,
            ["mode_name"] = mode.Name,
            ["mode_instructions"] = mode.Instructions,
            ["target_minutes"] = target.ToString("0.#", CultureInfo.InvariantCulture),
            ["max_clip_seconds"] = mode.MaxClipSeconds.ToString(CultureInfo.InvariantCulture),
            ["captions_rule"] = captions
                ? "Captions are enabled: use them whenever the viewer needs context, but keep most clips caption-free."
                : "Captions are disabled for this video: always return an empty caption and give context with setup footage instead.",
            ["output_language"] = o.OutputLanguage,
            ["speech_language"] = transcript.DetectedLanguage ?? transcript.Language,
            ["duration_seconds"] = duration.ToString("0", CultureInfo.InvariantCulture),
            ["summary"] = moments.Summary,
            ["beats"] = FormatBeats(moments.Moments),
            ["timeline"] = TimelineBuilder.Build(transcript, signals, o.MinLoudPeakDb),
        };
        IReadOnlyList<LlmMessage> messages =
        [
            LlmMessage.System(await templates.RenderAsync(SystemTemplate, values, cancellationToken)),
            LlmMessage.User(await templates.RenderAsync(UserTemplate, values, cancellationToken)),
        ];
        return new Prompt(messages, new PromptContext(modeId, mode, captions, target, duration,
            moments.Moments.Select(m => m.Id).ToHashSet()));
    }

    private static bool Captions(HighlightsProject project, EditMode mode) => project.Settings.ContextCaptions ?? mode.ContextCaptions;

    private static string FormatBeats(IReadOnlyList<Moment> moments)
    {
        static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        foreach (var m in moments)
        {
            sb.Append(m.Id).Append(" | ").Append(F(m.Start)).Append('–').Append(F(m.End));
            if (m.EffectiveSetupStart < m.Start)
                sb.Append(" (setup from ").Append(F(m.EffectiveSetupStart)).Append(')');
            sb.Append(CultureInfo.InvariantCulture, $" | {m.Category} | fun {m.Score} | story {m.StoryImportance} | {m.Title} — {m.Description}");
            if (m.ContextNote is { Length: > 0 } note)
                sb.Append(" [context: ").Append(note).Append(']');
            sb.AppendLine();
        }
        return sb.ToString();
    }

    internal static string? Validate(LlmPlanAnswer answer, IReadOnlySet<string> beats, EditMode mode, double targetMinutes, double duration)
    {
        var errors = new List<string>();
        if (answer.Clips.Count == 0)
            return "no clips: the video needs at least one clip";

        var ordered = answer.Clips.OrderBy(c => c.Start).ToList();
        for (var i = 0; i < ordered.Count && errors.Count < 15; i++)
        {
            var c = ordered[i];
            var at = $"clip at {c.Start:0.#}s \"{c.Title}\"";
            if (c.Start < 0 || c.End > duration + 1 || c.End <= c.Start)
            {
                errors.Add($"{at}: start/end must satisfy 0 <= start < end <= {duration:0}");
                continue;
            }
            if (i > 0 && c.Start < ordered[i - 1].End - 0.5)
                errors.Add($"{at}: overlaps the previous clip (ends at {ordered[i - 1].End:0.#}s); merge them or move the in point");

            var cuts = c.Cuts.OrderBy(x => x.Start).ToList();
            for (var k = 0; k < cuts.Count; k++)
            {
                if (cuts[k].Start <= c.Start || cuts[k].End >= c.End || cuts[k].End <= cuts[k].Start)
                    errors.Add($"{at}: cut {cuts[k].Start:0.#}–{cuts[k].End:0.#} must lie strictly inside the clip");
                else if (k > 0 && cuts[k].Start < cuts[k - 1].End)
                    errors.Add($"{at}: cuts overlap");
            }

            var kept = c.End - c.Start - cuts.Sum(x => Math.Max(0, x.End - x.Start));
            if (kept < 3)
                errors.Add($"{at}: less than 3 s left after cuts");
            else if (kept > mode.MaxClipSeconds * 1.3)
                errors.Add($"{at}: {kept:0} s after cuts is too long (max {mode.MaxClipSeconds} s); tighten it with cuts or split it");

            var unknown = c.BeatIds.Where(id => !beats.Contains(id)).ToList();
            if (unknown.Count > 0)
                errors.Add($"{at}: unknown beat ids {string.Join(", ", unknown)}");
            if (string.IsNullOrWhiteSpace(c.Title) || c.Title.Length > 80)
                errors.Add($"{at}: title must be 1..60 characters");
            if (c.Caption.Length > 110)
                errors.Add($"{at}: caption is too long (max ~90 characters)");
        }

        var totalMinutes = ordered.Sum(c => Math.Max(0, c.End - c.Start - c.Cuts.Sum(x => Math.Max(0, x.End - x.Start)))) / 60;
        if (totalMinutes < targetMinutes * MinTargetRatio)
            errors.Add($"the cut is {totalMinutes:0.#} min but the target is {targetMinutes:0.#} min: add clips or keep more of them");
        else if (totalMinutes > targetMinutes * MaxTargetRatio)
            errors.Add($"the cut is {totalMinutes:0.#} min but the target is {targetMinutes:0.#} min: drop the weakest clips or cut more inside clips");

        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    private static readonly LlmJsonSchema Schema = BuildSchema();

    private static LlmJsonSchema BuildSchema()
    {
        static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

        var cut = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("start", "end"),
            ["properties"] = new JsonObject { ["start"] = Prop("number", "seconds"), ["end"] = Prop("number", "seconds") },
        };
        var clip = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("beatIds", "start", "end", "cuts", "caption", "title"),
            ["properties"] = new JsonObject
            {
                ["beatIds"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["start"] = Prop("number", "In point in source seconds"),
                ["end"] = Prop("number", "Out point in source seconds"),
                ["cuts"] = new JsonObject { ["type"] = "array", ["items"] = cut, ["description"] = "Ranges inside the clip to remove" },
                ["caption"] = Prop("string", "Context line shown at the clip start, or empty"),
                ["title"] = Prop("string", "Chapter title"),
            },
        };
        return new LlmJsonSchema("video_plan", new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("clips"),
            ["properties"] = new JsonObject { ["clips"] = new JsonObject { ["type"] = "array", ["items"] = clip } },
        });
    }
}
