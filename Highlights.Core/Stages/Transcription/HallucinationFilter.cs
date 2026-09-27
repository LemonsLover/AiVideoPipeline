namespace Highlights.Core.Stages.Transcription;

/// <summary>
/// Drops typical Whisper hallucinations: the same phrase repeated in a loop and known phantom phrases
/// (subtitle credits and the like) that appear on silence or noise.
/// </summary>
internal sealed class HallucinationFilter(int maxConsecutiveRepeats, IReadOnlyList<string> phrases)
{
    private string? _last;
    private int _repeats;

    public int Dropped { get; private set; }

    public bool ShouldDrop(string text)
    {
        var normalized = Normalize(text);
        if (normalized == _last)
            _repeats++;
        else
            (_last, _repeats) = (normalized, 1);

        var drop = normalized.Length == 0
                   || (maxConsecutiveRepeats > 0 && _repeats > maxConsecutiveRepeats)
                   || phrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (drop)
            Dropped++;
        return drop;
    }

    /// <summary>Lowercase letters/digits only, so "- Да." and "да" count as the same phrase.</summary>
    internal static string Normalize(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
