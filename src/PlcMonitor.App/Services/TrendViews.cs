using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlcMonitor.App.Services;

/// <summary>Tag de una vista de tendencia (de cualquier dispositivo del agente).</summary>
public sealed record TrendTagRef(string Device, string Tag)
{
    public override string ToString() => $"{Device} / {Tag}";
}

/// <summary>
/// Vista de tendencia guardada: qué tags se ven juntos, la ventana de tiempo y el eje Y. Pertenece a un agente
/// (el id de su perfil o, si no hay perfil, su URL).
/// </summary>
public sealed class TrendView
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public string Agent { get; set; } = "";

    public List<TrendTagRef> Tags { get; set; } = [];

    public int WindowMinutes { get; set; } = 5;

    public bool AutoY { get; set; } = true;

    public double? YMin { get; set; }

    public double? YMax { get; set; }
}

/// <summary>Vistas de tendencia en <c>trends.json</c>, junto a las preferencias de la app.</summary>
public static class TrendViewStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string PathNextTo(string settingsPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "trends.json");

    public static List<TrendView> Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<TrendView>>(File.ReadAllText(path), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void Save(string path, IEnumerable<TrendView> views)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(views.ToList(), Json));
        File.Move(temp, path, overwrite: true);
    }
}
