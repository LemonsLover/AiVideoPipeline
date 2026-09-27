using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Highlights.Core.Projects;

public static class JsonDefaults
{
    /// <summary>Options for all project artifacts: camelCase, indented, enums as strings, readable Cyrillic.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Serializes to <paramref name="path"/> atomically (temp file + replace).</summary>
    public static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
            await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken);
        File.Move(tmp, path, overwrite: true);
    }

    public static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken)
               ?? throw new InvalidDataException($"{path} is empty.");
    }
}
