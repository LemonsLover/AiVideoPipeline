using System.CommandLine;
using System.Globalization;
using Highlights.Cli.Rendering;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Video;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Highlights.Cli.Commands;

internal static class VisionCommand
{
    public static Command Create(IServiceProvider services)
    {
        var project = CommonArguments.Project();
        var force = CommonArguments.Force();

        var command = new Command("vision", "Extract frames and let a vision model watch the video (on-screen events)")
        {
            project, force,
        };
        command.SetAction((parse, ct) => CommandHandler.RunAsync(async () =>
        {
            var store = services.GetRequiredService<IProjectStore>();
            var runner = services.GetRequiredService<PipelineRunner>();
            var p = await store.LoadAsync(parse.GetValue(project)!, ct);

            await SpectreProgressReporter.RunAsync(progress => runner.RunAsync(p, StageNames.Vision, parse.GetValue(force), progress, ct));

            var doc = await JsonDefaults.ReadAsync<VisionDocument>(p.PathOf(ProjectLayout.VisionFile), ct);
            if (!doc.Enabled)
            {
                AnsiConsole.MarkupLine("[yellow]Vision is disabled (Vision:Enabled = false).[/]");
                return CommandHandler.Ok;
            }

            var table = new Table().Border(TableBorder.Rounded).AddColumns("Time", "Event", "Imp", "What happens");
            foreach (var e in doc.Events.Where(e => e.Importance >= 5))
                table.AddRow(ProjectView.Duration(e.Time), Markup.Escape(e.Type), e.Importance.ToString(CultureInfo.InvariantCulture), Markup.Escape(e.Description));
            AnsiConsole.Write(table);
            var details = p.Stages[StageNames.Vision].Details ?? [];
            AnsiConsole.MarkupLineInterpolated(
                $"{doc.Events.Count} on-screen events ({doc.Events.Count(e => e.Importance >= 5)} shown) · {doc.Model} · ${details.GetValueOrDefault("costUsd")}");
            return CommandHandler.Ok;
        }));
        return command;
    }
}
