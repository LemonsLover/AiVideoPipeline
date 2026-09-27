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
        var target = new Option<double?>("--target")
        {
            Description = "Target video length in minutes, best moments first (0 = no target; saved)",
        };
        var minScore = new Option<int?>("--min-score")
        {
            Description = "Minimum moment score 0-10 (saved)",
        };
        var force = CommonArguments.Force();

        var command = new Command("postprocess", "Pick moments, add context padding and merge them into clips (clips.json)")
        {
            project, target, minScore, force,
        };
        command.SetAction((parse, ct) => CommandHandler.RunAsync(async () =>
        {
            var store = services.GetRequiredService<IProjectStore>();
            var runner = services.GetRequiredService<PipelineRunner>();
            var p = await store.LoadAsync(parse.GetValue(project)!, ct);

            var changed = TrackSelection.PromptIfMissing(p);
            if (parse.GetValue(target) is { } t && t != p.Settings.TargetMinutes)
            {
                p.Settings.TargetMinutes = t;
                changed = true;
            }
            if (parse.GetValue(minScore) is { } s && s != p.Settings.MinScore)
            {
                if (s is < 0 or > 10)
                    throw new PipelineException("--min-score must be 0..10.");
                p.Settings.MinScore = s;
                changed = true;
            }
            if (changed)
                await store.SaveAsync(p, ct);

            await SpectreProgressReporter.RunAsync(progress =>
                runner.RunAsync(p, StageNames.Postprocess, parse.GetValue(force), progress, ct));

            var moments = await JsonDefaults.ReadAsync<MomentsDocument>(p.PathOf(ProjectLayout.MomentsFile), ct);
            var clips = await JsonDefaults.ReadAsync<ClipsDocument>(p.PathOf(ProjectLayout.ClipsFile), ct);
            ProjectView.WriteClips(clips, moments);
            return CommandHandler.Ok;
        }));
        return command;
    }
}
