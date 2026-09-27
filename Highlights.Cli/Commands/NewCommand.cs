using System.CommandLine;
using Highlights.Cli.Rendering;
using Highlights.Core.Projects;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Highlights.Cli.Commands;

internal static class NewCommand
{
    public static Command Create(IServiceProvider services)
    {
        var video = new Argument<FileInfo>("video") { Description = "Gameplay recording" };
        var track = new Option<int?>("--track", "-t") { Description = "Voice track index (see the tracks table)" };

        var command = new Command("new", "Create a project folder next to the video and read its tracks") { video, track };
        command.SetAction((parse, ct) => CommandHandler.RunAsync(async () =>
        {
            var store = services.GetRequiredService<IProjectStore>();
            var project = await AnsiConsole.Status().StartAsync("Reading media info...",
                _ => store.CreateAsync(parse.GetValue(video)!.FullName, ct));

            if (parse.GetValue(track) is { } t && TrackSelection.Apply(project, t))
                await store.SaveAsync(project, ct);

            ProjectView.WriteSummary(project);
            ProjectView.WriteTracks(project);
            AnsiConsole.MarkupLine("[green]Project created.[/] Next: [bold]highlights extract[/] and [bold]highlights transcribe[/]");
            return CommandHandler.Ok;
        }));
        return command;
    }
}
