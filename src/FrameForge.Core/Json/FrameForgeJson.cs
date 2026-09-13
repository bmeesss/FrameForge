using System.Text.Json;
using System.Text.Json.Serialization;
using FrameForge.Core.IO;

namespace FrameForge.Core.Json;

/// <summary>
/// Shared JSON serializer options for local application data.
/// </summary>
public static class FrameForgeJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options);

    public static async Task<T?> DeserializeFileAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task SerializeFileAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        var json = Serialize(value);
        await AtomicFile.WriteAllTextAsync(path, json, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
