using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Highlights.Core.Configuration;
using Highlights.Core.Llm;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Analysis;
using Highlights.Core.Stages.Planning;
using Highlights.Core.Stages.Review;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Stages.Rendering;

/// <summary>
/// Writes youtube.txt: title options, description and tags from the LLM, plus chapters computed from the edit
/// (with crossfades, so they match highlights.mp4).
/// </summary>
public sealed class DescribeStage(
    StructuredLlm llm, EditModeStore modes, PromptTemplates templates, IOptionsMonitor<AnalyzeOptions> analyze,
    IOptionsMonitor<RenderOptions> render, IOptionsMonitor<OpenRouterOptions> openRouter) : IPipelineStage
{
    private const string SystemTemplate = "describe.system.md";
    private const string UserTemplate = "describe.user.md";

    /// <summary>YouTube only shows chapters when there are at least 3, each at least 10 s long.</summary>
    private const int MinChapters = 3;
    private const double MinChapterSeconds = 10;

    public string Name => StageNames.Describe;
    public IReadOnlyList<string> DependsOn => [StageNames.Review];
    public IReadOnlyList<string> Artifacts => [ProjectLayout.YouTubeFile];

    public string DescribeInputs(HighlightsProject project)
    {
        return string.Join('|', ReviewService.EditHash(project),
            PromptTemplates.FileHash(project.PathOf(ProjectLayout.MomentsFile)), modes.ResolveId(project),
            templates.HashOf(SystemTemplate), templates.HashOf(UserTemplate), analyze.CurrentValue.OutputLanguage,
            render.CurrentValue.CrossfadeSeconds, render.CurrentValue.InnerCrossfadeSeconds, string.Join(',', openRouter.CurrentValue.ModelsFor(Name)), openRouter.CurrentValue.For(Name).Temperature ?? openRouter.CurrentValue.Temperature,
            openRouter.CurrentValue.For(Name).MaxOutputTokens);
    }

    public async Task<IReadOnlyDictionary<string, string>> RunAsync(
        HighlightsProject project, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        var edit = await JsonDefaults.ReadAsync<EditDocument>(project.PathOf(ProjectLayout.EditFile), cancellationToken);
        var moments = await JsonDefaults.ReadAsync<MomentsDocument>(project.PathOf(ProjectLayout.MomentsFile), cancellationToken);
        var byId = moments.Moments.ToDictionary(m => m.Id);
        var mode = modes.Load(modes.ResolveId(project));
        var plan = RenderPlan.Create(edit.Clips, render.CurrentValue.CrossfadeSeconds, render.CurrentValue.InnerCrossfadeSeconds);

        var clipList = new StringBuilder();
        foreach (var clip in edit.Clips)
        {
            clipList.Append("- ").Append(clip.Title);
            if (clip.Caption is { Length: > 0 } caption)
                clipList.Append(" [").Append(caption).Append(']');
            foreach (var id in clip.MomentIds)
                if (byId.TryGetValue(id, out var m))
                    clipList.Append(" — ").Append(m.Description);
            clipList.AppendLine();
        }

        var values = new Dictionary<string, string>
        {
            ["game_name"] = string.IsNullOrWhiteSpace(moments.Game) ? "a video game" : moments.Game,
            ["mode_name"] = mode.Name,
            ["output_language"] = analyze.CurrentValue.OutputLanguage,
            ["session_summary"] = moments.Summary,
            ["clips"] = clipList.ToString(),
            ["duration"] = FormatTime(plan.TotalSeconds),
        };
        IReadOnlyList<LlmMessage> messages =
        [
            LlmMessage.System(await templates.RenderAsync(SystemTemplate, values, cancellationToken)),
            LlmMessage.User(await templates.RenderAsync(UserTemplate, values, cancellationToken)),
        ];

        var result = await llm.CompleteAsync<YouTubeAnswer>(messages, Schema, Validate, progress, Name,
            (model, usage) => project.LlmUsage.Add(new LlmUsageRecord(
                Name, model, usage.PromptTokens, usage.CompletionTokens, usage.CostUsd, DateTimeOffset.Now)),
            cancellationToken);

        var chapters = Chapters(plan);
        var text = Format(result.Value, chapters);
        await File.WriteAllTextAsync(project.PathOf(ProjectLayout.YouTubeFile), text, new UTF8Encoding(false), cancellationToken);

        progress.Report(new StageProgress(Name, 1, $"{chapters.Count} chapters"));
        return new Dictionary<string, string>
        {
            ["model"] = result.Model,
            ["chapters"] = chapters.Count.ToString(CultureInfo.InvariantCulture),
            ["costUsd"] = result.CostUsd?.ToString("0.####", CultureInfo.InvariantCulture) ?? "unknown",
        };
    }

    /// <summary>One chapter per clip; clips shorter than 10 s are folded into the previous chapter.</summary>
    internal static IReadOnlyList<(double Start, string Title)> Chapters(RenderPlan plan)
    {
        var chapters = new List<(double Start, string Title)>();
        var starts = plan.ClipStarts;
        for (var i = 0; i < plan.Clips.Count; i++)
        {
            var end = i + 1 < plan.Clips.Count ? starts[i + 1] : plan.TotalSeconds;
            if (chapters.Count > 0 && end - starts[i] < MinChapterSeconds)
                continue;
            chapters.Add((chapters.Count == 0 ? 0 : starts[i], plan.Clips[i].Title));
        }
        return chapters.Count >= MinChapters ? chapters : [];
    }

    private static string Format(YouTubeAnswer answer, IReadOnlyList<(double Start, string Title)> chapters)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== TITLE OPTIONS ===");
        foreach (var title in answer.Titles)
            sb.Append("- ").AppendLine(title.Trim());

        sb.AppendLine().AppendLine("=== DESCRIPTION (paste as is; chapters included) ===");
        sb.AppendLine(answer.Description.Trim()).AppendLine();
        if (chapters.Count > 0)
            foreach (var (start, title) in chapters)
                sb.Append(FormatTime(start)).Append(' ').AppendLine(title);
        else
            sb.AppendLine($"(no chapters: YouTube needs at least {MinChapters} clips of {MinChapterSeconds:0}+ s)");

        sb.AppendLine().AppendLine("=== TAGS ===");
        sb.AppendLine(string.Join(", ", answer.Tags.Select(t => t.Trim()).Where(t => t.Length > 0)));
        return sb.ToString();
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Floor(seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static string? Validate(YouTubeAnswer a)
    {
        var errors = new List<string>();
        if (a.Titles.Count is < 1 or > 5)
            errors.Add("titles must have 3 options");
        errors.AddRange(a.Titles.Where(t => string.IsNullOrWhiteSpace(t) || t.Length > 100)
            .Select(t => $"title \"{t}\" must be 1..100 characters"));
        if (string.IsNullOrWhiteSpace(a.Description) || a.Description.Length > 3500)
            errors.Add("description must be 1..3500 characters");
        if (string.Join(",", a.Tags).Length > 450)
            errors.Add("tags are too long in total (max ~450 characters)");
        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    private static readonly LlmJsonSchema Schema = new("youtube_metadata", new JsonObject
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("titles", "description", "tags"),
        ["properties"] = new JsonObject
        {
            ["titles"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["description"] = new JsonObject { ["type"] = "string" },
            ["tags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        },
    });

    internal sealed record YouTubeAnswer(List<string> Titles, string Description, List<string> Tags);
}
