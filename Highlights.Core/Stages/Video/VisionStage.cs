using System.Globalization;
using System.Text.Json.Nodes;
using Highlights.Core.Configuration;
using Highlights.Core.Llm;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Stages.Video;

/// <summary>Contents of vision.json: what a vision model saw on screen, independent of what was said.</summary>
public sealed record VisionDocument
{
    public required bool Enabled { get; init; }
    public string? Model { get; init; }
    public int IntervalSeconds { get; init; }
    public IReadOnlyList<VisionWindow> Windows { get; init; } = [];
    public IReadOnlyList<VisionEvent> Events { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>What happens on screen during [Start, End) in one or two sentences.</summary>
public sealed record VisionWindow(double Start, double End, string Summary);

/// <summary>A notable on-screen event. <see cref="Type"/> is one of <see cref="VisionStage.EventTypes"/>.</summary>
public sealed record VisionEvent(double Time, string Type, string Description, int Importance);

/// <summary>
/// Watches the whole video through sparse frames (every few seconds) with a vision LLM: kills, deaths, rounds,
/// discoveries, fails — so moments without talk are found and clips can start where the action starts.
/// </summary>
public sealed class VisionStage(
    StructuredLlm llm, PromptTemplates templates, IOptions<VisionOptions> options, IOptions<AnalyzeOptions> analyze)
    : IPipelineStage
{
    public static readonly string[] EventTypes =
        ["player_kill", "player_death", "teammate_action", "round_or_match_end", "objective", "discovery", "fail", "funny_visual", "other"];

    private const string SystemTemplate = "vision.system.md";

    public string Name => StageNames.Vision;
    public IReadOnlyList<string> DependsOn => [StageNames.Frames];
    public IReadOnlyList<string> Artifacts => [ProjectLayout.VisionFile];

    public string DescribeInputs(HighlightsProject project)
    {
        var o = options.Value;
        return o.Enabled
            ? string.Join('|', o.OverviewIntervalSeconds, o.OverviewBatchFrames, o.OverviewLowDetail, string.Join(',', o.EffectiveModels),
                templates.HashOf(SystemTemplate), analyze.Value.OutputLanguage)
            : "disabled";
    }

    public async Task<IReadOnlyDictionary<string, string>> RunAsync(
        HighlightsProject project, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            await JsonDefaults.WriteAtomicAsync(project.PathOf(ProjectLayout.VisionFile), new VisionDocument { Enabled = false }, cancellationToken);
            return new Dictionary<string, string> { ["enabled"] = "false" };
        }

        var motion = await JsonDefaults.ReadAsync<MotionTrack>(project.PathOf(ProjectLayout.MotionFile), cancellationToken);
        var times = Enumerable.Range(0, motion.FrameCount / o.OverviewIntervalSeconds + 1)
            .Select(i => (double)(i * o.OverviewIntervalSeconds))
            .Where(t => FrameStore.PathAt(project, t) is not null)
            .ToList();
        var batches = times.Chunk(o.OverviewBatchFrames).ToList();
        var system = await templates.RenderAsync(SystemTemplate, new Dictionary<string, string>
        {
            ["output_language"] = analyze.Value.OutputLanguage,
            ["interval"] = o.OverviewIntervalSeconds.ToString(CultureInfo.InvariantCulture),
        }, cancellationToken);

        var usageLock = new Lock();
        var done = 0;
        var results = new (VisionWindow Window, List<VisionEvent> Events, string Model)[batches.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, o.MaxParallelRequests));

        progress.Report(new StageProgress(Name, 0, $"watching {times.Count} frames in {batches.Count} parts"));
        await Task.WhenAll(batches.Select(async (batch, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var (from, to) = (batch[0], batch[^1] + o.OverviewIntervalSeconds);
                var parts = new List<LlmContentPart>
                {
                    LlmContentPart.FromText($"Frames from {from:0} s to {to:0} s of the recording, one every {o.OverviewIntervalSeconds} s:"),
                };
                foreach (var t in batch)
                {
                    parts.Add(LlmContentPart.FromText($"t={t:0}s"));
                    parts.Add(LlmContentPart.FromJpeg(await File.ReadAllBytesAsync(FrameStore.PathAt(project, t)!, cancellationToken), o.OverviewLowDetail));
                }

                var result = await llm.CompleteAsync<LlmVisionAnswer>(
                    [LlmMessage.System(system), LlmMessage.UserParts(parts)], Schema, a => Validate(a, from, to),
                    new Progress<StageProgress>(_ => { }), Name,
                    (model, usage) =>
                    {
                        lock (usageLock)
                            project.LlmUsage.Add(new LlmUsageRecord(Name, model, usage.PromptTokens, usage.CompletionTokens, usage.CostUsd, DateTimeOffset.Now));
                    },
                    cancellationToken, o.EffectiveModels);

                results[index] = (new VisionWindow(from, to, result.Value.Summary.Trim()),
                    result.Value.Events.Select(e => new VisionEvent(Math.Round(e.Time, 1), e.Type, e.Description.Trim(), e.Importance)).ToList(),
                    result.Model);
                var n = Interlocked.Increment(ref done);
                progress.Report(new StageProgress(Name, (double)n / batches.Count, $"watched {n}/{batches.Count} parts"));
            }
            finally
            {
                gate.Release();
            }
        }));

        var events = results.SelectMany(r => r.Events).OrderBy(e => e.Time).ToList();
        await JsonDefaults.WriteAtomicAsync(project.PathOf(ProjectLayout.VisionFile), new VisionDocument
        {
            Enabled = true,
            Model = results.Select(r => r.Model).FirstOrDefault(),
            IntervalSeconds = o.OverviewIntervalSeconds,
            Windows = results.Select(r => r.Window).ToList(),
            Events = events,
        }, cancellationToken);

        var cost = project.LlmUsage.Where(u => u.Stage == Name).TakeLast(batches.Count).Sum(u => u.CostUsd ?? 0);
        progress.Report(new StageProgress(Name, 1, $"{events.Count} on-screen events"));
        return new Dictionary<string, string>
        {
            ["events"] = events.Count.ToString(CultureInfo.InvariantCulture),
            ["frames"] = times.Count.ToString(CultureInfo.InvariantCulture),
            ["model"] = results.Select(r => r.Model).FirstOrDefault() ?? "",
            ["costUsd"] = cost.ToString("0.####", CultureInfo.InvariantCulture),
        };
    }

    private static string? Validate(LlmVisionAnswer answer, double from, double to)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(answer.Summary))
            errors.Add("summary is empty");
        foreach (var e in answer.Events.Take(40))
        {
            if (e.Time < from - 1 || e.Time > to + 1)
                errors.Add($"event at {e.Time:0.#}s is outside the frames ({from:0}–{to:0} s)");
            if (!EventTypes.Contains(e.Type))
                errors.Add($"unknown event type '{e.Type}'");
            if (e.Importance is < 0 or > 10)
                errors.Add("importance must be 0..10");
        }
        return errors.Count == 0 ? null : string.Join("; ", errors.Distinct().Take(10));
    }

    private static readonly LlmJsonSchema Schema = new("screen_events", new JsonObject
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("summary", "events"),
        ["properties"] = new JsonObject
        {
            ["summary"] = new JsonObject { ["type"] = "string", ["description"] = "1-3 sentences: what happens on screen in these frames" },
            ["events"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("time", "type", "description", "importance"),
                    ["properties"] = new JsonObject
                    {
                        ["time"] = new JsonObject { ["type"] = "number", ["description"] = "Seconds, from the frame labels" },
                        ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(EventTypes.Select(t => (JsonNode)t).ToArray()) },
                        ["description"] = new JsonObject { ["type"] = "string" },
                        ["importance"] = new JsonObject { ["type"] = "integer", ["description"] = "0-10 how notable for a highlights video" },
                    },
                },
            },
        },
    });

    internal sealed record LlmVisionAnswer(string Summary, List<LlmVisionEvent> Events);

    internal sealed record LlmVisionEvent(double Time, string Type, string Description, int Importance);
}
