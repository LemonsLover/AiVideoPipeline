namespace Highlights.Core.Projects;

/// <summary>File names inside a project directory and the video → project directory convention.</summary>
public static class ProjectLayout
{
    public const string DirectorySuffix = ".highlights";
    public const string ProjectFile = "project.json";
    public const string AudioFile = "audio.wav";
    public const string TranscriptFile = "transcript.json";
    public const string SignalsFile = "signals.json";
    public const string MomentsFile = "moments.json";
    public const string PlanFile = "plan.json";
    public const string ClipsFile = "clips.json";
    public const string ReviewFile = "review.json";
    public const string EditFile = "edit.json";
    public const string VideoOutputFile = "highlights.mp4";
    public const string YouTubeFile = "youtube.txt";

    /// <summary><c>D:\Videos\game.mkv</c> → <c>D:\Videos\game.highlights</c>.</summary>
    public static string ProjectDirectoryFor(string videoPath)
    {
        var full = Path.GetFullPath(videoPath);
        return Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + DirectorySuffix);
    }
}
