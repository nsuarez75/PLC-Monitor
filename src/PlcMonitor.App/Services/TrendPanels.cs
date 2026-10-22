using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlcMonitor.App.Services;

/// <summary>
/// Panel guardado: varias vistas de tendencia en una rejilla de <see cref="Columns"/> columnas (las filas salen solas).
/// Pertenece a un agente, como las vistas.
/// </summary>
public sealed class TrendPanel
{
    public const int MaxColumns = 4;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public string Agent { get; set; } = "";

    /// <summary>Vistas del panel, en orden (por su <see cref="TrendView.Id"/>).</summary>
    public List<string> Views { get; set; } = [];

    public int Columns { get; set; } = 2;

    /// <summary>El zoom de una gráfica de tiempo se aplica a las demás del panel.</summary>
    public bool SyncZoom { get; set; } = true;
}

/// <summary>Paneles en <c>panels.json</c>, junto a <c>trends.json</c>.</summary>
public static class TrendPanelStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string PathNextTo(string trendsPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(trendsPath))!, "panels.json");

    public static List<TrendPanel> Load(string path)
    {
        try
        {
            var panels = File.Exists(path) ? JsonSerializer.Deserialize<List<TrendPanel>>(File.ReadAllText(path), Json) ?? [] : [];
            foreach (var panel in panels)
                panel.Columns = Math.Clamp(panel.Columns, 1, TrendPanel.MaxColumns);
            return panels;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void Save(string path, IEnumerable<TrendPanel> panels)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(panels.ToList(), Json));
        File.Move(temp, path, overwrite: true);
    }
}
