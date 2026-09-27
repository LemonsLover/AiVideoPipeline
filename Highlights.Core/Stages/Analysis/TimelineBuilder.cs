using System.Globalization;
using System.Text;
using Highlights.Core.Stages.Signals;
using Highlights.Core.Stages.Transcription;

namespace Highlights.Core.Stages.Analysis;

/// <summary>
/// Builds the chronological text the LLM reads: transcript lines and audio events, each prefixed with
/// the time in seconds, e.g. <c>493.9 | [LAUGHTER p=0.55]</c>.
/// </summary>
internal static class TimelineBuilder
{
    public static string Build(Transcript transcript, AudioSignals signals, double minLoudPeakDb)
    {
        var lines = new List<(double Time, int Order, string Text)>();

        foreach (var s in transcript.Segments)
            lines.Add((s.Start, 0, s.Text));

        foreach (var e in signals.Events)
        {
            if (e.Type == "loud")
            {
                if (e.Peak >= minLoudPeakDb)
                    lines.Add((e.Start, 1, $"[LOUD +{e.Peak:0}dB {e.End - e.Start:0.#}s]"));
            }
            else
            {
                lines.Add((e.Start, 1, $"[{e.Type.ToUpperInvariant()} p={e.Peak:0.00} {e.End - e.Start:0.#}s]"));
            }
        }

        var sb = new StringBuilder();
        foreach (var (time, _, text) in lines.OrderBy(l => l.Time).ThenBy(l => l.Order))
            sb.Append(time.ToString("0.0", CultureInfo.InvariantCulture)).Append(" | ").AppendLine(text);
        return sb.ToString();
    }
}
