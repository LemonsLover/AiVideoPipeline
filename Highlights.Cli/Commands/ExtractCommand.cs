using System.CommandLine;
using Highlights.Cli.Rendering;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Highlights.Cli.Commands;

internal static class ExtractCommand
{
    public static Command Create(IServiceProvider services)
    {
        var project = CommonArguments.Project();
        var track = new Option<int?>("--track", "-t") { Description = "Voice track index (saved in the project)" };
        var force = CommonArguments.Force();

        var command = new Command("extract", "Extract the voice track to audio.wav (16 kHz mono)") { project, track, force };
        command.SetAction((parse, ct) => CommandHandler.RunAsync(async () =>
        {
            var store = services.GetRequiredService<IProjectStore>();
            var runner = services.GetRequiredService<PipelineRunner>();
            var p = await store.LoadAsync(parse.GetValue(project)!, ct);

            var changed = parse.GetValue(track) is { } t ? TrackSelection.Apply(p, t) : TrackSelection.PromptIfMissing(p);
            if (changed)
                await store.SaveAsync(p, ct);

            var outcome = StageOutcome.Skipped;
            await SpectreProgressReporter.RunAsync(async progress =>
                outcome = await runner.RunAsync(p, StageNames.Extract, parse.GetValue(force), progress, ct));

            AnsiConsole.MarkupLine(outcome == StageOutcome.Skipped
                ? "[grey]audio.wav is up to date (use --force to re-extract).[/]"
                : $"[green]Extracted:[/] {Markup.Escape(p.PathOf(ProjectLayout.AudioFile))}");
            return CommandHandler.Ok;
        }));
        return command;
    }
}
