using System.Security.Cryptography;
using PlcMonitor.Agent.Contracts.Client;

namespace PlcMonitor.Deploy;

/// <summary>Opciones de instalación o actualización.</summary>
/// <param name="PackageFolders">Carpetas donde buscar el paquete del agente para el sistema del equipo.</param>
/// <param name="Port">Puerto TLS del agente.</param>
/// <param name="RegenerateCredentials">Genera token y certificado nuevos aunque el equipo ya los tenga.</param>
/// <param name="SudoPassword">Contraseña de sudo (Linux) si difiere de la SSH o se entra con clave.</param>
public sealed record InstallOptions(IReadOnlyList<string> PackageFolders, int Port = 5311, bool RegenerateCredentials = false, string? SudoPassword = null);

/// <summary>Resultado de una instalación.</summary>
/// <param name="Url">URL del agente (https).</param>
/// <param name="Token">Token nuevo, o null si se conservaron las credenciales que ya tenía el equipo.</param>
/// <param name="CertificateThumbprint">Huella del certificado nuevo, o null si se conservó.</param>
public sealed record InstallResult(RemotePlatform Platform, string Url, string? Token, string? CertificateThumbprint, string HostKeyFingerprint);

/// <summary>Estado del agente en el equipo.</summary>
public sealed record AgentStatus(RemotePlatform Platform, bool Installed, string ServiceState, string? Since)
{
    public bool Running => ServiceState == "active";
}

/// <summary>El paso remoto falló: incluye la salida del equipo.</summary>
public sealed class DeployException(string message) : Exception(message);

/// <summary>Instala, actualiza, consulta y desinstala el agente en un equipo por SSH.</summary>
public sealed class AgentDeployer(ISshSession session, IProgress<string>? progress = null)
{
    private RemotePlatform? _platform;

    /// <summary>Detecta el sistema del equipo (Linux por <c>uname</c>; si no, Windows).</summary>
    public async Task<RemotePlatform> DetectPlatformAsync(CancellationToken cancellationToken = default)
    {
        if (_platform is not null)
            return _platform;
        Report("Detectando el sistema del equipo…");
        var uname = await session.RunAsync("uname -sm", cancellationToken: cancellationToken);
        var platform = uname.Success ? RemotePlatform.FromUname(uname.Output) : null;
        if (platform is null)
        {
            var arch = await session.RunAsync(AgentScripts.PowerShell("$env:PROCESSOR_ARCHITECTURE"), cancellationToken: cancellationToken);
            platform = arch.Success ? RemotePlatform.FromWindowsArchitecture(arch.Output) : null;
        }
        _platform = platform ?? throw new DeployException($"Sistema no soportado o no reconocido: {uname.Output.Trim()} {uname.Error.Trim()}".Trim());
        Report($"Equipo: {_platform.Description} (paquete {_platform.Rid})");
        return _platform;
    }

    public async Task<InstallResult> InstallAsync(string host, InstallOptions options, string? password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var platform = await DetectPlatformAsync(cancellationToken);
        var package = AgentPackages.Find(platform.Rid, options.PackageFolders)
            ?? throw new DeployException($"No se encuentra el ejecutable del agente para {platform.Rid} ({AgentPackages.FileName(platform.Rid)}) en: {string.Join(", ", options.PackageFolders)}");

        var hasSecurity = (await RunPlatformAsync(platform, platform.Os == RemoteOs.Linux ? AgentScripts.LinuxHasSecurity : AgentScripts.WindowsHasSecurity, password, options.SudoPassword, elevated: !session.IsLocal, cancellationToken)).Output.Trim() == "yes";
        string? token = null;
        string? thumbprint = null;
        AgentSecurityFiles? security = null;
        if (!hasSecurity || options.RegenerateCredentials)
        {
            Report("Generando token de acceso y certificado TLS…");
            var hostName = (await session.RunAsync("hostname", cancellationToken: cancellationToken)).Output.Trim();
            token = AgentSecurity.NewToken();
            var pfxPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var (pfx, print) = AgentSecurity.CreateCertificate("plcmon-agent", [host, hostName, "localhost"], pfxPassword);
            thumbprint = print;
            security = new AgentSecurityFiles(AgentScripts.SettingsJson(AgentSecurity.HashToken(token), options.Port, pfxPassword), pfx);
        }
        else
        {
            Report("El equipo ya tiene credenciales: se conservan (actualización).");
        }

        var remoteArchive = await UploadAsync(platform, package, password, cancellationToken);
        Report("Instalando el servicio…");
        var script = platform.Os == RemoteOs.Linux
            ? AgentScripts.LinuxInstall(remoteArchive, security, options.Port)
            : AgentScripts.WindowsInstall(remoteArchive.TrimStart('/').Replace('/', '\\'), security, options.Port);
        var result = await RunPlatformAsync(platform, script, password, options.SudoPassword, elevated: true, cancellationToken);
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l != "active"))
            Report(line);
        if (!result.Success || !result.Output.Contains("active", StringComparison.Ordinal))
            throw new DeployException($"La instalación falló: {result.Detail}");
        var url = $"https://{host}:{options.Port}";
        Report($"Agente en marcha: {url}");
        return new InstallResult(platform, url, token, thumbprint, session.HostKeyFingerprint);
    }

    public async Task UninstallAsync(bool keepConfiguration, string? password, string? sudoPassword = null, int port = 5311, CancellationToken cancellationToken = default)
    {
        var platform = await DetectPlatformAsync(cancellationToken);
        Report("Desinstalando el servicio…");
        var script = platform.Os == RemoteOs.Linux ? AgentScripts.LinuxUninstall(keepConfiguration, port) : AgentScripts.WindowsUninstall(keepConfiguration);
        var result = await RunPlatformAsync(platform, script, password, sudoPassword, elevated: true, cancellationToken);
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l != "desinstalado"))
            Report(line);
        if (!result.Success || !result.Output.Contains("desinstalado", StringComparison.Ordinal))
            throw new DeployException($"La desinstalación falló: {result.Detail}");
        Report("Agente desinstalado");
    }

    public async Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var platform = await DetectPlatformAsync(cancellationToken);
        var result = platform.Os == RemoteOs.Linux
            ? await session.RunAsync(AgentScripts.LinuxStatus, cancellationToken: cancellationToken)
            : await session.RunAsync(AgentScripts.PowerShell(AgentScripts.WindowsStatus), cancellationToken: cancellationToken);
        var values = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
        return new AgentStatus(
            platform,
            values.GetValueOrDefault("installed") == "yes",
            values.GetValueOrDefault("active") is { Length: > 0 } state ? state : "inactive",
            values.GetValueOrDefault("since") is { Length: > 0 } since ? since : null);
    }

    /// <summary>Últimas líneas del log del servicio (Linux: journald; Windows: los ficheros de logs\ del agente).</summary>
    public async Task<string> GetLogsAsync(string? password, string? sudoPassword = null, int lines = 200, CancellationToken cancellationToken = default)
    {
        var platform = await DetectPlatformAsync(cancellationToken);
        if (platform.Os == RemoteOs.Windows)
        {
            // Program Files se puede leer sin ser administrador: no hace falta UAC.
            var log = await RunPlatformAsync(platform, AgentScripts.WindowsLogs(lines), password, sudoPassword, elevated: false, cancellationToken);
            if (!log.Success)
                throw new DeployException($"No se pudo leer el log: {log.Detail}");
            return log.Output.Trim() == "nolog"
                ? "Este agente aún no ha escrito ningún log en fichero (las versiones anteriores a la 1.3.0 solo escriben avisos y errores en el Visor de eventos, origen «plcmon-agent»). Actualízalo con «Instalar / actualizar»."
                : log.Output;
        }
        if (session.IsLocal)
        {
            // Si el usuario puede leer el diario del sistema (grupos wheel, adm o systemd-journal) no hace falta pedir permiso.
            var own = await RunPlatformAsync(platform, AgentScripts.LinuxLogs(lines), password, sudoPassword, elevated: false, cancellationToken);
            if (own.Success && own.Output.Trim() is { Length: > 0 } text && !text.Contains("-- No entries --", StringComparison.Ordinal))
                return own.Output;
        }
        var result = await RunPlatformAsync(platform, AgentScripts.LinuxLogs(lines), password, sudoPassword, elevated: true, cancellationToken);
        return result.Success ? result.Output : throw new DeployException($"No se pudo leer el log: {result.Detail}");
    }

    private async Task<string> UploadAsync(RemotePlatform platform, string package, string? password, CancellationToken cancellationToken)
    {
        string remote;
        if (platform.Os == RemoteOs.Linux)
        {
            remote = $"/tmp/plcmon-agent-{Guid.NewGuid():N}";
        }
        else
        {
            var temp = (await session.RunAsync(AgentScripts.PowerShell("[IO.Path]::GetTempPath()"), cancellationToken: cancellationToken)).Output.Trim();
            // Ruta de Windows construida a mano (Path.Combine no entiende '\\' fuera de Windows); SFTP usa /C:/...
            remote = "/" + (temp.TrimEnd('\\', '/') + $"/plcmon-agent-{Guid.NewGuid():N}.exe").Replace('\\', '/');
        }
        Report($"Subiendo {Path.GetFileName(package)} ({new FileInfo(package).Length / (1024 * 1024)} MB)…");
        await using var stream = File.OpenRead(package);
        await session.UploadAsync(stream, remote, cancellationToken);
        return remote;
    }

    /// <summary>
    /// Ejecuta un script con permisos de administrador. Linux: root directamente, <c>sudo -n</c> si no pide contraseña,
    /// <c>sudo -S</c> con la contraseña por la entrada estándar o, en el propio equipo y sin contraseña, pkexec (ventana
    /// de polkit). Windows: PowerShell elevado (por SSH la sesión de un administrador ya lo está; en el propio equipo, UAC).
    /// </summary>
    private async Task<SshResult> RunPlatformAsync(RemotePlatform platform, string script, string? password, string? sudoPassword, bool elevated, CancellationToken cancellationToken)
    {
        if (platform.Os == RemoteOs.Windows)
        {
            return elevated
                ? await session.RunElevatedPowerShellAsync(script, cancellationToken)
                : await session.RunAsync(AgentScripts.PowerShell(script), cancellationToken: cancellationToken);
        }
        if (!elevated)
            return await session.RunAsync("bash -s", script, cancellationToken);

        var uid = (await session.RunAsync("id -u", cancellationToken: cancellationToken)).Output.Trim();
        if (uid == "0")
            return await session.RunAsync("bash -s", script, cancellationToken);
        if ((await session.RunAsync("sudo -n true", cancellationToken: cancellationToken)).Success)
            return await session.RunAsync("sudo -n bash -s", script, cancellationToken);
        var sudo = sudoPassword ?? password;
        if (sudo is null && session.IsLocal)
            return await RunWithPkexecAsync(script, cancellationToken);
        if (sudo is null)
            throw new DeployException("sudo pide contraseña: indícala (o configura sudo sin contraseña para este usuario).");
        var result = await session.RunAsync("sudo -S -p '' bash -s", sudo + "\n" + script, cancellationToken);
        if (!result.Success && result.Error.Contains("incorrect password", StringComparison.OrdinalIgnoreCase))
            throw new DeployException("Contraseña de sudo incorrecta.");
        return result;
    }

    /// <summary>Pide permiso de administrador con la ventana de polkit del escritorio.</summary>
    private async Task<SshResult> RunWithPkexecAsync(string script, CancellationToken cancellationToken)
    {
        if (!(await session.RunAsync("command -v pkexec", cancellationToken: cancellationToken)).Success)
            throw new DeployException("sudo pide contraseña y no hay pkexec para pedirla con una ventana: indica la contraseña de sudo.");
        Report("Pidiendo permiso de administrador (ventana del sistema)…");
        var result = await session.RunAsync("pkexec bash -s", script, cancellationToken);
        return result.ExitCode switch
        {
            126 => throw new DeployException("Permiso de administrador denegado o cancelado."),
            127 when result.Error.Contains("authentication agent", StringComparison.OrdinalIgnoreCase) || result.Error.Contains("Not authorized", StringComparison.OrdinalIgnoreCase)
                => throw new DeployException("No hay agente de polkit para pedir la contraseña: indica la contraseña de sudo."),
            _ => result,
        };
    }

    private void Report(string message) => progress?.Report(message);
}
