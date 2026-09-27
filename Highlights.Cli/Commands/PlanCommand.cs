using System.CommandLine;
using System.Globalization;
using Highlights.Cli.Rendering;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Planning;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Highlights.Cli.Commands;

internal static class PlanCommand
{
    private const string PromptDumpFile = "plan.prompt.txt";

    public static Command Create(IServiceProvider services)
    {
        var project = CommonArguments.Project();
        var mode = CommonArguments.Mode();
        var target = new Option<double?>("--target")
        {
            Description = "Target video length in minutes (0 = the mode's default for this session; saved)",
        };
        var captions = new Option<bool?>("--captions")
        {
            Description = "On-screen context captions: --captions or --captions false (default: the mode's; saved)",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var force = CommonArguments.Force();
        var dryRun = CommonArguments.DryRun(PromptDumpFile);

        var command = new Command("plan", "Cut the video in an editing mode with the LLM (runs analyze first if needed)")
        {
            project, mode, target, captions, force, dryRun,
        };
        command.SetAction((parse, ct) => CommandHandler.RunAsync(async () =>
        {
            var store = services.GetRequiredService<IProjectStore>();
            var runner = services.GetRequiredService<PipelineRunner>();
            var modes = services.GetRequiredService<EditModeStore>();
            var p = await store.LoadAsync(parse.GetValue(project)!, ct);

            var changed = TrackSelection.PromptIfMissing(p);
            if (parse.GetValue(mode) is { } m && m != p.Settings.Mode)
            {
                modes.Load(m); // fail early on a typo
                p.Settings.Mode = m;
                changed = true;
            }
            if (parse.GetValue(target) is { } t && t != p.Settings.TargetMinutes)
            {
                p.Settings.TargetMinutes = t > 0 ? t : null;
                changed = true;
            }
            if (parse.GetResult(captions) is not null)
            {
                p.Settings.ContextCaptions = parse.GetValue(captions) ?? true;
                changed = true;
            }
            if (changed)
                await store.SaveAsync(p, ct);

            if (parse.GetValue(dryRun))
            {
                await SpectreProgressReporter.RunAsync(progress => runner.RunAsync(p, StageNames.Analyze, false, progress, ct));
                var stage = services.GetServices<IPipelineStage>().OfType<PlanStage>().Single();
                var prompt = await stage.BuildPromptAsync(p, ct);
                AnsiConsole.MarkupLineInterpolated($"Mode [bold]{prompt.Context.Mode.Name}[/], target {prompt.Context.TargetMinutes} min");
                await AnalyzeCommand.SavePromptAsync(p, PromptDumpFile, prompt.Messages, ct);
                return CommandHandler.Ok;
            }

            var outcome = StageOutcome.Skipped;
            await SpectreProgressReporter.RunAsync(async progress =>
                outcome = await runner.RunAsync(p, StageNames.Plan, parse.GetValue(force), progress, ct));
            if (outcome == StageOutcome.Skipped)
                AnsiConsole.MarkupLine("[grey]plan.json is up to date (use --force to ask the LLM again).[/]");

            var plan = await JsonDefaults.ReadAsync<PlanDocument>(p.PathOf(ProjectLayout.PlanFile), ct);
            ProjectView.WritePlan(plan);
            var details = p.Stages[StageNames.Plan].Details ?? [];
            AnsiConsole.MarkupLineInterpolated(
                $"this run: ${details.GetValueOrDefault("costUsd")} · project total: ${p.TotalLlmCostUsd.ToString("0.####", CultureInfo.InvariantCulture)} · next: [bold]highlights render[/]");
            return CommandHandler.Ok;
        }));
        return command;
    }
}
