using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlcMonitor.Agent.Contracts.Client;

namespace PlcMonitor.App.Services;

/// <summary>
/// Un agente conocido por la app: cómo conectar con él y, si se gestiona por SSH, en qué equipo está.
/// Las contraseñas SSH no se guardan nunca.
/// </summary>
public sealed class AgentProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public string Url { get; set; } = AppSettings.DefaultAgentUrl;

    /// <summary>Token de acceso (en disco va protegido: DPAPI en Windows; en Linux, fichero solo legible por el usuario).</summary>
    [JsonIgnore]
    public string? Token { get; set; }

    public string? CertificateThumbprint { get; set; }

    /// <summary>Equipo SSH (vacío = no gestionado por SSH).</summary>
    public string? SshHost { get; set; }

    public int SshPort { get; set; } = 22;

    public string? SshUser { get; set; }

    /// <summary>Fichero de clave privada; vacío = ~/.ssh/id_* o contraseña.</summary>
    public string? SshKeyFile { get; set; }

    /// <summary>Huella SHA-256 del servidor SSH, fijada en la primera conexión.</summary>
    public string? SshHostKey { get; set; }

    /// <summary>Sistema detectado al instalar (p.ej. «Linux x86_64»).</summary>
    public string? Platform { get; set; }

    public int AgentPort { get; set; } = 5311;

    public DateTimeOffset? InstalledAt { get; set; }

    /// <summary>Token tal como se guarda en disco.</summary>
    public string? StoredToken { get; set; }

    [JsonIgnore]
    public AgentEndpoint Endpoint => new(Url, string.IsNullOrEmpty(Token) ? null : Token, string.IsNullOrEmpty(CertificateThumbprint) ? null : CertificateThumbprint);

    [JsonIgnore]
    public bool IsManaged => !string.IsNullOrWhiteSpace(SshHost);

    public AgentProfile Clone() => (AgentProfile)MemberwiseClone();
}

/// <summary>Lista de agentes guardada en <c>agents.json</c> junto a las preferencias.</summary>
public static class AgentProfileStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PLC-Monitor", "agents.json");

    public static List<AgentProfile> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];
            var profiles = JsonSerializer.Deserialize<List<AgentProfile>>(File.ReadAllText(path), Json) ?? [];
            foreach (var p in profiles)
                p.Token = Unprotect(p.StoredToken);
            return profiles;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void Save(string path, IEnumerable<AgentProfile> profiles)
    {
        var list = profiles.Select(p =>
        {
            var copy = p.Clone();
            copy.StoredToken = Protect(p.Token);
            return copy;
        }).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Primer arranque: un perfil «Local» con la URL que se usaba antes.</summary>
    public static List<AgentProfile> LoadOrCreate(string path, AppSettings settings)
    {
        var profiles = Load(path);
        if (profiles.Count == 0)
        {
            foreach (var url in new[] { settings.AgentUrl }.Concat(settings.RecentAgents).Distinct(StringComparer.OrdinalIgnoreCase))
                profiles.Add(new AgentProfile { Name = profiles.Count == 0 ? "Local" : url, Url = url });
        }
        return profiles;
    }

    private static string? Protect(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        return OperatingSystem.IsWindows() ? "dpapi:" + ProtectWindows(token) : "plain:" + token;
    }

    private static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return null;
        if (stored.StartsWith("plain:", StringComparison.Ordinal))
            return stored["plain:".Length..];
        if (stored.StartsWith("dpapi:", StringComparison.Ordinal) && OperatingSystem.IsWindows())
        {
            try
            {
                return UnprotectWindows(stored["dpapi:".Length..]);
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string ProtectWindows(string token) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));

    [SupportedOSPlatform("windows")]
    private static string UnprotectWindows(string data) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data), null, DataProtectionScope.CurrentUser));
}
