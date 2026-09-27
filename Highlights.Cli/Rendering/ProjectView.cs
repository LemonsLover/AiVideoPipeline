using System.Globalization;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Spectre.Console;

namespace Highlights.Cli.Rendering;

internal static class ProjectView
{
    public static void WriteSummary(HighlightsProject project)
    {
        var media = project.Media;
        AnsiConsole.MarkupLineInterpolated($"[bold]Project:[/] {project.Directory}");
        AnsiConsole.MarkupLineInterpolated($"[bold]Video:[/]   {project.ResolveVideoPath()}");
        if (media is not null)
        {
            var video = media.Video is { } v
                ? $"{v.Width}x{v.Height} {v.Codec}{(v.FrameRate is { } fps ? $" {fps:0.##} fps" : "")}"
                : "no video stream";
            AnsiConsole.MarkupLineInterpolated(
                $"[bold]Media:[/]   {Duration(media.DurationSeconds)}, {video}, {media.AudioTracks.Count} audio track(s)");
        }
    }

    public static void WriteTracks(HighlightsProject project)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumns("", "#", "Stream", "Codec", "Channels", "Rate", "Lang", "Title", "Duration");

        foreach (var t in project.Media?.AudioTracks ?? [])
        {
            var selected = project.Settings.AudioTrack == t.Index;
            table.AddRow(
                selected ? "[green]►[/]" : "",
                t.Index.ToString(CultureInfo.InvariantCulture),
                t.StreamIndex.ToString(CultureInfo.InvariantCulture),
                Markup.Escape(t.Codec),
                Markup.Escape(t.ChannelLayout ?? t.Channels.ToString(CultureInfo.InvariantCulture)),
                $"{t.SampleRate / 1000.0:0.#} kHz",
                Markup.Escape(t.Language ?? "-"),
                Markup.Escape(t.Title ?? "-"),
                t.DurationSeconds is { } d ? Duration(d) : "-");
        }

        AnsiConsole.Write(table);
        if (project.Settings.AudioTrack is null)
            AnsiConsole.MarkupLine("[yellow]No voice track selected.[/] Pick one: [bold]highlights tracks --select N[/]");
    }

    public static void WriteStages(HighlightsProject project, PipelineRunner runner, params string[] stages)
    {
        foreach (var name in stages)
        {
            var stage = runner.GetStage(name);
            var state = project.Stages.GetValueOrDefault(name);
            var status = runner.IsUpToDate(project, stage) ? "[green]up to date[/]"
                : state?.Status == StageStatus.Completed ? "[yellow]outdated[/]"
                : state?.Status is { } s ? Markup.Escape(s.ToString().ToLowerInvariant())
                : "[grey]not run[/]";
            AnsiConsole.MarkupLine($"  {Markup.Escape(name),-12} {status}");
        }
    }

    public static string Duration(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
}
