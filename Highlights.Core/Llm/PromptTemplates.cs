using System.Security.Cryptography;
using System.Text;
using Highlights.Core.Configuration;
using Highlights.Core.Pipeline;
using Microsoft.Extensions.Options;

namespace Highlights.Core.Llm;

/// <summary>Prompt templates (Prompts/*.md) with {{placeholders}}, editable without recompiling.</summary>
public sealed class PromptTemplates(IOptions<AnalyzeOptions> options)
{
    public string PathOf(string name)
    {
        var path = Path.Combine(options.Value.ResolveDirectory(options.Value.PromptsDirectory), name);
        return File.Exists(path) ? path : throw new PipelineException($"Prompt template not found: {path}");
    }

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
}
