using System.Globalization;
using System.Text;
using CliWrap;
using Highlights.Core.Pipeline;

namespace Highlights.Core.Media;

public interface IFfmpegRunner
{
    /// <summary>
    /// Runs ffmpeg with <paramref name="arguments"/> and reports progress as a fraction of
    /// <paramref name="totalSeconds"/> (parsed from <c>-progress pipe:1</c>).
    /// </summary>
    Task RunAsync(IReadOnlyList<string> arguments, double totalSeconds, IProgress<double>? progress,
        CancellationToken cancellationToken = default);
}

public sealed class FfmpegRunner(ToolLocator tools) : IFfmpegRunner
{
    public async Task RunAsync(IReadOnlyList<string> arguments, double totalSeconds, IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        string[] args = ["-hide_banner", "-nostdin", "-nostats", "-loglevel", "error", "-progress", "pipe:1", .. arguments];
        var stderr = new StringBuilder();

        var result = await Cli.Wrap(tools.Ffmpeg)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line => OnProgressLine(line, totalSeconds, progress)))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
            .ExecuteAsync(cancellationToken);

        if (result.ExitCode != 0)
            throw new PipelineException($"ffmpeg exited with code {result.ExitCode}: {stderr.ToString().Trim()}");

        progress?.Report(1);
    }

    private static void OnProgressLine(string line, double totalSeconds, IProgress<double>? progress)
    {
        // "out_time_us" is microseconds ("out_time_ms" is, historically, also microseconds).
        const string key = "out_time_us=";
        if (progress is null || totalSeconds <= 0 || !line.StartsWith(key, StringComparison.Ordinal))
            return;
        if (long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
            progress.Report(Math.Clamp(us / 1_000_000.0 / totalSeconds, 0, 1));
    }
}
