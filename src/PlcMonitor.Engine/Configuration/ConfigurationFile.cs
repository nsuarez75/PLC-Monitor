using System.Text.Json;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Engine.Configuration;

/// <summary>Lectura y escritura de la configuración en JSON (admite comentarios y comas finales).</summary>
public static class ConfigurationFile
{
    public static JsonSerializerOptions Options { get; } = new(DriverJson.Options)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AgentConfiguration Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<AgentConfiguration>(json, Options)
            ?? throw new JsonException("La configuración está vacía.");
    }

    public static async Task<AgentConfiguration> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        try
        {
            return Parse(json);
        }
        catch (JsonException ex)
        {
            throw new JsonException($"{path}: {ex.Message}", ex);
        }
    }

    public static string Serialize(AgentConfiguration configuration) => JsonSerializer.Serialize(configuration, Options);

    /// <summary>Guarda de forma atómica: escribe en un temporal y lo renombra sobre el original.</summary>
    public static async Task SaveAsync(string path, AgentConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        var temp = full + ".tmp";
        await File.WriteAllTextAsync(temp, Serialize(configuration), cancellationToken);
        File.Move(temp, full, overwrite: true);
    }
}
