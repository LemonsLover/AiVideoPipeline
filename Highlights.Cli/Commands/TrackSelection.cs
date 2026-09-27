using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Spectre.Console;

namespace Highlights.Cli.Commands;

internal static class TrackSelection
{
    /// <summary>Sets the voice track from an explicit value. Returns true if the selection changed.</summary>
    public static bool Apply(HighlightsProject project, int track)
    {
        var count = project.Media?.AudioTracks.Count ?? 0;
        if (track < 0 || track >= count)
            throw new PipelineException($"Audio track {track} does not exist (valid: 0..{count - 1}).");
        if (project.Settings.AudioTrack == track)
            return false;
        project.Settings.AudioTrack = track;
        return true;
    }

    /// <summary>Asks the user to pick a track when none is selected and the console is interactive.</summary>
    public static bool PromptIfMissing(HighlightsProject project)
    {
        if (project.Settings.AudioTrack is not null)
            return false;
        if (!AnsiConsole.Profile.Capabilities.Interactive || System.Console.IsInputRedirected)
            throw new PipelineException("No voice track selected. Use --track N (see 'highlights tracks').");

        var tracks = project.Media?.AudioTracks ?? [];
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<int>()
                .Title("Which audio track contains the [bold]voice chat[/]?")
                .AddChoices(tracks.Select(t => t.Index))
                .UseConverter(i =>
                {
                    var t = tracks[i];
                    return Markup.Escape($"#{i}  {t.Title ?? "(no title)"}  {t.Codec} {t.ChannelLayout ?? $"{t.Channels}ch"} {t.Language}");
                }));
        project.Settings.AudioTrack = choice;
        return true;
    }
}
