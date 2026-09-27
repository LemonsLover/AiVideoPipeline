using System.CommandLine;

namespace Highlights.Cli.Commands;

internal static class CommonArguments
{
    public static Argument<string> Project() => new("project")
    {
        Description = "Video file or project directory (default: current directory)",
        Arity = ArgumentArity.ZeroOrOne,
        DefaultValueFactory = _ => ".",
    };

    public static Option<string?> Mode() => new("--mode")
    {
        Description = "Editing mode for this project: story, funny, bestof, ... (file name in Modes/, saved)",
    };

    public static Option<bool> DryRun(string file) => new("--dry-run")
    {
        Description = $"Build the prompt and save it to {file} without calling the LLM",
    };

    public static Option<bool> Force() => new("--force", "-f")
    {
        Description = "Re-run the stage even if it is up to date",
    };
}
