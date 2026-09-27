using Highlights.Core.Pipeline;
using Spectre.Console;

namespace Highlights.Cli.Commands;

internal static class CommandHandler
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int Cancelled = 130;

    /// <summary>Runs a command body, turning expected failures into readable messages and exit codes.</summary>
    public static async Task<int> RunAsync(Func<Task<int>> body)
    {
        try
        {
            return await body();
        }
        catch (PipelineException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Error:[/] {ex.Message}");
            return Error;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return Cancelled;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
            return Error;
        }
    }
}
