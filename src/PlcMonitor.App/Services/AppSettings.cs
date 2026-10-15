using System.Text.Json;

namespace PlcMonitor.App.Services;

/// <summary>Preferencias locales de la app (última URL de agente...). Se guardan en la carpeta de datos del usuario.</summary>
public sealed class AppSettings
{
    public const string DefaultAgentUrl = "http://127.0.0.1:5310";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string AgentUrl { get; set; } = DefaultAgentUrl;

    /// <summary>URLs usadas recientemente (la primera es la última).</summary>
    public List<string> RecentAgents { get; init; } = [];

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PLC-Monitor", "app.json");

    public static AppSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unas preferencias dañadas no deben impedir arrancar.
            return new();
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No poder guardar las preferencias no es grave.
        }
    }

    /// <summary>Recuerda una URL como la última usada (máximo 10).</summary>
    public void Remember(string url)
    {
        AgentUrl = url;
        RecentAgents.RemoveAll(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase));
        RecentAgents.Insert(0, url);
        if (RecentAgents.Count > 10)
            RecentAgents.RemoveRange(10, RecentAgents.Count - 10);
    }
}
