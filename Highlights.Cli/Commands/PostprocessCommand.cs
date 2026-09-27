using System.CommandLine;
using Highlights.Cli.Rendering;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Analysis;
using Highlights.Core.Stages.Postprocessing;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Highlights.Cli.Commands;

internal static class PostprocessCommand
{
    public static Command Create(IServiceProvider services)
    {
        var project = CommonArguments.Project();
        var force = CommonArguments.Force();

        var command = new Command("postprocess", "Build exact clip segments from the plan: padding, word snapping, pause trimming (clips.json)")
        {
            project, force,
        };
        command.SetAction((parse, ct) => CommandHandler.RunAsync(async () =>
        {
            var store = services.GetRequiredService<IProjectStore>();
            var runner = services.GetRequiredService<PipelineRunner>();
            var p = await store.LoadAsync(parse.GetValue(project)!, ct);

            var changed = TrackSelection.PromptIfMissing(p);
            if (changed)
                await store.SaveAsync(p, ct);

            await SpectreProgressReporter.RunAsync(progress =>
                runner.RunAsync(p, StageNames.Postprocess, parse.GetValue(force), progress, ct));

            var clips = await JsonDefaults.ReadAsync<ClipsDocument>(p.PathOf(ProjectLayout.ClipsFile), ct);
            ProjectView.WriteClips(clips);
            return CommandHandler.Ok;
        }));
        return command;
    }
}
