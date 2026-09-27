using System.Globalization;
using Highlights.Core.Configuration;
using Highlights.Core.Pipeline;
using Highlights.Core.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Highlights.Core.Stages.Transcription;

/// <summary>Transcribes audio.wav with Whisper.net into transcript.json (segments + optional word timings).</summary>
public sealed class TranscribeStage(
    WhisperModelManager models, IOptions<WhisperOptions> options, ILogger<TranscribeStage> logger) : IPipelineStage
{
    private const int BytesPerSecond = ExtractStage.SampleRate * 2; // mono s16le

    public string Name => StageNames.Transcribe;
    public IReadOnlyList<string> DependsOn => [StageNames.Extract];
    public IReadOnlyList<string> Artifacts => [ProjectLayout.TranscriptFile];

    public string DescribeInputs(HighlightsProject project)
    {
        var o = options.Value;
        return string.Join('|', Model(project), Language(project), o.WordTimestamps, o.Prompt, o.NoContext,
            o.MaxConsecutiveRepeats, string.Join('/', o.EffectiveHallucinationFilters));
    }

    public async Task<IReadOnlyDictionary<string, string>> RunAsync(
        HighlightsProject project, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var model = Model(project);
        var language = Language(project);
        var modelPath = await models.EnsureAsync(model, progress, Name, cancellationToken);

        ConfigureRuntimes(o.EffectiveRuntimes);
        progress.Report(new StageProgress(Name, null, "loading model"));
        using var factory = WhisperFactory.FromPath(modelPath);
        var runtime = RuntimeOptions.LoadedLibrary;
        logger.LogInformation("Whisper runtime: {Runtime}", runtime);

        var builder = factory.CreateBuilder().WithLanguage(language);
        if (o.Threads > 0)
            builder.WithThreads(o.Threads);
        if (!string.IsNullOrWhiteSpace(o.Prompt))
            builder.WithPrompt(o.Prompt);
        if (o.WordTimestamps)
            builder.WithTokenTimestamps();
        if (o.NoContext)
            builder.WithNoContext();

        var audioPath = project.PathOf(ProjectLayout.AudioFile);
        var duration = Math.Max(1, (new FileInfo(audioPath).Length - 44) / (double)BytesPerSecond);
        var segments = new List<TranscriptSegment>();
        var filter = new HallucinationFilter(o.MaxConsecutiveRepeats, o.EffectiveHallucinationFilters);
        string? detected = null;

        progress.Report(new StageProgress(Name, 0, $"transcribing on {runtime}"));
        await using (var audio = File.OpenRead(audioPath))
        await using (var processor = builder.Build())
        {
            await foreach (var s in processor.ProcessAsync(audio, cancellationToken))
            {
                detected ??= s.Language;
                var text = s.Text.Trim();
                if (text.Length == 0 || filter.ShouldDrop(text))
                    continue;

                segments.Add(new TranscriptSegment
                {
                    Start = Round(s.Start.TotalSeconds),
                    End = Round(s.End.TotalSeconds),
                    Text = text,
                    Probability = MeanProbability(s.Tokens),
                    Words = o.WordTimestamps ? BuildWords(s.Tokens) : null,
                });
                progress.Report(new StageProgress(Name, Math.Min(1, s.End.TotalSeconds / duration),
                    $"{s.End:hh\\:mm\\:ss} {Shorten(text)}"));
            }
        }

        var transcript = new Transcript
        {
            Model = model,
            Language = language,
            DetectedLanguage = detected,
            Runtime = runtime?.ToString(),
            DurationSeconds = Round(duration),
            Segments = segments,
        };
        await JsonDefaults.WriteAtomicAsync(project.PathOf(ProjectLayout.TranscriptFile), transcript, cancellationToken);

        progress.Report(new StageProgress(Name, 1, $"{segments.Count} segments"));
        return new Dictionary<string, string>
        {
            ["model"] = model,
            ["runtime"] = runtime?.ToString() ?? "unknown",
            ["language"] = detected ?? language,
            ["segments"] = segments.Count.ToString(CultureInfo.InvariantCulture),
            ["droppedSegments"] = filter.Dropped.ToString(CultureInfo.InvariantCulture),
        };
    }

    private string Model(HighlightsProject project) => project.Settings.WhisperModel ?? options.Value.Model;
    private string Language(HighlightsProject project) => project.Settings.Language ?? options.Value.Language;

    /// <summary>The native library is loaded once per process, so the order only matters before the first load.</summary>
    private static void ConfigureRuntimes(IReadOnlyList<RuntimeLibrary> order)
    {
        if (RuntimeOptions.LoadedLibrary is null)
            RuntimeOptions.RuntimeLibraryOrder = [.. order];
    }

    /// <summary>
    /// Merges BPE tokens into words: a token starting with a space begins a new word.
    /// Token times are in 10 ms units; special tokens ([_BEG_], [_TT_..], &lt;|..|&gt;) are skipped.
    /// </summary>
    internal static IReadOnlyList<TranscriptWord> BuildWords(IEnumerable<WhisperToken>? tokens)
    {
        var words = new List<TranscriptWord>();
        if (tokens is null)
            return words;

        string? text = null;
        long start = 0, end = 0;
        double probSum = 0;
        var count = 0;

        void Flush()
        {
            if (text is not null && text.Trim().Length > 0)
                words.Add(new TranscriptWord(start / 100.0, end / 100.0, text.Trim(), Round(probSum / count)));
            text = null;
        }

        foreach (var t in tokens.Where(IsTextToken))
        {
            if (text is null || t.Text!.StartsWith(' '))
            {
                Flush();
                text = t.Text;
                start = t.Start;
                end = t.End;
                probSum = 0;
                count = 0;
            }
            else
            {
                text += t.Text;
            }

            end = Math.Max(end, t.End);
            probSum += t.Probability;
            count++;
        }

        Flush();
        return words;
    }

    /// <summary>SegmentData.Probability is only filled with WithProbabilities(); token probabilities are always there.</summary>
    private static double? MeanProbability(IEnumerable<WhisperToken>? tokens)
    {
        var text = tokens?.Where(IsTextToken).ToList();
        return text is { Count: > 0 } ? Round(text.Average(t => t.Probability)) : null;
    }

    private static bool IsTextToken(WhisperToken t) =>
        !string.IsNullOrEmpty(t.Text)
        && !t.Text.StartsWith("[_", StringComparison.Ordinal)
        && !t.Text.StartsWith("<|", StringComparison.Ordinal);

    private static double Round(double value) => Math.Round(value, 3);

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..57] + "...";
}
