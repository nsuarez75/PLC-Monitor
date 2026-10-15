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

/// <summary>Busca el paquete del agente para un RID: <c>plcmon-agent-*-&lt;rid&gt;.tar.gz</c> (Linux) o <c>.zip</c> (Windows).</summary>
public static class AgentPackages
{
    /// <summary>Carpetas por defecto: <c>agents/</c> junto a la app y en la carpeta superior (paquete de la release).</summary>
    public static IEnumerable<string> DefaultFolders()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "agents");
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "agents"));
    }

    public static string? Find(string rid, IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var extension = rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
        return folders
            .Where(Directory.Exists)
            .SelectMany(f => Directory.EnumerateFiles(f, $"plcmon-agent-*-{rid}{extension}"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
