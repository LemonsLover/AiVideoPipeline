using System.CommandLine;
using Highlights.Cli.Rendering;
using Highlights.Core.Media;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Planning;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Highlights.Cli.Commands;

internal static class ImportCommand
{
    public static Command Create(IServiceProvider services)
    {
        var url = new Argument<string>("url") { Description = "Video link (YouTube, including unlisted videos)" };
        var dir = new Option<string?>("--dir") { Description = "Download folder (default: Import:DownloadDirectory, i.e. Videos\\Highlights)" };
        var mode = CommonArguments.Mode();

        var command = new Command("import", "Download a video by link and create a project for it") { url, dir, mode };
        command.SetAction((parse, ct) => CommandHandler.RunAsync(async () =>
        {
            if (parse.GetValue(mode) is { } m)
                services.GetRequiredService<EditModeStore>().Load(m); // fail before downloading anything

            var importer = services.GetRequiredService<YouTubeImporter>();
            HighlightsProject? project = null;
            await SpectreProgressReporter.RunAsync(async progress =>
                project = await importer.ImportAsync(parse.GetValue(url)!, parse.GetValue(dir), progress, ct));

            if (parse.GetValue(mode) is { } modeId && project!.Settings.Mode != modeId)
            {
                project.Settings.Mode = modeId;
                await services.GetRequiredService<IProjectStore>().SaveAsync(project, ct);
            }

            ProjectView.WriteSummary(project!);
            ProjectView.WriteTracks(project!);
            AnsiConsole.MarkupLineInterpolated(
                $"[green]Imported.[/] Build the video with: [bold]highlights render \"{project!.Directory}\"[/]");
            return CommandHandler.Ok;
        }));
        return command;
    }
}
