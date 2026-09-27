using System.CommandLine;
using Highlights.Cli.Rendering;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Highlights.Cli.Commands;

internal static class SignalsCommand
{
    public static Command Create(IServiceProvider services)
    {
        var project = CommonArguments.Project();
        var force = CommonArguments.Force();

        var command = new Command("signals", "Analyze loudness and detect laughter/screams with YAMNet (runs extract first if needed)")
        {
            project, force,
        };
        command.SetAction((parse, ct) => CommandHandler.RunAsync(async () =>
        {
            var store = services.GetRequiredService<IProjectStore>();
            var runner = services.GetRequiredService<PipelineRunner>();
            var p = await store.LoadAsync(parse.GetValue(project)!, ct);

            if (TrackSelection.PromptIfMissing(p))
                await store.SaveAsync(p, ct);

            var outcome = StageOutcome.Skipped;
            await SpectreProgressReporter.RunAsync(async progress =>
                outcome = await runner.RunAsync(p, StageNames.AudioSignals, parse.GetValue(force), progress, ct));

            if (outcome == StageOutcome.Skipped)
                AnsiConsole.MarkupLine("[grey]signals.json is up to date (use --force to recompute).[/]");

            var details = p.Stages[StageNames.AudioSignals].Details ?? [];
            var table = new Table().Border(TableBorder.Rounded).AddColumns("Event type", "Count");
            foreach (var (key, value) in details.Where(d => d.Key.StartsWith("events.", StringComparison.Ordinal)))
                table.AddRow(Markup.Escape(key["events.".Length..]), value);
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLineInterpolated($"Median level: {details.GetValueOrDefault("medianDb")} dBFS → {p.PathOf(ProjectLayout.SignalsFile)}");
            return CommandHandler.Ok;
        }));
        return command;
    }
}
