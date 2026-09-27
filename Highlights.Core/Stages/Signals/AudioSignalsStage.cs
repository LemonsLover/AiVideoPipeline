using System.Globalization;
using Highlights.Core.Configuration;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Stages.Signals;

/// <summary>
/// Computes loudness over time and YAMNet sound-class scores (laughter, screams, ...) for the voice track,
/// and derives events from them. Writes signals.json.
/// </summary>
public sealed class AudioSignalsStage(YamnetModelManager models, IOptions<AudioSignalsOptions> options) : IPipelineStage
{
    public string Name => StageNames.AudioSignals;
    public IReadOnlyList<string> DependsOn => [StageNames.Extract];
    public IReadOnlyList<string> Artifacts => [ProjectLayout.SignalsFile];

    public string DescribeInputs(HighlightsProject project)
    {
        var o = options.Value;
        static string Describe(IReadOnlyDictionary<string, string[]> groups) =>
            string.Join(';', groups.OrderBy(g => g.Key).Select(g => $"{g.Key}={string.Join(',', g.Value)}"));
        return string.Join('|', o.YamnetModelUrl, Describe(o.EffectiveClassGroups), Describe(o.EffectiveContextGroups), o.LoudnessWindowSeconds, o.LoudSpikeDb, o.BaselineWindowSeconds,
            o.MinSpikeDb, o.EventThreshold, o.MergeGapSeconds);
    }

    public async Task<IReadOnlyDictionary<string, string>> RunAsync(
        HighlightsProject project, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var modelDir = await models.EnsureAsync(progress, Name, cancellationToken);

        progress.Report(new StageProgress(Name, 0, "reading audio"));
        var (samples, sampleRate) = await WavReader.ReadMonoPcm16Async(project.PathOf(ProjectLayout.AudioFile), cancellationToken);
        if (sampleRate != LogMelSpectrogram.SampleRate)
            throw new PipelineException($"audio.wav must be {LogMelSpectrogram.SampleRate} Hz, got {sampleRate}. Re-run extract.");
        var duration = samples.Length / (double)sampleRate;

        var loudness = ComputeLoudness(samples, sampleRate, o.LoudnessWindowSeconds);
        var events = new List<SignalEvent>(FindLoudSpikes(loudness, o));

        progress.Report(new StageProgress(Name, 0.05, "computing spectrogram"));
        var mel = LogMelSpectrogram.Compute(samples, out var frames);
        cancellationToken.ThrowIfCancellationRequested();

        using var classifier = new YamnetClassifier(modelDir);
        var eventGroups = o.EffectiveClassGroups;
        var allGroups = eventGroups.Concat(o.EffectiveContextGroups.Where(g => !eventGroups.ContainsKey(g.Key)))
            .ToDictionary(g => g.Key, g => g.Value);
        var groupIndices = ResolveGroups(classifier.Labels, allGroups);
        var groups = await ClassifyAsync(classifier, mel, frames, groupIndices, progress, cancellationToken);

        var classTrack = new ClassTrack(
            Round(YamnetClassifier.PatchHopFrames * LogMelSpectrogram.FrameSeconds),
            Round(YamnetClassifier.PatchFrames * LogMelSpectrogram.FrameSeconds),
            groups);
        foreach (var name in eventGroups.Keys)
            events.AddRange(FindClassEvents(name, groups[name], classTrack, o));
        events.Sort((a, b) => a.Start.CompareTo(b.Start));

        var result = new AudioSignals
        {
            Model = Path.GetFileName(modelDir),
            DurationSeconds = Round(duration),
            Loudness = loudness,
            Classes = classTrack,
            Events = events,
        };
        await JsonDefaults.WriteAtomicAsync(project.PathOf(ProjectLayout.SignalsFile), result, cancellationToken);

        progress.Report(new StageProgress(Name, 1, $"{events.Count} events"));
        var details = new Dictionary<string, string>
        {
            ["model"] = result.Model,
            ["medianDb"] = loudness.MedianDb.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var type in events.GroupBy(e => e.Type))
            details[$"events.{type.Key}"] = type.Count().ToString(CultureInfo.InvariantCulture);
        return details;
    }

    internal static LoudnessTrack ComputeLoudness(float[] samples, int sampleRate, double windowSeconds)
    {
        var window = Math.Max(1, (int)Math.Round(windowSeconds * sampleRate));
        var db = new float[samples.Length / window];
        for (var i = 0; i < db.Length; i++)
        {
            double sum = 0;
            foreach (var s in samples.AsSpan(i * window, window))
                sum += s * s;
            db[i] = (float)Math.Round(Math.Max(-100, 10 * Math.Log10(sum / window + 1e-10)), 1);
        }

        var sorted = db.Order().ToArray();
        return new LoudnessTrack(
            window / (double)sampleRate,
            sorted.Length == 0 ? -100 : sorted[sorted.Length / 2],
            sorted.Length == 0 ? -100 : sorted[(int)(sorted.Length * 0.95)],
            db);
    }

    /// <summary>Spikes: 0.5 s energy-smoothed level exceeding a rolling median baseline by LoudSpikeDb.</summary>
    internal static IEnumerable<SignalEvent> FindLoudSpikes(LoudnessTrack loudness, AudioSignalsOptions o)
    {
        var db = loudness.Db;
        var step = loudness.StepSeconds;
        if (db.Length == 0)
            yield break;

        var smoothRadius = Math.Max(0, (int)Math.Round(0.25 / step));
        var smoothed = new double[db.Length];
        for (var i = 0; i < db.Length; i++)
        {
            int from = Math.Max(0, i - smoothRadius), to = Math.Min(db.Length - 1, i + smoothRadius);
            double energy = 0;
            for (var j = from; j <= to; j++)
                energy += Math.Pow(10, db[j] / 10);
            smoothed[i] = 10 * Math.Log10(energy / (to - from + 1));
        }

        // Rolling median, recomputed once per second of audio.
        var halfWindow = Math.Max(1, (int)(o.BaselineWindowSeconds / step / 2));
        var baselineStep = Math.Max(1, (int)Math.Round(1 / step));
        var baseline = new double[db.Length];
        for (var c = 0; c < db.Length; c += baselineStep)
        {
            int from = Math.Max(0, c - halfWindow), to = Math.Min(db.Length, c + halfWindow);
            var slice = db[from..to];
            Array.Sort(slice);
            var median = slice[slice.Length / 2];
            for (var i = c; i < Math.Min(db.Length, c + baselineStep); i++)
                baseline[i] = median;
        }

        var intervals = new List<(int Start, int End, double Peak)>();
        for (var i = 0; i < db.Length; i++)
        {
            var excess = smoothed[i] - baseline[i];
            if (excess < o.LoudSpikeDb || smoothed[i] < o.MinSpikeDb)
                continue;
            if (intervals.Count > 0 && intervals[^1].End == i)
                intervals[^1] = (intervals[^1].Start, i + 1, Math.Max(intervals[^1].Peak, excess));
            else
                intervals.Add((i, i + 1, excess));
        }

        foreach (var e in Merge(intervals.Select(x => new SignalEvent("loud", x.Start * step, x.End * step, x.Peak)), o.MergeGapSeconds))
            yield return e;
    }

    internal static IEnumerable<SignalEvent> FindClassEvents(string type, float[] scores, ClassTrack track, AudioSignalsOptions o) =>
        Merge(scores
            .Select((score, i) => (score, i))
            .Where(x => x.score >= o.EventThreshold)
            .Select(x => new SignalEvent(type, x.i * track.StepSeconds, x.i * track.StepSeconds + track.WindowSeconds, x.score)),
            o.MergeGapSeconds);

    private static IEnumerable<SignalEvent> Merge(IEnumerable<SignalEvent> ordered, double gap)
    {
        SignalEvent? current = null;
        foreach (var e in ordered)
        {
            if (current is not null && e.Start - current.End <= gap)
            {
                current = current with { End = Math.Max(current.End, e.End), Peak = Math.Max(current.Peak, e.Peak) };
                continue;
            }
            if (current is not null)
                yield return Rounded(current);
            current = e;
        }
        if (current is not null)
            yield return Rounded(current);
    }

    private static SignalEvent Rounded(SignalEvent e) => e with
    {
        Start = Round(e.Start), End = Round(e.End), Peak = Math.Round(e.Peak, 2),
    };

    private static Dictionary<string, int[]> ResolveGroups(IReadOnlyList<string> labels, IReadOnlyDictionary<string, string[]> groups)
    {
        var index = labels.Select((l, i) => (l, i)).ToDictionary(x => x.l, x => x.i, StringComparer.OrdinalIgnoreCase);
        return groups.ToDictionary(g => g.Key, g => g.Value.Select(name => index.TryGetValue(name, out var i)
            ? i
            : throw new PipelineException($"AudioSignals:ClassGroups:{g.Key}: unknown YAMNet class '{name}' (see labels.txt).")).ToArray());
    }

    private static async Task<IReadOnlyDictionary<string, float[]>> ClassifyAsync(
        YamnetClassifier classifier, float[] mel, int frames, Dictionary<string, int[]> groups,
        IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        const int patchSize = YamnetClassifier.PatchFrames * LogMelSpectrogram.MelBands;
        var patches = frames < YamnetClassifier.PatchFrames ? 0 : 1 + (frames - YamnetClassifier.PatchFrames) / YamnetClassifier.PatchHopFrames;
        var result = groups.ToDictionary(g => g.Key, _ => new float[patches]);
        var done = 0;

        await Parallel.ForAsync(0, patches,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
            (p, _) =>
            {
                var patch = mel.AsSpan(p * YamnetClassifier.PatchHopFrames * LogMelSpectrogram.MelBands, patchSize).ToArray();
                var scores = classifier.Classify(patch);
                foreach (var (name, indices) in groups)
                    result[name][p] = (float)Math.Round(indices.Max(i => scores[i]), 3);

                var n = Interlocked.Increment(ref done);
                if (n % 200 == 0 || n == patches)
                    progress.Report(new StageProgress(StageNames.AudioSignals, 0.1 + 0.9 * n / patches,
                        $"classifying sounds {n}/{patches}"));
                return ValueTask.CompletedTask;
            });

        return result;
    }

    private static double Round(double value) => Math.Round(value, 3);
}
