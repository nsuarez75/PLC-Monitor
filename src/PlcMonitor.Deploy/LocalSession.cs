using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace PlcMonitor.Deploy;

/// <summary>
/// El propio equipo, sin SSH: ejecuta los mismos comandos y scripts con un proceso local. Para lo que necesita
/// administrador, <see cref="AgentDeployer"/> usa sudo o pkexec (ventana de polkit) en Linux y, en Windows,
/// <see cref="RunElevatedPowerShellAsync"/> pide permiso con UAC.
/// </summary>
public sealed class LocalSession : ISshSession
{
    public string HostKeyFingerprint => "";

    public bool IsLocal => true;

    public async Task<SshResult> RunAsync(string command, string? input = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var info = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (OperatingSystem.IsWindows())
        {
            // Igual que en una sesión OpenSSH de Windows: el intérprete es cmd.
            info.FileName = "cmd.exe";
            info.Arguments = $"/d /s /c \"{command}\"";
        }
        else
        {
            info.FileName = "/bin/sh";
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(command);
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new DeployException($"No se pudo ejecutar: {command}");
        }
        catch (Win32Exception ex)
        {
            return new SshResult(127, "", ex.Message);
        }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                if (input is not null)
                    await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // El comando terminó sin leer la entrada (p.ej. falló antes): su salida dice por qué.
            }
            await process.WaitForExitAsync(cancellationToken);
            return new SshResult(process.ExitCode, await output, await error);
        }
    }

    /// <summary>
    /// «Sube» el paquete: lo copia a la ruta temporal indicada (el script de instalación lo borra al terminar, así que
    /// nunca se le da el original). En Windows la ruta llega con el formato de SFTP, <c>/C:/...</c>.
    /// </summary>
    public async Task UploadAsync(Stream content, string remotePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = LocalPath(remotePath);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await content.CopyToAsync(file, cancellationToken);
    }

    /// <summary>
    /// Ejecuta el script en un PowerShell de administrador. Si la app ya lo es, directamente; si no, con «Ejecutar como
    /// administrador» (UAC). Un proceso elevado no puede devolver su salida por tuberías: el script va a un fichero
    /// temporal y su salida y su error vuelven en otros dos.
    /// </summary>
    public async Task<SshResult> RunElevatedPowerShellAsync(string script, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Solo en Windows.");
        if (IsAdministrator())
            return await RunAsync(AgentScripts.PowerShell(script), cancellationToken: cancellationToken);

        var dir = Directory.CreateTempSubdirectory("plcmon-").FullName;
        try
        {
            var scriptFile = Path.Combine(dir, "run.ps1");
            var outFile = Path.Combine(dir, "out.txt");
            var errFile = Path.Combine(dir, "err.txt");
            await File.WriteAllTextAsync(scriptFile, AgentScripts.ElevatedWrapper(script, outFile, errFile), new UTF8Encoding(true), cancellationToken);
            var info = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptFile })
                info.ArgumentList.Add(arg);
            Process? process;
            try
            {
                process = Process.Start(info);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new DeployException("Permiso de administrador denegado (UAC).");
            }
            using (process)
            {
                if (process is null)
                    throw new DeployException("No se pudo abrir PowerShell como administrador.");
                await process.WaitForExitAsync(cancellationToken);
                var output = File.Exists(outFile) ? await File.ReadAllTextAsync(outFile, cancellationToken) : "";
                var error = File.Exists(errFile) ? await File.ReadAllTextAsync(errFile, cancellationToken) : "";
                return new SshResult(process.ExitCode, output, error);
            }
        }
        finally
        {
            // El script lleva el certificado y su contraseña: no se deja en disco.
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public void Dispose()
    {
    }

    /// <summary>Ruta local a partir de la de SFTP (<c>/C:/Users/...</c> en Windows).</summary>
    internal static string LocalPath(string remotePath) =>
        OperatingSystem.IsWindows() && remotePath.Length > 3 && remotePath[0] == '/' && remotePath[2] == ':'
            ? remotePath[1..].Replace('/', '\\')
            : remotePath;

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
