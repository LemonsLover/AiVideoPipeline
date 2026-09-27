using System.CommandLine;
using System.Globalization;
using System.Text;
using Highlights.Cli.Rendering;
using Highlights.Core.Llm;
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
        var force = CommonArguments.Force();
        var dryRun = CommonArguments.DryRun(PromptDumpFile);

        var command = new Command("analyze", "Map the session into beats with the LLM (runs transcribe and signals first if needed)")
        {
            project, force, dryRun,
        };
        command.SetAction((parse, ct) => CommandHandler.RunAsync(async () =>
        {
            var store = services.GetRequiredService<IProjectStore>();
            var runner = services.GetRequiredService<PipelineRunner>();
            var p = await store.LoadAsync(parse.GetValue(project)!, ct);

            if (TrackSelection.PromptIfMissing(p))
                await store.SaveAsync(p, ct);

            if (parse.GetValue(dryRun))
            {
                await SpectreProgressReporter.RunAsync(async progress =>
                {
                    await runner.RunAsync(p, StageNames.Transcribe, force: false, progress, ct);
                    await runner.RunAsync(p, StageNames.AudioSignals, force: false, progress, ct);
                });
                var stage = services.GetServices<IPipelineStage>().OfType<AnalyzeStage>().Single();
                var (_, messages) = await stage.BuildPromptAsync(p, ct);
                await SavePromptAsync(p, PromptDumpFile, messages, ct);
                return CommandHandler.Ok;
            }

            var outcome = StageOutcome.Skipped;
            await SpectreProgressReporter.RunAsync(async progress =>
                outcome = await runner.RunAsync(p, StageNames.Analyze, parse.GetValue(force), progress, ct));

            if (outcome == StageOutcome.Skipped)
                AnsiConsole.MarkupLine("[grey]moments.json is up to date (use --force to ask the LLM again).[/]");

            var doc = await JsonDefaults.ReadAsync<MomentsDocument>(p.PathOf(ProjectLayout.MomentsFile), ct);
            ProjectView.WriteMoments(doc);

            var details = p.Stages[StageNames.Analyze].Details ?? [];
            AnsiConsole.MarkupLineInterpolated(
                $"[bold]{doc.Moments.Count}[/] beats · game: {doc.Game} · {doc.Model} · this run: ${details.GetValueOrDefault("costUsd")} · project total: ${p.TotalLlmCostUsd.ToString("0.####", CultureInfo.InvariantCulture)}");
            return CommandHandler.Ok;
        }));
        return command;
    }

    /// <summary>Writes the prompt to the project folder for inspection (--dry-run).</summary>
    public static async Task SavePromptAsync(HighlightsProject p, string file, IReadOnlyList<LlmMessage> messages, CancellationToken ct)
    {
        var text = string.Join("\n\n", messages.Select(m => $"===== {m.Role.ToUpperInvariant()} =====\n{m.Content}"));
        var path = p.PathOf(file);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct);
        // ~4 characters per token is a rough estimate for mixed Cyrillic/Latin text.
        AnsiConsole.MarkupLineInterpolated($"Prompt: {text.Length:N0} chars (~{text.Length / 4:N0} tokens) → {path}");
    }
}
