using Microsoft.Extensions.Configuration;

namespace Highlights.Core;

/// <summary>
/// Configuration shared by the CLI and the desktop app: defaults from appsettings.json next to the executable,
/// personal settings and secrets from %USERPROFILE%\.highlights\settings.json, then HIGHLIGHTS_* env vars.
/// </summary>
/// <remarks>
/// The user folder deliberately lives outside AppData: a process started from a packaged (MSIX) app gets AppData
/// redirected into the package's private cache, so the CLI run from such a host and the desktop app started from
/// Explorer or Visual Studio would silently see different settings, models and tools.
/// </remarks>
public static class HighlightsConfiguration
{
    /// <summary>Settings, downloaded models and tools: %USERPROFILE%\.highlights.</summary>
    public static string UserDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".highlights");

    public static string UserSettingsPath { get; } = Path.Combine(UserDirectory, "settings.json");

    /// <summary>Earlier location; copied over on first start.</summary>
    private static string LegacySettingsPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Highlights", "appsettings.local.json");

    public static IConfigurationBuilder AddHighlightsConfiguration(this IConfigurationBuilder builder)
    {
        EnsureUserSettingsFile();
        return builder
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            // Reloaded on change: edits made while the desktop app is open (Settings button) apply right away.
            .AddJsonFile(UserSettingsPath, optional: true, reloadOnChange: true)
            .AddEnvironmentVariables("HIGHLIGHTS_");
    }

    /// <summary>Migrates the old settings file, or creates a commented template so users know where the key goes.</summary>
    private static void EnsureUserSettingsFile()
    {
        if (File.Exists(UserSettingsPath))
            return;
        Directory.CreateDirectory(UserDirectory);
        if (File.Exists(LegacySettingsPath))
        {
            File.Copy(LegacySettingsPath, UserSettingsPath);
            return;
        }
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
