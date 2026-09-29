using System.IO.Compression;
using Highlights.Core.Configuration;
using Highlights.Core.Models;
using Highlights.Core.Pipeline;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;

namespace Highlights.Core.Stages.Signals;

/// <summary>Locates/downloads the YAMNet ONNX model (zip with yamnet.onnx, yamnet.data, labels.txt).</summary>
public sealed class YamnetModelManager(ModelDownloader downloader, IOptionsMonitor<AudioSignalsOptions> signals, IOptionsMonitor<ModelsOptions> models)
{
    public const string ModelFile = "yamnet.onnx";
    public const string LabelsFile = "labels.txt";

    /// <summary>Model directory is keyed by the zip name, so a new URL gets its own folder.</summary>
    public string ModelDirectory =>
        Path.Combine(models.CurrentValue.EffectiveDirectory, Path.GetFileNameWithoutExtension(new Uri(signals.CurrentValue.YamnetModelUrl).LocalPath));

    public async Task<string> EnsureAsync(IProgress<StageProgress> progress, string stage, CancellationToken cancellationToken)
    {
        var dir = ModelDirectory;
        bool Complete() => File.Exists(Path.Combine(dir, ModelFile)) && File.Exists(Path.Combine(dir, LabelsFile));
        if (Complete())
            return dir;
        if (ModelsOptions.TryAdoptLegacy(dir, models.CurrentValue.EffectiveDirectory) && Complete())
            return dir;

        var zip = dir + ".zip";
        await downloader.DownloadAsync(new Uri(signals.CurrentValue.YamnetModelUrl), zip, progress, stage, cancellationToken);
        try
        {
            // Files may be nested in a folder inside the zip; flatten them into the model directory.
            Directory.CreateDirectory(dir);
            using var archive = ZipFile.OpenRead(zip);
            foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0))
                entry.ExtractToFile(Path.Combine(dir, entry.Name), overwrite: true);
        }
        finally
        {
            File.Delete(zip);
        }

        if (!File.Exists(Path.Combine(dir, ModelFile)) || !File.Exists(Path.Combine(dir, LabelsFile)))
            throw new PipelineException($"YAMNet archive has no {ModelFile}/{LabelsFile}: {signals.CurrentValue.YamnetModelUrl}");
        return dir;
    }
}

/// <summary>Runs YAMNet on 96×64 log-mel patches and returns per-class scores (probabilities).</summary>
internal sealed class YamnetClassifier : IDisposable
{
    public const int PatchFrames = 96;
    public const int PatchHopFrames = 48;

    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly string _output;

    public YamnetClassifier(string modelDirectory)
    {
        // ONNX Runtime checks that yamnet.data lies inside the model's folder by comparing real (final) paths.
        // Under file-system redirection (e.g. launched from a packaged app, where AppData is virtualized) the
        // logical folder differs from the real one, so load the model through its real path.
        var model = FinalPath(Path.Combine(modelDirectory, YamnetModelManager.ModelFile));
        _session = new InferenceSession(model);
        _input = _session.InputMetadata.Keys.Single();
        _output = _session.OutputMetadata.Keys.First();
        Labels = File.ReadAllLines(Path.Combine(modelDirectory, YamnetModelManager.LabelsFile))
            .Where(l => l.Length > 0).ToArray();
    }

    public IReadOnlyList<string> Labels { get; }

    /// <summary>The path Windows actually opens (resolves redirection, links, 8.3 names); the input elsewhere.</summary>
    private static string FinalPath(string path)
    {
        if (!OperatingSystem.IsWindows())
            return path;
        using var stream = File.OpenRead(path);
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
            return path;
        var final = new string(buffer, 0, (int)length);
        // "\\?\C:\..." → "C:\...", "\\?\UNC\server\share" → "\\server\share".
        return final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + final[8..]
            : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..]
            : final;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, char[] path, uint length, uint flags);

    /// <summary>Scores for one patch (96 frames × 64 mel bands, row-major). Thread-safe.</summary>
    public float[] Classify(float[] patch)
    {
        using var input = OrtValue.CreateTensorValueFromMemory(patch, [1, 1, PatchFrames, LogMelSpectrogram.MelBands]);
        using var results = _session.Run(new RunOptions(), [_input], [input], [_output]);
        var scores = results[0].GetTensorDataAsSpan<float>().ToArray();

        // Some exports return logits instead of sigmoid probabilities.
        if (scores.Any(s => s is < 0 or > 1))
            for (var i = 0; i < scores.Length; i++)
                scores[i] = 1f / (1f + MathF.Exp(-scores[i]));
        return scores;
    }

    public void Dispose() => _session.Dispose();
}
