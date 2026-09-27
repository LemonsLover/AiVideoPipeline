using Whisper.net.LibraryLoader;

namespace Highlights.Core.Configuration;

public sealed class WhisperOptions
{
    public const string SectionName = "Whisper";

    /// <summary>ggml model name (e.g. "large-v3-turbo", "medium") or a path to a .bin file.</summary>
    public string Model { get; set; } = "large-v3-turbo";

    /// <summary>Where downloaded models are stored. Defaults to %LOCALAPPDATA%\Highlights\models.</summary>
    public string? ModelsDirectory { get; set; }

    /// <summary>Base URL the ggml-&lt;model&gt;.bin files are downloaded from.</summary>
    public string ModelBaseUrl { get; set; } = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    /// <summary>Spoken language code ("ru", "en", ...) or "auto" to detect it once for the whole file.</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Native runtimes in order of preference; the first one that loads is used.</summary>
    public RuntimeLibrary[]? Runtimes { get; set; }

    /// <summary>CPU threads; 0 lets whisper.cpp decide.</summary>
    public int Threads { get; set; }

    /// <summary>Optional initial prompt: names, slang, game terms that help recognition.</summary>
    public string? Prompt { get; set; }

    /// <summary>Store per-word timestamps in transcript.json.</summary>
    public bool WordTimestamps { get; set; } = true;

    public IReadOnlyList<RuntimeLibrary> EffectiveRuntimes =>
        Runtimes is { Length: > 0 } ? Runtimes : [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu];

    public string EffectiveModelsDirectory =>
        string.IsNullOrWhiteSpace(ModelsDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Highlights", "models")
            : Environment.ExpandEnvironmentVariables(ModelsDirectory);
}
