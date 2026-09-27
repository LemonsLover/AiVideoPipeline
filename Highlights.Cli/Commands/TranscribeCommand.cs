using System.CommandLine;
using Highlights.Cli.Rendering;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Highlights.Cli.Commands;

internal static class TranscribeCommand
{
    public static Command Create(IServiceProvider services)
    {
        var project = CommonArguments.Project();
        var model = new Option<string?>("--model", "-m") { Description = "Whisper model for this project, e.g. large-v3-turbo (saved)" };
        var language = new Option<string?>("--language", "-l") { Description = "Language code or 'auto' for this project (saved)" };
        var force = CommonArguments.Force();

        var command = new Command("transcribe", "Transcribe the voice track with Whisper (runs extract first if needed)")
        {
            project, model, language, force,
        };
        command.SetAction((parse, ct) => CommandHandler.RunAsync(async () =>
        {
            var store = services.GetRequiredService<IProjectStore>();
            var runner = services.GetRequiredService<PipelineRunner>();
            var p = await store.LoadAsync(parse.GetValue(project)!, ct);

            var changed = TrackSelection.PromptIfMissing(p);
            if (parse.GetValue(model) is { } m && m != p.Settings.WhisperModel)
            {
                p.Settings.WhisperModel = m;
                changed = true;
            }
            if (parse.GetValue(language) is { } l && l != p.Settings.Language)
            {
                p.Settings.Language = l;
                changed = true;
            }
            if (changed)
                await store.SaveAsync(p, ct);

            var outcome = StageOutcome.Skipped;
            await SpectreProgressReporter.RunAsync(async progress =>
                outcome = await runner.RunAsync(p, StageNames.Transcribe, parse.GetValue(force), progress, ct));

            var details = p.Stages[StageNames.Transcribe].Details ?? [];
            if (outcome == StageOutcome.Skipped)
                AnsiConsole.MarkupLine("[grey]transcript.json is up to date (use --force to re-transcribe).[/]");
            else
                AnsiConsole.MarkupLineInterpolated(
                    $"[green]Transcribed:[/] {details.GetValueOrDefault("segments")} segments, language {details.GetValueOrDefault("language")}, runtime {details.GetValueOrDefault("runtime")}");

            if (details.GetValueOrDefault("runtime") is "Cpu" or "CpuNoAvx")
                AnsiConsole.MarkupLine("[yellow]Whisper ran on CPU. For GPU install CUDA Toolkit 13.x (see Whisper:Runtimes).[/]");
            return CommandHandler.Ok;
        }));
        return command;
    }
}
