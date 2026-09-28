using System.Security.Cryptography;
using System.Text;
using Highlights.Core.Configuration;
using Highlights.Core.Pipeline;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Llm;

/// <summary>
/// Prompt templates with {{placeholders}}. Built-in ones live in Prompts/ next to the executable (the defaults);
/// a file with the same name in %USERPROFILE%\.highlights\prompts replaces it (edited in the app's step settings).
/// </summary>
public sealed class PromptTemplates(IOptionsMonitor<AnalyzeOptions> options)
{
    /// <summary>The template in effect: the user's edited copy if there is one, else the built-in one.</summary>
    public string PathOf(string name)
    {
        var user = UserPathOf(name);
        if (File.Exists(user))
            return user;
        var path = DefaultPathOf(name);
        return File.Exists(path) ? path : throw new PipelineException($"Prompt template not found: {path}");
    }

    public string DefaultPathOf(string name) =>
        Path.Combine(options.CurrentValue.ResolveDirectory(options.CurrentValue.PromptsDirectory), name);

    public static string UserPathOf(string name) => Path.Combine(HighlightsConfiguration.UserPromptsDirectory, name);

    public bool IsOverridden(string name) => File.Exists(UserPathOf(name));

    public string Read(string name) => File.ReadAllText(PathOf(name));

    public string ReadDefault(string name) => File.Exists(DefaultPathOf(name)) ? File.ReadAllText(DefaultPathOf(name)) : "";

    /// <summary>Saves an edited template; text equal to the built-in one removes the override.</summary>
    public void Save(string name, string text)
    {
        if (Normalize(text) == Normalize(ReadDefault(name)))
        {
            Reset(name);
            return;
        }
        Directory.CreateDirectory(HighlightsConfiguration.UserPromptsDirectory);
        File.WriteAllText(UserPathOf(name), text, new UTF8Encoding(false));
    }

    public void Reset(string name) => File.Delete(UserPathOf(name));

    public async Task<string> RenderAsync(string name, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder(await File.ReadAllTextAsync(PathOf(name), cancellationToken));
        foreach (var (key, value) in values)
            sb.Replace("{{" + key + "}}", value);
        return sb.ToString();
    }

    /// <summary>Content hash, so editing a template makes dependent stages out of date.</summary>
    public string HashOf(string name) => FileHash(PathOf(name));

    public static string FileHash(string path) =>
        File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))[..12] : "missing";

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();
}
