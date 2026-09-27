using System.Globalization;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Highlights.Core.Stages.Analysis;
using Highlights.Core.Stages.Postprocessing;
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
            AnsiConsole.MarkupLine($"  {Markup.Escape(name),-14} {status}");
        }
    }

    public static void WriteMoments(MomentsDocument doc)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Id", "Time", "Len", "Category", "Score", "Title");
        foreach (var m in doc.Moments)
        {
            var score = m.Score >= 8 ? $"[green]{m.Score}[/]" : m.Score >= 5 ? $"{m.Score}" : $"[grey]{m.Score}[/]";
            table.AddRow(m.Id, Duration(m.Start), $"{m.End - m.Start:0}s", Markup.Escape(m.Category), score, Markup.Escape(m.Title));
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLineInterpolated($"[italic]{doc.Summary}[/]");
    }

    public static void WriteClips(ClipsDocument clips, MomentsDocument moments)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Clip", "Source", "Len", "Score", "Moments", "Title");
        foreach (var c in clips.Clips)
            table.AddRow(c.Id, $"{Duration(c.Start)}–{Duration(c.End)}", $"{c.Duration:0}s", c.Score.ToString(CultureInfo.InvariantCulture),
                string.Join(",", c.MomentIds), Markup.Escape(c.Title));
        AnsiConsole.Write(table);

        var used = clips.Clips.SelectMany(c => c.MomentIds).ToHashSet();
        var target = clips.TargetMinutes is > 0 ? $", target {clips.TargetMinutes} min" : "";
        AnsiConsole.MarkupLineInterpolated(
            $"[bold]{clips.Clips.Count}[/] clips, [bold]{Duration(clips.TotalSeconds)}[/] total · {used.Count}/{moments.Moments.Count} moments (score ≥ {clips.MinScore}{target})");
    }

    public static string Duration(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
}
