using System.Globalization;
using System.Text.RegularExpressions;
using Highlights.Core.Media;
using Highlights.Core.Pipeline;

namespace Highlights.Core.Stages.Rendering;

/// <summary>
/// Brings the rendered audio to a target integrated loudness (EBU R128) with a gentle compressor, gain and a
/// peak limiter. The gain is found iteratively by measuring the actual result (audio-only passes take seconds).
/// </summary>
/// <remarks>
/// Recorded voice chat is often very quiet (≈ -30 LUFS) with loud spikes (yells, game sounds). loudnorm undershoots
/// such material by 1.5–3 LU in both single- and two-pass mode, and a plain gain + limiter crushes the spikes.
/// </remarks>
internal static partial class LoudnessNormalizer
{
    private const string Compressor = "acompressor=threshold=-30dB:ratio=3:attack=20:release=250";
    private const double Tolerance = 0.3;
    private const int MaxIterations = 3;

    /// <summary>Returns the audio filter that normalizes <paramref name="input"/> to <paramref name="lufs"/>.</summary>
    public static async Task<(string Filter, double Before, double After)> BuildFilterAsync(
        IFfmpegRunner ffmpeg, string input, double lufs, double truePeakDb, CancellationToken cancellationToken)
    {
        // The limiter works on sample peaks; stay a bit below the true-peak target to leave room for AAC overshoot.
        var limit = Math.Clamp(Math.Pow(10, (truePeakDb - 0.5) / 20), 0.0625, 1);
        string Filter(double gain) => FormattableString.Invariant(
            $"{Compressor},volume={gain:0.##}dB,alimiter=limit={limit:0.####}:attack=5:release=50:level=0");

        var before = await MeasureAsync(ffmpeg, input, "anull", cancellationToken);
        var compressed = await MeasureAsync(ffmpeg, input, Compressor, cancellationToken);
        var gain = Math.Clamp(lufs - compressed, -20, 40);
        var after = compressed;

        for (var i = 0; i < MaxIterations; i++)
        {
            after = await MeasureAsync(ffmpeg, input, Filter(gain), cancellationToken);
            var error = lufs - after;
            if (Math.Abs(error) < Tolerance)
                break;
            gain = Math.Clamp(gain + error, -20, 40);
        }

        return (Filter(gain) + ",aresample=48000", before, after);
    }

    /// <summary>Integrated loudness (LUFS) of <paramref name="input"/>'s audio after <paramref name="filter"/>.</summary>
    private static async Task<double> MeasureAsync(IFfmpegRunner ffmpeg, string input, string filter, CancellationToken cancellationToken)
    {
        var log = await ffmpeg.RunForLogAsync(["-i", input, "-vn", "-af", $"{filter},ebur128", "-f", "null", "-"], cancellationToken);
        var matches = IntegratedRegex().Matches(log);
        if (matches.Count == 0)
            throw new PipelineException("Could not measure loudness (no ebur128 summary).");
        // The summary at the end repeats I: once more; the last match is the final integrated value.
        var value = matches[^1].Groups[1].Value;
        return value == "-inf" ? -70 : double.Parse(value, CultureInfo.InvariantCulture); // -inf = silence
    }

    [GeneratedRegex(@"^\s+I:\s+(-?\d+(?:\.\d+)?|-inf) LUFS", RegexOptions.Multiline)]
    private static partial Regex IntegratedRegex();
}
