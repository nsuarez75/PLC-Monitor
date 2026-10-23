using System.Text.Json;

namespace PlcMonitor.App.Services;

/// <summary>Preferencias locales de la app (última URL de agente...). Se guardan en la carpeta de datos del usuario.</summary>
public sealed class AppSettings
{
    public const string DefaultAgentUrl = "http://127.0.0.1:5310";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string AgentUrl { get; set; } = DefaultAgentUrl;

    /// <summary>Carpeta con los paquetes del agente elegida por el usuario (vacía = buscar la de la release).</summary>
    public string? AgentPackagesFolder { get; set; }

    /// <summary>Tema: "system", "light" o "dark".</summary>
    public string Theme { get; set; } = "system";

    /// <summary>Idioma de la interfaz: "es" o "en".</summary>
    public string Language { get; set; } = Localization.Loc.Spanish;

    /// <summary>Anchos de las columnas de la tabla de tags elegidos por el usuario (null = los de serie).</summary>
    public string? TagColumns { get; set; }

    /// <summary>Columnas de la tabla de tags que el usuario ha ocultado (índices separados por comas; null = ninguna).</summary>
    public string? TagHiddenColumns { get; set; }

    /// <summary>Orden en que se muestran las columnas de la tabla de tags (índices separados por comas; null = el de serie).</summary>
    public string? TagColumnOrder { get; set; }

    /// <summary>Grupos de visualización plegados en la tabla de tags (agente/dispositivo/grupo).</summary>
    public List<string> CollapsedFolders { get; init; } = [];

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
