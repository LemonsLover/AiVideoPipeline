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
        CancellationToken cancellationToken = default, string? workingDirectory = null);

    /// <summary>Runs ffmpeg and returns true when it exits successfully (for capability probes).</summary>
    Task<bool> TryRunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);

    /// <summary>Runs ffmpeg at info log level and returns stderr (for analysis filters that print results).</summary>
    Task<string> RunForLogAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

public sealed class FfmpegRunner(ToolLocator tools) : IFfmpegRunner
{
    public async Task<string> RunForLogAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var stderr = new StringBuilder();
        var result = await Cli.Wrap(tools.Ffmpeg)
            .WithArguments(["-hide_banner", "-nostdin", "-nostats", "-loglevel", "info", .. arguments])
            .WithValidation(CommandResultValidation.None)
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
            .ExecuteAsync(cancellationToken);
        if (result.ExitCode != 0)
            throw new PipelineException($"ffmpeg exited with code {result.ExitCode}: {Tail(stderr.ToString())}");
        return stderr.ToString();
    }

    private static string Tail(string log) => log.Length <= 2000 ? log.Trim() : log[^2000..].Trim();

    public async Task<bool> TryRunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var result = await Cli.Wrap(tools.Ffmpeg)
            .WithArguments(["-hide_banner", "-nostdin", "-loglevel", "error", .. arguments])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task RunAsync(IReadOnlyList<string> arguments, double totalSeconds, IProgress<double>? progress,
        CancellationToken cancellationToken = default, string? workingDirectory = null)
    {
        string[] args = ["-hide_banner", "-nostdin", "-nostats", "-loglevel", "error", "-progress", "pipe:1", .. arguments];
        var stderr = new StringBuilder();

        var result = await Cli.Wrap(tools.Ffmpeg)
            .WithArguments(args)
            .WithWorkingDirectory(workingDirectory ?? Environment.CurrentDirectory)
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
