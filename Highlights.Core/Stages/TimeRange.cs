namespace Highlights.Core.Stages;

/// <summary>A range of source-video time in seconds.</summary>
public sealed record TimeRange(double Start, double End)
{
    public double Duration => End - Start;

    public bool Overlaps(TimeRange other) => Start < other.End && other.Start < End;

    /// <summary><paramref name="range"/> minus <paramref name="cuts"/>, in order.</summary>
    public static List<TimeRange> Subtract(TimeRange range, IEnumerable<TimeRange> cuts)
    {
        var result = new List<TimeRange>();
        var cursor = range.Start;
        foreach (var cut in cuts.Where(c => c.Overlaps(range)).OrderBy(c => c.Start))
        {
            if (cut.Start > cursor)
                result.Add(new TimeRange(cursor, Math.Min(cut.Start, range.End)));
            cursor = Math.Max(cursor, cut.End);
        }
        if (cursor < range.End)
            result.Add(new TimeRange(cursor, range.End));
        return result;
    }
}
