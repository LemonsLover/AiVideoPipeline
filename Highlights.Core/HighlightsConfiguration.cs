using Microsoft.Extensions.Configuration;

namespace Highlights.Core;

/// <summary>
/// Configuration shared by the CLI and the desktop app: defaults from appsettings.json next to the executable,
/// personal settings and secrets from %APPDATA%\Highlights\appsettings.local.json, then HIGHLIGHTS_* env vars.
/// </summary>
public static class HighlightsConfiguration
{
    public static string UserDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Highlights");

    public static string UserSettingsPath { get; } = Path.Combine(UserDirectory, "appsettings.local.json");

    public static IConfigurationBuilder AddHighlightsConfiguration(this IConfigurationBuilder builder)
    {
        EnsureUserSettingsFile();
        return builder
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile(UserSettingsPath, optional: true)
            .AddEnvironmentVariables("HIGHLIGHTS_");
    }

    /// <summary>Creates a commented template so users know where the API key and machine paths go.</summary>
    private static void EnsureUserSettingsFile()
    {
        if (File.Exists(UserSettingsPath))
            return;
        Directory.CreateDirectory(UserDirectory);
        File.WriteAllText(UserSettingsPath, """
            {
              // Personal settings for the Highlights CLI and desktop app; override anything from appsettings.json.
              "Tools": {
                // Folder with ffmpeg.exe / ffprobe.exe / ffplay.exe; empty = search PATH.
                "FfmpegPath": ""
              },
              "OpenRouter": {
                // Your OpenRouter key (https://openrouter.ai/settings/keys), e.g. "sk-or-v1-...".
                "ApiKey": ""
              }
            }

            """);
    }
}
