using System.Text.Json;
using Highlights.Core.Media;
using Highlights.Core.Pipeline;

namespace Highlights.Core.Projects;

public interface IProjectStore
{
    /// <summary>
    /// Resolves a user-supplied path to a project directory: a project directory itself, a directory
    /// containing project.json, or a video file (its sibling <c>.highlights</c> directory).
    /// </summary>
    string ResolveProjectDirectory(string path);

    Task<HighlightsProject> CreateAsync(string videoPath, CancellationToken cancellationToken = default);
    Task<HighlightsProject> LoadAsync(string path, CancellationToken cancellationToken = default);
    Task SaveAsync(HighlightsProject project, CancellationToken cancellationToken = default);
}

public sealed class ProjectStore(IMediaProbe probe) : IProjectStore
{
    public string ResolveProjectDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full))
            return Path.GetFileName(full).Equals(ProjectLayout.ProjectFile, StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(full)!
                : ProjectLayout.ProjectDirectoryFor(full);
        return full;
    }

    public async Task<HighlightsProject> CreateAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        var video = Path.GetFullPath(videoPath);
        if (!File.Exists(video))
            throw new PipelineException($"Video not found: {video}");

        var dir = ProjectLayout.ProjectDirectoryFor(video);
        if (File.Exists(Path.Combine(dir, ProjectLayout.ProjectFile)))
            throw new PipelineException($"Project already exists: {dir}");

        var media = await probe.ProbeAsync(video, cancellationToken);
        if (media.AudioTracks.Count == 0)
            throw new PipelineException("The video has no audio tracks.");

        System.IO.Directory.CreateDirectory(dir);
        var project = new HighlightsProject { VideoPath = video, Media = media, Directory = dir };
        if (media.AudioTracks.Count == 1)
            project.Settings.AudioTrack = 0;

        await SaveAsync(project, cancellationToken);
        return project;
    }

    public async Task<HighlightsProject> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var dir = ResolveProjectDirectory(path);
        var file = Path.Combine(dir, ProjectLayout.ProjectFile);
        if (!File.Exists(file))
            throw new PipelineException($"No project found at {dir}. Create one with: highlights new <video>");

        HighlightsProject project;
        try
        {
            project = await JsonDefaults.ReadAsync<HighlightsProject>(file, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new PipelineException($"{file} is corrupted: {ex.Message}", ex);
        }

        if (project.SchemaVersion > HighlightsProject.CurrentSchemaVersion)
            throw new PipelineException($"{file} was written by a newer version (schema {project.SchemaVersion}).");

        project.Directory = dir;
        return project;
    }

    public Task SaveAsync(HighlightsProject project, CancellationToken cancellationToken = default) =>
        JsonDefaults.WriteAtomicAsync(project.PathOf(ProjectLayout.ProjectFile), project, cancellationToken);
}
