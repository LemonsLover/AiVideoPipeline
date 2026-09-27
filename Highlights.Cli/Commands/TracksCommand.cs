using System.CommandLine;
using Highlights.Cli.Rendering;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Highlights.Cli.Commands;

internal static class TracksCommand
{
    public static Command Create(IServiceProvider services)
    {
        var project = CommonArguments.Project();
        var select = new Option<int?>("--select", "-s") { Description = "Select the voice track by index" };

        var command = new Command("tracks", "List audio tracks and select the voice track") { project, select };
        command.SetAction((parse, ct) => CommandHandler.RunAsync(async () =>
        {
            var store = services.GetRequiredService<IProjectStore>();
            var runner = services.GetRequiredService<PipelineRunner>();
            var p = await store.LoadAsync(parse.GetValue(project)!, ct);

            if (parse.GetValue(select) is { } index && TrackSelection.Apply(p, index))
            {
                await store.SaveAsync(p, ct);
                AnsiConsole.MarkupLineInterpolated($"[green]Voice track set to #{index}.[/]");
            }

            ProjectView.WriteSummary(p);
            ProjectView.WriteTracks(p);
            ProjectView.WriteStages(p, runner, StageNames.Extract, StageNames.Transcribe, StageNames.AudioSignals);
            return CommandHandler.Ok;
        }));
        return command;
    }
}
