using System.CommandLine;
using System.Globalization;
using Highlights.Cli.Rendering;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Highlights.Cli.Commands;

internal static class RenderCommand
{
    public static Command Create(IServiceProvider services)
    {
        var project = CommonArguments.Project();
        var titles = new Option<bool?>("--titles")
        {
            Description = "On-screen title captions: --titles or --titles false (saved in the project)",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var noDescription = new Option<bool>("--no-description") { Description = "Skip the YouTube description (no LLM call)" };
        var force = CommonArguments.Force();

        var command = new Command("render", "Render highlights.mp4 and youtube.txt (runs every missing stage before it)")
        {
            project, titles, noDescription, force,
        };
        command.SetAction((parse, ct) => CommandHandler.RunAsync(async () =>
        {
            var store = services.GetRequiredService<IProjectStore>();
            var runner = services.GetRequiredService<PipelineRunner>();
            var p = await store.LoadAsync(parse.GetValue(project)!, ct);

            var changed = TrackSelection.PromptIfMissing(p);
            if (parse.GetResult(titles) is not null)
            {
                // "--titles" alone means true.
                var value = parse.GetValue(titles) ?? true;
                if (p.Settings.Titles != value)
                {
                    p.Settings.Titles = value;
                    changed = true;
                }
            }
            if (changed)
                await store.SaveAsync(p, ct);

            var forced = parse.GetValue(force);
            var describe = !parse.GetValue(noDescription);
            var rendered = StageOutcome.Skipped;
            await SpectreProgressReporter.RunAsync(async progress =>
            {
                rendered = await runner.RunAsync(p, StageNames.Render, forced, progress, ct);
                if (describe)
                    await runner.RunAsync(p, StageNames.Describe, forced, progress, ct);
            });

            var details = p.Stages[StageNames.Render].Details ?? [];
            var size = long.TryParse(details.GetValueOrDefault("sizeBytes"), CultureInfo.InvariantCulture, out var bytes) ? bytes / 1048576 : 0;
            var seconds = double.TryParse(details.GetValueOrDefault("durationSeconds"), CultureInfo.InvariantCulture, out var s) ? s : 0;
            AnsiConsole.MarkupLineInterpolated(rendered == StageOutcome.Skipped
                ? (FormattableString)$"[grey]highlights.mp4 is up to date (use --force to re-render).[/]"
                : $"[green]Rendered:[/] {p.PathOf(ProjectLayout.VideoOutputFile)} ({ProjectView.Duration(seconds)}, {size} MB, {details.GetValueOrDefault("encoder")})");

            if (describe)
            {
                AnsiConsole.Write(new Rule("youtube.txt").LeftJustified());
                AnsiConsole.WriteLine(await File.ReadAllTextAsync(p.PathOf(ProjectLayout.YouTubeFile), ct));
            }
            AnsiConsole.MarkupLineInterpolated($"LLM cost for this project so far: ${p.TotalLlmCostUsd.ToString("0.####", CultureInfo.InvariantCulture)}");
            return CommandHandler.Ok;
        }));
        return command;
    }
}
