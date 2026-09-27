namespace Highlights.Core.Stages.Transcription;

/// <summary>
/// Drops typical Whisper hallucinations: phrases looping over recent segments (including alternating
/// loops like A B A B) and known phantom phrases (subtitle credits and the like) on silence or noise.
/// </summary>
internal sealed class HallucinationFilter(int maxRepeats, IReadOnlyList<string> phrases)
{
    private const int RecentWindow = 6;
    private readonly Queue<string> _recent = new();

    public int Dropped { get; private set; }

    public bool ShouldDrop(string text)
    {
        var normalized = Normalize(text);
        var repeats = _recent.Count(r => r == normalized);

        _recent.Enqueue(normalized);
        if (_recent.Count > RecentWindow)
            _recent.Dequeue();

        var drop = normalized.Length == 0
                   || (maxRepeats > 0 && repeats >= maxRepeats)
                   || phrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (drop)
            Dropped++;
        return drop;
    }

    /// <summary>Lowercase letters/digits only, so "- Да." and "да" count as the same phrase.</summary>
    internal static string Normalize(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
