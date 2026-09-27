using Highlights.Core.Pipeline;
using Spectre.Console;

namespace Highlights.Cli.Rendering;

/// <summary>Maps <see cref="StageProgress"/> reports to Spectre progress tasks (one per stage/step).</summary>
internal sealed class SpectreProgressReporter(ProgressContext context) : IProgress<StageProgress>
{
    private const int MaxMessageLength = 70;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, ProgressTask> _tasks = [];

    public void Report(StageProgress value)
    {
        lock (_lock)
        {
            var label = value.Step is null ? value.Stage : $"{value.Stage} · {value.Step}";
            if (!_tasks.TryGetValue(label, out var task))
                _tasks[label] = task = context.AddTask(label, maxValue: 100);

            if (value.Fraction is { } fraction)
            {
                task.IsIndeterminate = false;
                task.Value = fraction * 100;
            }
            else
            {
                task.IsIndeterminate = true;
            }

            var message = value.Message is { Length: > MaxMessageLength } m ? m[..(MaxMessageLength - 1)] + "…" : value.Message;
            task.Description = message is null
                ? $"[bold]{Markup.Escape(label)}[/]"
                : $"[bold]{Markup.Escape(label)}[/] [grey]{Markup.Escape(message)}[/]";
        }
    }

    /// <summary>Shows a progress display while <paramref name="work"/> runs.</summary>
    public static Task RunAsync(Func<IProgress<StageProgress>, Task> work) =>
        AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new TaskDescriptionColumn { Alignment = Justify.Left }, new ProgressBarColumn(),
                new PercentageColumn(), new ElapsedTimeColumn(), new SpinnerColumn())
            .StartAsync(ctx => work(new SpectreProgressReporter(ctx)));
}
