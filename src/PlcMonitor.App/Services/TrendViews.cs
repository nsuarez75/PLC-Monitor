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

    /// <summary>Tendencia de disparo: solo se ven los valores mientras se cumple; null = siempre.</summary>
    public TrendTrigger? Trigger { get; set; }
}

/// <summary>
/// Disparo de una vista de tendencia (como el del histórico): los tags se ven mientras <see cref="Tag"/> cumple la
/// condición con <see cref="Value"/>, y <see cref="HoldSeconds"/> más después.
/// </summary>
public sealed class TrendTrigger
{
    public string Device { get; set; } = "";

    public string Tag { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter<PlcMonitor.Agent.Contracts.TriggerCondition>))]
    public PlcMonitor.Agent.Contracts.TriggerCondition Condition { get; set; }

    /// <summary>true/false para booleanos; un número para los demás.</summary>
    public string Value { get; set; } = "true";

    /// <summary>Segundos que se siguen mostrando después de que deje de cumplirse.</summary>
    public double HoldSeconds { get; set; }

    /// <summary>Segundos que se muestran de antes de cumplirse (pre-disparo).</summary>
    public double BeforeSeconds { get; set; }

    public TrendTagRef Ref => new(Device, Tag);

    public override string ToString()
    {
        var condition = Condition switch
        {
            PlcMonitor.Agent.Contracts.TriggerCondition.NotEqual => "≠",
            PlcMonitor.Agent.Contracts.TriggerCondition.Greater => ">",
            PlcMonitor.Agent.Contracts.TriggerCondition.GreaterOrEqual => "≥",
            PlcMonitor.Agent.Contracts.TriggerCondition.Less => "<",
            PlcMonitor.Agent.Contracts.TriggerCondition.LessOrEqual => "≤",
            _ => "=",
        };
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var times = (BeforeSeconds > 0 ? $" −{BeforeSeconds.ToString("0.#", culture)} s" : "") + (HoldSeconds > 0 ? $" +{HoldSeconds.ToString("0.#", culture)} s" : "");
        return $"{Device} / {Tag} {condition} {Value}{(times.Length > 0 ? $" ({times.Trim()})" : "")}";
    }
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
