using System.Security.Cryptography;
using System.Text;
using Highlights.Core.Projects;
using Microsoft.Extensions.Logging;

namespace Highlights.Core.Pipeline;

public enum StageOutcome { Completed, Skipped }

/// <summary>
/// Runs stages with dependency resolution. A stage is up to date when it completed with the same
/// inputs hash (own parameters + upstream hashes) and its artifacts exist; such stages are skipped
/// unless forced. Missing or outdated upstream stages are run automatically.
/// </summary>
public sealed class PipelineRunner(IEnumerable<IPipelineStage> stages, IProjectStore store, ILogger<PipelineRunner> logger)
{
    private readonly Dictionary<string, IPipelineStage> _stages =
        stages.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    public IPipelineStage GetStage(string name) =>
        _stages.TryGetValue(name, out var stage) ? stage : throw new PipelineException($"Unknown stage '{name}'.");

    public string ComputeInputsHash(HighlightsProject project, IPipelineStage stage)
    {
        var sb = new StringBuilder().Append(stage.Name).Append('\n').Append(stage.DescribeInputs(project));
        foreach (var dep in stage.DependsOn)
            sb.Append('\n').Append(ComputeInputsHash(project, GetStage(dep)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    public bool IsUpToDate(HighlightsProject project, IPipelineStage stage)
    {
        if (!project.Stages.TryGetValue(stage.Name, out var state) || state.Status != StageStatus.Completed)
            return false;
        if (stage.Artifacts.Any(a => !File.Exists(project.PathOf(a))))
            return false;
        try
        {
            return state.InputsHash == ComputeInputsHash(project, stage)
                   && stage.DependsOn.All(d => IsUpToDate(project, GetStage(d)));
        }
        catch (PipelineException)
        {
            return false;
        }
    }

    public async Task<StageOutcome> RunAsync(
        HighlightsProject project, string stageName, bool force,
        IProgress<StageProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        progress ??= new Progress<StageProgress>();
        var stage = GetStage(stageName);

        foreach (var dep in stage.DependsOn)
            await RunAsync(project, dep, force: false, progress, cancellationToken);

        if (!force && IsUpToDate(project, stage))
        {
            progress.Report(new StageProgress(stage.Name, 1, "up to date"));
            return StageOutcome.Skipped;
        }

        var hash = ComputeInputsHash(project, stage);
        var state = project.GetStage(stage.Name);
        state.Status = StageStatus.Running;
        state.StartedAt = DateTimeOffset.Now;
        state.CompletedAt = null;
        state.Error = null;
        await store.SaveAsync(project, cancellationToken);

        logger.LogInformation("Running stage {Stage} for {Project}", stage.Name, project.Directory);
        try
        {
            state.Details = new Dictionary<string, string>(await stage.RunAsync(project, progress, cancellationToken));
            state.Status = StageStatus.Completed;
            state.InputsHash = hash;
            state.CompletedAt = DateTimeOffset.Now;
        }
        catch (OperationCanceledException)
        {
            state.Status = StageStatus.Cancelled;
            await store.SaveAsync(project, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            state.Status = StageStatus.Failed;
            state.Error = ex.Message;
            await store.SaveAsync(project, CancellationToken.None);
            throw;
        }

        await store.SaveAsync(project, CancellationToken.None);
        return StageOutcome.Completed;
    }
}
