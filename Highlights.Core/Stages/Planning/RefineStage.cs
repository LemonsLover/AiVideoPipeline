using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Highlights.Core.Configuration;
using Highlights.Core.Llm;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Analysis;
using Highlights.Core.Stages.Transcription;
using Highlights.Core.Stages.Video;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Stages.Planning;

/// <summary>Contents of refine.json: in/out points of planned clips corrected by looking at the frames.</summary>
public sealed record RefineDocument
{
    public required bool Enabled { get; init; }
    public IReadOnlyDictionary<string, RefinedEdges> Clips { get; init; } = new Dictionary<string, RefinedEdges>();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record RefinedEdges(double Start, double End, string Reason);

/// <summary>
/// For every planned clip, a vision model looks at the seconds before the in point and around the out point and moves
/// them so the on-screen action is visible — not only the reaction to it.
/// </summary>
public sealed class RefineStage(
    StructuredLlm llm, PromptTemplates templates, IOptions<VisionOptions> options) : IPipelineStage
{
    private const string SystemTemplate = "refine.system.md";
    private const int LookAfterStartSeconds = 5, LookBeforeEndSeconds = 4;

    public string Name => StageNames.Refine;
    public IReadOnlyList<string> DependsOn => [StageNames.Plan, StageNames.Frames];
    public IReadOnlyList<string> Artifacts => [ProjectLayout.RefineFile];

    public string DescribeInputs(HighlightsProject project)
    {
        var o = options.Value;
        return o.Enabled
            ? string.Join('|', o.RefineLeadInSeconds, o.RefineTailSeconds, string.Join(',', o.EffectiveModels), templates.HashOf(SystemTemplate))
            : "disabled";
    }

    public async Task<IReadOnlyDictionary<string, string>> RunAsync(
        HighlightsProject project, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            await JsonDefaults.WriteAtomicAsync(project.PathOf(ProjectLayout.RefineFile), new RefineDocument { Enabled = false }, cancellationToken);
            return new Dictionary<string, string> { ["enabled"] = "false" };
        }

        var plan = await JsonDefaults.ReadAsync<PlanDocument>(project.PathOf(ProjectLayout.PlanFile), cancellationToken);
        var moments = (await JsonDefaults.ReadAsync<MomentsDocument>(project.PathOf(ProjectLayout.MomentsFile), cancellationToken))
            .Moments.ToDictionary(m => m.Id);
        var transcript = await JsonDefaults.ReadAsync<Transcript>(project.PathOf(ProjectLayout.TranscriptFile), cancellationToken);
        var duration = project.Media?.DurationSeconds ?? transcript.DurationSeconds;
        var system = await templates.RenderAsync(SystemTemplate, new Dictionary<string, string>
        {
            ["lead_in"] = o.RefineLeadInSeconds.ToString(CultureInfo.InvariantCulture),
            ["tail"] = o.RefineTailSeconds.ToString(CultureInfo.InvariantCulture),
        }, cancellationToken);

        var usageLock = new Lock();
        var refined = new Dictionary<string, RefinedEdges>();
        var done = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, o.MaxParallelRequests));

        await Task.WhenAll(plan.Clips.Select(async clip =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var minStart = Math.Max(0, clip.Start - o.RefineLeadInSeconds);
                var maxEnd = Math.Min(duration, clip.End + o.RefineTailSeconds);
                var parts = new List<LlmContentPart> { LlmContentPart.FromText(Describe(clip, moments, transcript, minStart)) };
                await AddFramesAsync(project, parts, "Frames before and at the planned in point", minStart, Math.Min(clip.End, clip.Start + LookAfterStartSeconds), cancellationToken);
                await AddFramesAsync(project, parts, "Frames around the planned out point", Math.Max(clip.Start, clip.End - LookBeforeEndSeconds), maxEnd, cancellationToken);

                var result = await llm.CompleteAsync<LlmRefineAnswer>(
                    [LlmMessage.System(system), LlmMessage.UserParts(parts)], Schema,
                    a => Validate(a, clip, minStart, maxEnd), new Progress<StageProgress>(_ => { }), Name,
                    (model, usage) =>
                    {
                        lock (usageLock)
                            project.LlmUsage.Add(new LlmUsageRecord(Name, model, usage.PromptTokens, usage.CompletionTokens, usage.CostUsd, DateTimeOffset.Now));
                    },
                    cancellationToken, o.EffectiveModels);

                lock (usageLock)
                    refined[clip.Id] = new RefinedEdges(Math.Round(result.Value.Start, 1), Math.Round(result.Value.End, 1), result.Value.Reason.Trim());
                var n = Interlocked.Increment(ref done);
                progress.Report(new StageProgress(Name, (double)n / plan.Clips.Count, $"checked {n}/{plan.Clips.Count} clips"));
            }
            finally
            {
                gate.Release();
            }
        }));

        await JsonDefaults.WriteAtomicAsync(project.PathOf(ProjectLayout.RefineFile),
            new RefineDocument { Enabled = true, Clips = refined }, cancellationToken);

        var moved = plan.Clips.Count(c => refined.TryGetValue(c.Id, out var r) && (Math.Abs(r.Start - c.Start) >= 1 || Math.Abs(r.End - c.End) >= 1));
        var earlier = plan.Clips.Where(c => refined.TryGetValue(c.Id, out var r) && r.Start < c.Start - 1).Sum(c => c.Start - refined[c.Id].Start);
        progress.Report(new StageProgress(Name, 1, $"{moved} of {plan.Clips.Count} clips adjusted"));
        return new Dictionary<string, string>
        {
            ["clips"] = plan.Clips.Count.ToString(CultureInfo.InvariantCulture),
            ["adjusted"] = moved.ToString(CultureInfo.InvariantCulture),
            ["addedLeadInSeconds"] = Math.Round(earlier).ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>What the clip is about plus the lines spoken in the looked-at window, for alignment.</summary>
    private static string Describe(PlannedClip clip, IReadOnlyDictionary<string, Moment> moments, Transcript transcript, double from)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Planned clip \"{clip.Title}\": in point {clip.Start:0.0} s, out point {clip.End:0.0} s.\n");
        foreach (var id in clip.MomentIds)
            if (moments.TryGetValue(id, out var m))
                sb.Append(CultureInfo.InvariantCulture, $"- {m.Title}: {m.Description} (moment {m.Start:0.0}–{m.End:0.0} s)\n");
        var lines = transcript.Segments.Where(s => s.End > from && s.Start < clip.Start + LookAfterStartSeconds).ToList();
        if (lines.Count > 0)
        {
            sb.Append("Speech around the in point:\n");
            foreach (var s in lines)
                sb.Append(CultureInfo.InvariantCulture, $"{s.Start:0.0} | {s.Text}\n");
        }
        return sb.ToString();
    }

    private static async Task AddFramesAsync(HighlightsProject project, List<LlmContentPart> parts, string title, double from, double to,
        CancellationToken cancellationToken)
    {
        parts.Add(LlmContentPart.FromText($"{title} ({from:0}–{to:0} s, one per second):"));
        for (var t = Math.Ceiling(from); t <= to; t++)
        {
            if (FrameStore.PathAt(project, t) is not { } path)
                continue;
            parts.Add(LlmContentPart.FromText($"t={t:0}s"));
            parts.Add(LlmContentPart.FromJpeg(await File.ReadAllBytesAsync(path, cancellationToken)));
        }
    }

    private static string? Validate(LlmRefineAnswer a, PlannedClip clip, double minStart, double maxEnd)
    {
        var errors = new List<string>();
        if (a.Start < minStart - 0.5 || a.Start > clip.Start + LookAfterStartSeconds + 0.5)
            errors.Add($"start must be between {minStart:0} and {clip.Start + LookAfterStartSeconds:0} s");
        if (a.End < clip.End - LookBeforeEndSeconds - 0.5 || a.End > maxEnd + 0.5)
            errors.Add($"end must be between {clip.End - LookBeforeEndSeconds:0} and {maxEnd:0} s");
        if (a.End - a.Start < 3)
            errors.Add("the clip must stay at least 3 s long");
        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    private static readonly LlmJsonSchema Schema = new("refined_edges", new JsonObject
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("start", "end", "reason"),
        ["properties"] = new JsonObject
        {
            ["start"] = new JsonObject { ["type"] = "number", ["description"] = "New in point, seconds" },
            ["end"] = new JsonObject { ["type"] = "number", ["description"] = "New out point, seconds" },
            ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "What you saw, one short sentence" },
        },
    });

    internal sealed record LlmRefineAnswer(double Start, double End, string Reason);
}
