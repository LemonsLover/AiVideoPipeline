using System.Globalization;
using System.Text;
using Highlights.Core.Stages.Signals;
using Highlights.Core.Stages.Transcription;
using Highlights.Core.Stages.Video;

namespace Highlights.Core.Stages.Analysis;

/// <summary>
/// Builds the chronological text the LLM reads: transcript lines, audio events and on-screen events, each prefixed
/// with the time in seconds, e.g. <c>493.9 | [LAUGHTER p=0.55]</c> or <c>512.0 | [SCREEN player_kill: …]</c>.
/// </summary>
internal static class TimelineBuilder
{
    public static string Build(Transcript transcript, AudioSignals signals, double minLoudPeakDb, VisionDocument? vision = null)
    {
        var lines = new List<(double Time, int Order, string Text)>();

        foreach (var s in transcript.Segments)
            lines.Add((s.Start, 1, s.Text));

        foreach (var e in signals.Events)
        {
            if (e.Type == "loud")
            {
                if (e.Peak >= minLoudPeakDb)
                    lines.Add((e.Start, 2, $"[LOUD +{e.Peak:0}dB {e.End - e.Start:0.#}s]"));
            }
            else
            {
                lines.Add((e.Start, 2, $"[{e.Type.ToUpperInvariant()} p={e.Peak:0.00} {e.End - e.Start:0.#}s]"));
            }
        }

        if (vision is { Enabled: true })
        {
            foreach (var w in vision.Windows)
                lines.Add((w.Start, 0, $"[SCREEN SUMMARY {w.Start:0}–{w.End:0}s: {w.Summary}]"));
            foreach (var e in vision.Events)
                lines.Add((e.Time, 3, $"[SCREEN {e.Type} ({e.Importance}): {e.Description}]"));
        }

        var sb = new StringBuilder();
        foreach (var (time, _, text) in lines.OrderBy(l => l.Time).ThenBy(l => l.Order))
            sb.Append(time.ToString("0.0", CultureInfo.InvariantCulture)).Append(" | ").AppendLine(text);
        return sb.ToString();
    }

    /// <summary>vision.json when the vision stage ran, else null.</summary>
    public static async Task<VisionDocument?> LoadVisionAsync(Projects.HighlightsProject project, CancellationToken cancellationToken)
    {
        var path = project.PathOf(Projects.ProjectLayout.VisionFile);
        return File.Exists(path) ? await Projects.JsonDefaults.ReadAsync<VisionDocument>(path, cancellationToken) : null;
    }
}
