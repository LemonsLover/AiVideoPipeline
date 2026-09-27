using Whisper.net.LibraryLoader;

namespace Highlights.Core.Configuration;

public sealed class WhisperOptions
{
    public const string SectionName = "Whisper";

    /// <summary>ggml model name (e.g. "large-v3-turbo", "medium") or a path to a .bin file.</summary>
    public string Model { get; set; } = "large-v3-turbo";

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

    /// <summary>
    /// Don't condition on previously decoded text. Prevents the repetition loops Whisper falls into
    /// on long noisy recordings (game sounds between phrases).
    /// </summary>
    public bool NoContext { get; set; } = true;

    /// <summary>A segment is dropped when its text already occurs this many times among the last 6 segments.</summary>
    public int MaxConsecutiveRepeats { get; set; } = 2;

    /// <summary>Known Whisper hallucinations (subtitle credits etc.); segments containing them are dropped.</summary>
    public string[]? HallucinationFilters { get; set; }

    public IReadOnlyList<string> EffectiveHallucinationFilters => HallucinationFilters ??
    [
        "Продолжение следует",
        "Субтитры сделал",
        "Субтитры создавал",
        "Редактор субтитров",
        "Спасибо за просмотр",
        "ПОДПИШИСЬ",
        "DimaTorzok",
        "Thanks for watching",
    ];

    public IReadOnlyList<RuntimeLibrary> EffectiveRuntimes =>
        Runtimes is { Length: > 0 } ? Runtimes : [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu];
}
