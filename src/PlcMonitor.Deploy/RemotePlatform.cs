using System.Reflection;

namespace PlcMonitor.Deploy;

public enum RemoteOs
{
    Linux,
    Windows,
}

/// <summary>Sistema del equipo remoto y el identificador de runtime (.NET RID) del paquete del agente que necesita.</summary>
public sealed record RemotePlatform(RemoteOs Os, string Rid, string Description)
{
    /// <summary>Interpreta la salida de <c>uname -sm</c> (Linux).</summary>
    public static RemotePlatform? FromUname(string output)
    {
        var parts = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("Linux", StringComparison.OrdinalIgnoreCase))
            return null;
        var rid = parts[1] switch
        {
            "x86_64" or "amd64" => "linux-x64",
            "aarch64" or "arm64" => "linux-arm64",
            _ => null,
        };
        return rid is null ? null : new RemotePlatform(RemoteOs.Linux, rid, $"Linux {parts[1]}");
    }

    /// <summary>Interpreta <c>$env:PROCESSOR_ARCHITECTURE</c> (Windows).</summary>
    public static RemotePlatform? FromWindowsArchitecture(string output)
    {
        var arch = output.Trim().ToUpperInvariant();
        var rid = arch switch
        {
            "AMD64" => "win-x64",
            "ARM64" => "win-arm64",
            _ => null,
        };
        return rid is null ? null : new RemotePlatform(RemoteOs.Windows, rid, $"Windows {arch}");
    }
}

/// <summary>
/// Busca el ejecutable (único) del agente para un RID: <c>plcmon-agent-&lt;versión&gt;-&lt;rid&gt;</c>, con <c>.exe</c> en
/// Windows. Primero en las carpetas indicadas; si no está, el que la app lleva dentro (la app de la release incluye los
/// agentes de todas las plataformas como recursos <c>agents/plcmon-agent-&lt;rid&gt;</c>).
/// </summary>
public static class AgentPackages
{
    /// <summary>Prefijo de los recursos con los agentes incluidos en la app.</summary>
    public const string ResourcePrefix = "agents/plcmon-agent-";

    /// <summary>Carpetas por defecto: <c>agents/</c> junto a la app y en la carpeta superior.</summary>
    public static IEnumerable<string> DefaultFolders() => DefaultFolders(AppContext.BaseDirectory);

    /// <summary>
    /// <c>agents/</c> junto a la app y en la carpeta superior y, si la app corre desde el código, la release más
    /// reciente en <c>artifacts/release/&lt;versión&gt;/agents</c> del repositorio.
    /// </summary>
    public static IEnumerable<string> DefaultFolders(string baseDir)
    {
        yield return Path.Combine(baseDir, "agents");
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "agents"));
        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            var releases = Path.Combine(dir.FullName, "artifacts", "release");
            if (!Directory.Exists(releases))
                continue;
            foreach (var release in Directory.EnumerateDirectories(releases).OrderByDescending(d => Directory.GetLastWriteTimeUtc(d)))
                yield return Path.Combine(release, "agents");
            yield break;
        }
    }

    /// <summary>Nombre del ejecutable del agente para un RID (sin versión), p.ej. <c>plcmon-agent-*-win-x64.exe</c>.</summary>
    public static string FileName(string rid) => $"plcmon-agent-*-{rid}{Extension(rid)}";

    public static string? Find(string rid, IEnumerable<string> folders) => Find(rid, folders, Assembly.GetEntryAssembly());

    /// <summary>Busca en las carpetas y, si no está, en los recursos de <paramref name="bundle"/> (se extrae a un temporal).</summary>
    public static string? Find(string rid, IEnumerable<string> folders, Assembly? bundle)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var file = folders
            .Where(Directory.Exists)
            .SelectMany(f => Directory.EnumerateFiles(f, FileName(rid)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return file ?? ExtractEmbedded(rid, bundle);
    }

    /// <summary>RIDs de los agentes que lleva dentro la app.</summary>
    public static IReadOnlyList<string> Embedded(Assembly? bundle) =>
        bundle?.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..])
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];

    /// <summary>
    /// Copia el agente incluido a <c>%TEMP%/plcmon-agents/&lt;versión&gt;/</c> (se reutiliza entre instalaciones) y
    /// devuelve su ruta, o null si la app no lo lleva.
    /// </summary>
    private static string? ExtractEmbedded(string rid, Assembly? bundle)
    {
        using var resource = bundle?.GetManifestResourceStream(ResourcePrefix + rid);
        if (resource is null)
            return null;
        var version = bundle!.GetName().Version?.ToString(3) ?? "0.0.0";
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "plcmon-agents", version)).FullName;
        var path = Path.Combine(dir, $"plcmon-agent-{version}-{rid}{Extension(rid)}");
        if (File.Exists(path) && new FileInfo(path).Length == resource.Length)
            return path;
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        using (var file = File.Create(temp))
            resource.CopyTo(file);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    private static string Extension(string rid) => rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : "";
}
