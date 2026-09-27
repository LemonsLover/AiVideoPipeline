namespace Highlights.Core.Pipeline;

/// <summary>
/// Exclusive lock on a project directory while stages run, across processes (CLI and desktop app): two runs on the
/// same project would overwrite each other's project.json and artifacts. The lock file is removed on dispose,
/// and the OS releases it if the process dies.
/// </summary>
public sealed class ProjectLock : IDisposable
{
    private const string FileName = "project.lock";
    private readonly FileStream _stream;

    private ProjectLock(FileStream stream) => _stream = stream;

    public static ProjectLock Acquire(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, FileName);
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            using (var writer = new StreamWriter(stream, leaveOpen: true))
                writer.Write($"{Environment.ProcessId} {Environment.ProcessPath} {DateTimeOffset.Now:O}");
            return new ProjectLock(stream);
        }
        catch (IOException ex)
        {
            throw new PipelineException(
                "This project is already being processed by another window or command. Wait for it to finish (or close it) and try again.", ex);
        }
    }

    public void Dispose() => _stream.Dispose();
}
