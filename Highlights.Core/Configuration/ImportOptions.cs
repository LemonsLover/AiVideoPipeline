namespace Highlights.Core.Configuration;

/// <summary>Importing videos by link (YouTube and anything else yt-dlp supports).</summary>
public sealed class ImportOptions
{
    public const string SectionName = "Import";

    /// <summary>Where downloaded videos go; empty = %USERPROFILE%\Videos\Highlights.</summary>
    public string? DownloadDirectory { get; set; }

    /// <summary>Path to yt-dlp.exe or its folder; empty = PATH, else a copy downloaded on first use to %USERPROFILE%\.highlights\tools.</summary>
    public string? YtDlpPath { get; set; }

    /// <summary>Where the managed copy is downloaded from (official release).</summary>
    public string YtDlpUrl { get; set; } = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    /// <summary>Run "yt-dlp -U" on the managed copy once per session: YouTube changes break old versions often.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>yt-dlp format: best video up to 1080p (the render resolution) + best audio.</summary>
    public string Format { get; set; } = "bv*[height<=1080]+ba/b[height<=1080]/bv*+ba/b";

    /// <summary>Only for private videos: browser to take cookies from ("chrome", "edge", "firefox"). Unlisted links don't need it.</summary>
    public string? CookiesFromBrowser { get; set; }

    public string EffectiveDownloadDirectory =>
        string.IsNullOrWhiteSpace(DownloadDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Highlights")
            : Environment.ExpandEnvironmentVariables(DownloadDirectory);
}
