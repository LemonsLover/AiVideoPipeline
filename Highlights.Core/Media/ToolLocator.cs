using Highlights.Core.Configuration;
using Highlights.Core.Pipeline;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Media;

/// <summary>Resolves ffmpeg/ffprobe from configuration (file or directory) or PATH.</summary>
public sealed class ToolLocator(IOptions<ToolsOptions> options)
{
    private readonly Lazy<string> _ffmpeg = new(() => Resolve("ffmpeg", options.Value.FfmpegPath, null));
    private readonly Lazy<string> _ffprobe = new(() => Resolve("ffprobe", options.Value.FfprobePath, options.Value.FfmpegPath));

    public string Ffmpeg => _ffmpeg.Value;
    public string Ffprobe => _ffprobe.Value;

    private static string Resolve(string tool, string? configured, string? siblingOf)
    {
        var exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;

        foreach (var candidate in new[] { configured, SiblingDirectory(siblingOf) })
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate), AppContext.BaseDirectory);
            if (Directory.Exists(path))
                path = Path.Combine(path, exe);
            if (File.Exists(path))
                return path;
            if (candidate == configured)
                throw new PipelineException($"{tool} not found at configured path: {path}");
        }

        var fromPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dir => Path.Combine(dir, exe))
            .FirstOrDefault(File.Exists);

        return fromPath ?? throw new PipelineException(
            $"{tool} not found. Set Tools:{char.ToUpperInvariant(tool[0])}{tool[1..]}Path in appsettings.json or add it to PATH.");
    }

    private static string? SiblingDirectory(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return null;
        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured), AppContext.BaseDirectory);
        return Directory.Exists(path) ? path : Path.GetDirectoryName(path);
    }
}
