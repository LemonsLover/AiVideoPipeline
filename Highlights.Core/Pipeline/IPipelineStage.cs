using Highlights.Core.Projects;

namespace Highlights.Core.Pipeline;

public interface IPipelineStage
{
    string Name { get; }

    /// <summary>Stages that must be up to date before this one runs.</summary>
    IReadOnlyList<string> DependsOn { get; }

    /// <summary>Artifact file names (relative to the project directory) this stage produces.</summary>
    IReadOnlyList<string> Artifacts { get; }

    /// <summary>
    /// Canonical description of the stage parameters. Any change here (together with upstream changes)
    /// makes the stage out of date. Throws <see cref="PipelineException"/> if the stage cannot run.
    /// </summary>
    string DescribeInputs(HighlightsProject project);

    /// <summary>Runs the stage and returns details to store in project.json.</summary>
    Task<IReadOnlyDictionary<string, string>> RunAsync(
        HighlightsProject project, IProgress<StageProgress> progress, CancellationToken cancellationToken);
}
