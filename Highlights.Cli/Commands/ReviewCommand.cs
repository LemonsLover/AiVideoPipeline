using System.CommandLine;
using System.Globalization;
using CliWrap;
using Command = System.CommandLine.Command;
using Highlights.Cli.Rendering;
using Highlights.Core.Media;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Analysis;
using Highlights.Core.Stages.Postprocessing;
using Highlights.Core.Stages.Review;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Highlights.Cli.Commands;

internal static class ReviewCommand
{
    public static Command Create(IServiceProvider services)
    {
        var project = CommonArguments.Project();
        var exclude = new Option<string[]>("--exclude") { Description = "Clip ids to cut, e.g. --exclude c03 c07", AllowMultipleArgumentsPerToken = true };
        var include = new Option<string[]>("--include") { Description = "Clip ids to bring back", AllowMultipleArgumentsPerToken = true };
        var reset = new Option<bool>("--reset") { Description = "Discard all review edits" };

        var command = new Command("review", "Review clips: preview, include/exclude, trim, rename (interactive without options)")
        {
            project, exclude, include, reset,
        };
        command.SetAction((parse, ct) => CommandHandler.RunAsync(async () =>
        {
            var store = services.GetRequiredService<IProjectStore>();
            var runner = services.GetRequiredService<PipelineRunner>();
            var reviews = services.GetRequiredService<ReviewService>();
            var p = await store.LoadAsync(parse.GetValue(project)!, ct);

            if (TrackSelection.PromptIfMissing(p))
                await store.SaveAsync(p, ct);
            await SpectreProgressReporter.RunAsync(progress => runner.RunAsync(p, StageNames.Postprocess, false, progress, ct));

            var clips = (await JsonDefaults.ReadAsync<ClipsDocument>(p.PathOf(ProjectLayout.ClipsFile), ct)).Clips;
            var moments = (await JsonDefaults.ReadAsync<MomentsDocument>(p.PathOf(ProjectLayout.MomentsFile), ct)).Moments
                .ToDictionary(m => m.Id);
            var review = parse.GetValue(reset) ? new ReviewDocument() : await reviews.LoadAsync(p, ct);

            var excluded = parse.GetValue(exclude) ?? [];
            var included = parse.GetValue(include) ?? [];
            if (excluded.Length > 0 || included.Length > 0 || parse.GetValue(reset))
            {
                foreach (var id in excluded)
                    Edit(review, Find(clips, id)).Included = false;
                foreach (var id in included)
                    Edit(review, Find(clips, id)).Included = true;
            }
            else if (!AnsiConsole.Profile.Capabilities.Interactive || System.Console.IsInputRedirected)
            {
                WriteTable(clips, review, p);
                AnsiConsole.MarkupLine("[grey]Run in an interactive terminal to edit, or use --exclude/--include.[/]");
                return CommandHandler.Ok;
            }
            else if (!await InteractiveAsync(services, p, clips, moments, review, ct))
            {
                AnsiConsole.MarkupLine("[yellow]Changes discarded.[/]");
                return CommandHandler.Ok;
            }

            await reviews.SaveAsync(p, review, ct);
            await SpectreProgressReporter.RunAsync(progress => runner.RunAsync(p, StageNames.Review, false, progress, ct));
            var edit = await JsonDefaults.ReadAsync<EditDocument>(p.PathOf(ProjectLayout.EditFile), ct);
            WriteTable(clips, review, p);
            AnsiConsole.MarkupLineInterpolated(
                $"Saved. Video: [bold]{edit.Clips.Count}[/] clips, [bold]{ProjectView.Duration(edit.TotalSeconds)}[/]. Next: [bold]highlights render[/]");
            return CommandHandler.Ok;
        }));
        return command;
    }

    /// <summary>Returns false when the user discards the changes.</summary>
    private static async Task<bool> InteractiveAsync(IServiceProvider services, HighlightsProject p, IReadOnlyList<Clip> clips,
        IReadOnlyDictionary<string, Moment> moments, ReviewDocument review, CancellationToken ct)
    {
        const string save = "💾 Save", discard = "✖ Discard changes";
        while (true)
        {
            WriteTable(clips, review, p);
            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("Pick a clip to review")
                .PageSize(20)
                .AddChoices([.. clips.Select(c => c.Id), save, discard])
                .UseConverter(id => clips.FirstOrDefault(c => c.Id == id) is { } c ? Label(c, review) : id));

            if (choice == save)
                return true;
            if (choice == discard)
                return false;
            await EditClipAsync(services, p, clips.Single(c => c.Id == choice), moments, review, ct);
        }
    }

    private static async Task EditClipAsync(IServiceProvider services, HighlightsProject p, Clip clip,
        IReadOnlyDictionary<string, Moment> moments, ReviewDocument review, CancellationToken ct)
    {
        const string preview = "▶ Preview", toggle = "⇄ Include / exclude", start = "⇤ Adjust start", end = "⇥ Adjust end",
            rename = "✎ Rename", resetClip = "↺ Reset", back = "← Back";
        while (true)
        {
            var edit = Edit(review, clip);
            var (s, e) = (clip.Start + edit.StartOffset, clip.End + edit.EndOffset);
            AnsiConsole.Write(new Rule($"[bold]{clip.Id}[/] {Markup.Escape(edit.Title ?? clip.Title)}").LeftJustified());
            AnsiConsole.MarkupLineInterpolated($"{ProjectView.Duration(s)}–{ProjectView.Duration(e)} ({e - s:0.#}s), score {clip.Score}, {(edit.Included ? "included" : "EXCLUDED")}");
            foreach (var id in clip.MomentIds)
                if (moments.TryGetValue(id, out var m))
                    AnsiConsole.MarkupLineInterpolated($"  [grey]{m.Id}:[/] {m.Description} [italic grey]«{m.Quote}»[/]");

            var action = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .AddChoices(preview, toggle, start, end, rename, resetClip, back));
            switch (action)
            {
                case preview:
                    await PreviewAsync(services, p, s, e, $"{clip.Id} {edit.Title ?? clip.Title}", ct);
                    break;
                case toggle:
                    edit.Included = !edit.Included;
                    break;
                case start:
                    edit.StartOffset += AnsiConsole.Prompt(new TextPrompt<double>("Seconds to move the start (negative = earlier):")
                        .DefaultValue(-3));
                    break;
                case end:
                    edit.EndOffset += AnsiConsole.Prompt(new TextPrompt<double>("Seconds to move the end (positive = later):")
                        .DefaultValue(3));
                    break;
                case rename:
                    var title = AnsiConsole.Prompt(new TextPrompt<string>("New title (empty = keep the original):").AllowEmpty());
                    edit.Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
                    break;
                case resetClip:
                    review.Clips.Remove(ReviewService.KeyOf(clip));
                    break;
                default:
                    return;
            }
        }
    }

    private static async Task PreviewAsync(IServiceProvider services, HighlightsProject p, double start, double end, string title,
        CancellationToken ct)
    {
        var ffplay = services.GetRequiredService<ToolLocator>().Ffplay;
        AnsiConsole.MarkupLine("[grey]Playing… close the window (or press Q/Esc) to return.[/]");
        await CliWrap.Cli.Wrap(ffplay)
            .WithArguments(["-hide_banner", "-loglevel", "error", "-autoexit", "-x", "1280", "-y", "720",
                "-window_title", title,
                "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
                "-t", (end - start).ToString("0.###", CultureInfo.InvariantCulture),
                p.ResolveVideoPath()])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);
    }

    private static void WriteTable(IReadOnlyList<Clip> clips, ReviewDocument review, HighlightsProject p)
    {
        var applied = ReviewService.Apply(clips, review, p.Media?.DurationSeconds ?? double.MaxValue);
        var table = new Table().Border(TableBorder.Rounded).AddColumns("", "Clip", "Source", "Len", "Score", "Title");
        foreach (var (planned, edit, final) in applied)
        {
            var c = final ?? planned;
            var changed = edit.StartOffset != 0 || edit.EndOffset != 0 ? " [yellow]✂[/]" : "";
            table.AddRow(final is null ? "[red]✗[/]" : "[green]✓[/]", planned.Id,
                $"{ProjectView.Duration(c.Start)}–{ProjectView.Duration(c.End)}{changed}", $"{c.Duration:0}s",
                planned.Score.ToString(CultureInfo.InvariantCulture),
                final is null ? $"[grey strikethrough]{Markup.Escape(c.Title)}[/]" : Markup.Escape(c.Title));
        }
        AnsiConsole.Write(table);
        var total = applied.Where(a => a.Final is not null).Sum(a => a.Final!.Duration);
        AnsiConsole.MarkupLineInterpolated($"Total: [bold]{ProjectView.Duration(total)}[/] in {applied.Count(a => a.Final is not null)} clips");
    }

    private static string Label(Clip c, ReviewDocument review)
    {
        var edit = review.Clips.GetValueOrDefault(ReviewService.KeyOf(c));
        var mark = edit is { Included: false } ? "✗" : "✓";
        return Markup.Escape($"{mark} {c.Id}  {ProjectView.Duration(c.Start)}  [{c.Score}]  {edit?.Title ?? c.Title}");
    }

    private static ClipEdit Edit(ReviewDocument review, Clip clip)
    {
        var key = ReviewService.KeyOf(clip);
        if (!review.Clips.TryGetValue(key, out var edit))
            review.Clips[key] = edit = new ClipEdit();
        return edit;
    }

    private static Clip Find(IReadOnlyList<Clip> clips, string id) =>
        clips.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new PipelineException($"Unknown clip '{id}'. Clips: {string.Join(", ", clips.Select(c => c.Id))}");
}
