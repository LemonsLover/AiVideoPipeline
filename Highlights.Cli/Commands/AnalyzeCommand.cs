using System.CommandLine;
using System.Globalization;
using System.Text;
using Highlights.Cli.Rendering;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Analysis;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Highlights.Cli.Commands;

internal static class AnalyzeCommand
{
    private const string PromptDumpFile = "analyze.prompt.txt";

    public static Command Create(IServiceProvider services)
    {
        var project = CommonArguments.Project();
        var game = CommonArguments.Game();
        var force = CommonArguments.Force();
        var dryRun = new Option<bool>("--dry-run")
        {
            Description = $"Build the prompt and save it to {PromptDumpFile} without calling the LLM",
        };

        var command = new Command("analyze", "Find highlight moments with the LLM (runs transcribe and signals first if needed)")
        {
            project, game, force, dryRun,
        };
        command.SetAction((parse, ct) => CommandHandler.RunAsync(async () =>
        {
            var store = services.GetRequiredService<IProjectStore>();
            var runner = services.GetRequiredService<PipelineRunner>();
            var p = await store.LoadAsync(parse.GetValue(project)!, ct);

            var changed = TrackSelection.PromptIfMissing(p);
            if (parse.GetValue(game) is { } g && g != p.Settings.Game)
            {
                services.GetRequiredService<GameProfileStore>().Load(g); // fail early on a typo
                p.Settings.Game = g;
                changed = true;
            }
            if (changed)
                await store.SaveAsync(p, ct);

            if (parse.GetValue(dryRun))
                return await DryRunAsync(services, runner, p, ct);

            var outcome = StageOutcome.Skipped;
            await SpectreProgressReporter.RunAsync(async progress =>
                outcome = await runner.RunAsync(p, StageNames.Analyze, parse.GetValue(force), progress, ct));

            if (outcome == StageOutcome.Skipped)
                AnsiConsole.MarkupLine("[grey]moments.json is up to date (use --force to ask the LLM again).[/]");

            var doc = await JsonDefaults.ReadAsync<MomentsDocument>(p.PathOf(ProjectLayout.MomentsFile), ct);
            ProjectView.WriteMoments(doc);

            var details = p.Stages[StageNames.Analyze].Details ?? [];
            AnsiConsole.MarkupLineInterpolated(
                $"[bold]{doc.Moments.Count}[/] moments by {doc.Model} · this run: ${details.GetValueOrDefault("costUsd")} · project total: ${p.TotalLlmCostUsd.ToString("0.####", CultureInfo.InvariantCulture)}");
            return CommandHandler.Ok;
        }));
        return command;
    }

    private static async Task<int> DryRunAsync(IServiceProvider services, PipelineRunner runner, HighlightsProject p, CancellationToken ct)
    {
        // Inputs must exist; this runs the free local stages if needed.
        await SpectreProgressReporter.RunAsync(async progress =>
        {
            await runner.RunAsync(p, StageNames.Transcribe, force: false, progress, ct);
            await runner.RunAsync(p, StageNames.AudioSignals, force: false, progress, ct);
        });

        var stage = services.GetServices<IPipelineStage>().OfType<AnalyzeStage>().Single();
        var (profileId, _, _, messages) = await stage.BuildPromptAsync(p, ct);
        var text = string.Join("\n\n", messages.Select(m => $"===== {m.Role.ToUpperInvariant()} =====\n{m.Content}"));
        var path = p.PathOf(PromptDumpFile);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct);

        // ~4 characters per token is a rough estimate for mixed Cyrillic/Latin text.
        AnsiConsole.MarkupLineInterpolated(
            $"Profile [bold]{profileId}[/], prompt {text.Length:N0} chars (~{text.Length / 4:N0} tokens) → {path}");
        return CommandHandler.Ok;
    }
}
