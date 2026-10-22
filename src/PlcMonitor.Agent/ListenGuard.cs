using System.Net;

namespace PlcMonitor.Agent;

/// <summary>
/// Sin token, el agente solo puede escuchar en este equipo (127.0.0.1, ::1 o localhost): abrir la API a la red sin
/// protección dejaría escribir en los PLC a cualquiera que llegue al puerto.
/// </summary>
public static class ListenGuard
{
    /// <summary>Por qué no debe arrancar el agente, o null si la configuración es segura.</summary>
    public static string? Problem(IConfiguration configuration, AgentSecurityOptions security)
    {
        if (security.RequiresToken)
            return null;
        var exposed = ListenUrls(configuration).Where(url => !IsLoopback(url)).ToList();
        if (exposed.Count == 0)
            return null;
        return $"El agente escucha en {string.Join(", ", exposed)} sin token de acceso. Sin token solo puede escuchar en este equipo "
            + "(http://127.0.0.1:5310): instálalo desde la app (genera token y certificado) o indica Agent:Security:TokenSha256 en agent.settings.json.";
    }

    /// <summary>Direcciones de escucha configuradas: los endpoints de Kestrel y, si no hay ninguno, <c>urls</c> (--urls, ASPNETCORE_URLS).</summary>
    public static IReadOnlyList<string> ListenUrls(IConfiguration configuration)
    {
        var endpoints = configuration.GetSection("Kestrel:Endpoints").GetChildren()
            .Select(e => e["Url"])
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!)
            .ToList();
        if (endpoints.Count > 0)
            return endpoints;
        return (configuration["urls"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>La URL solo acepta conexiones de este equipo. «*», «+», 0.0.0.0 y cualquier nombre que no sea localhost, no.</summary>
    public static bool IsLoopback(string url)
    {
        var start = url.IndexOf("://", StringComparison.Ordinal);
        var rest = start < 0 ? url : url[(start + 3)..];
        var end = rest.IndexOf('/');
        var authority = end < 0 ? rest : rest[..end];
        string host;
        if (authority.StartsWith('['))
            host = authority[1..Math.Max(1, authority.IndexOf(']'))];
        else
        {
            var colon = authority.LastIndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
        }
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }
}
