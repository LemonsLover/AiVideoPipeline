using System.Collections.Concurrent;
using Highlights.Core.Configuration;
using Highlights.Core.Pipeline;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Media;

/// <summary>
/// Finds ffmpeg/ffprobe/ffplay. Order: the tool's own setting, the Tools:FfmpegPath folder, a "tools\ffmpeg\bin"
/// folder next to the app or above it (portable/dev layout), PATH (process, user and machine — a process started
/// from an old shell may have a stale one), and WinGet's links folder. Only successful lookups are cached, and
/// settings are re-read each time, so fixing settings.json takes effect without restarting.
/// </summary>
public sealed class ToolLocator(IOptionsMonitor<ToolsOptions> options)
{
    private readonly ConcurrentDictionary<string, string> _found = new();

    public string Ffmpeg => Resolve("ffmpeg", o => o.FfmpegPath);
    public string Ffprobe => Resolve("ffprobe", o => o.FfprobePath);

    /// <summary>Used by the CLI to preview clips.</summary>
    public string Ffplay => Resolve("ffplay", _ => null);

    /// <summary>Same search for any other tool (e.g. yt-dlp); null when it isn't found anywhere.</summary>
    public string? TryFind(string tool, string? configured)
    {
        try
        {
            return Resolve(tool, _ => configured);
        }
        catch (PipelineException) when (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }
    }

    private string Resolve(string tool, Func<ToolsOptions, string?> ownSetting)
    {
        var o = options.CurrentValue;
        var own = ownSetting(o);
        var cacheKey = $"{tool}|{own}|{o.FfmpegPath}";
        if (_found.TryGetValue(cacheKey, out var cached) && File.Exists(cached))
            return cached;

        var exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        var settingName = $"Tools:{char.ToUpperInvariant(tool[0])}{tool[1..]}Path";

        // An explicit path for this tool must be right; silently using another copy would hide the mistake.
        if (!string.IsNullOrWhiteSpace(own))
        {
            var path = ToFile(own, exe);
            return File.Exists(path)
                ? _found[cacheKey] = path
                : throw new PipelineException($"{tool} not found at {settingName} = {path} ({HighlightsConfiguration.UserSettingsPath}).");
        }

        var tried = new List<string>();
        foreach (var dir in CandidateDirectories(o.FfmpegPath))
        {
            var path = Path.Combine(dir, exe);
            if (File.Exists(path))
                return _found[cacheKey] = path;
            tried.Add(dir);
        }

        throw new PipelineException(
            $"{tool} not found. Set Tools:FfmpegPath (the folder with ffmpeg.exe, ffprobe.exe) in " +
            $"{HighlightsConfiguration.UserSettingsPath}, or add ffmpeg to PATH.\n" +
            $"Searched: {string.Join("; ", tried.Distinct(StringComparer.OrdinalIgnoreCase).Take(12))}");
    }

    private static IEnumerable<string> CandidateDirectories(string? ffmpegSetting)
    {
        if (!string.IsNullOrWhiteSpace(ffmpegSetting))
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(ffmpegSetting), AppContext.BaseDirectory);
            yield return Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? full;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            yield return Path.Combine(dir.FullName, "tools", "ffmpeg", "bin");

        foreach (var dir in PathDirectories())
            yield return dir;

        if (OperatingSystem.IsWindows())
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links");
    }

    private static IEnumerable<string> PathDirectories()
    {
        IEnumerable<string?> sources = OperatingSystem.IsWindows()
            ?
            [
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            ]
            : [Environment.GetEnvironmentVariable("PATH")];

        return sources
            .SelectMany(p => (p ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(Environment.ExpandEnvironmentVariables)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string ToFile(string setting, string exe)
    {
        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(setting), AppContext.BaseDirectory);
        return Directory.Exists(path) ? Path.Combine(path, exe) : path;
    }
}
