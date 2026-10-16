using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace PlcMonitor.Deploy;

/// <summary>Equipo de destino y credenciales SSH (no se guardan: se piden en cada operación).</summary>
/// <param name="Host">Nombre o IP del equipo.</param>
/// <param name="User">Usuario SSH (en Linux con sudo; en Windows, administrador).</param>
/// <param name="Password">Contraseña SSH (y de sudo en Linux). Null si se usa clave.</param>
/// <param name="KeyFile">Fichero de clave privada. Null para probar las de ~/.ssh si no hay contraseña.</param>
/// <param name="KeyPassphrase">Frase de la clave, si la tiene.</param>
/// <param name="ExpectedHostKey">Huella SHA-256 del servidor SSH ya conocida; null la primera vez (se acepta y se devuelve).</param>
public sealed record SshTarget(
    string Host,
    int Port,
    string User,
    string? Password = null,
    string? KeyFile = null,
    string? KeyPassphrase = null,
    string? ExpectedHostKey = null);

/// <summary>Resultado de un comando remoto.</summary>
public sealed record SshResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;

    /// <summary>Salida de error, o la estándar si no hay, para mensajes.</summary>
    public string Detail => (string.IsNullOrWhiteSpace(Error) ? Output : Error).Trim();
}

/// <summary>Sesión con el equipo donde se gestiona el agente: por SSH o el propio equipo (<see cref="LocalSession"/>).</summary>
public interface ISshSession : IDisposable
{
    /// <summary>Huella SHA-256 de la clave del servidor (formato de <c>ssh</c>, sin prefijo). Vacía en el propio equipo.</summary>
    string HostKeyFingerprint { get; }

    /// <summary>
    /// El propio equipo, sin SSH. En Linux, si sudo pide contraseña y no se ha dado, se pide permiso con pkexec (ventana
    /// de polkit); lo que no necesita administrador (comprobar si ya hay credenciales, leer el log) se intenta sin él.
    /// </summary>
    bool IsLocal => false;

    /// <summary>Ejecuta un script de PowerShell como administrador. Por SSH, la sesión de un administrador ya lo es.</summary>
    Task<SshResult> RunElevatedPowerShellAsync(string script, CancellationToken cancellationToken = default) =>
        RunAsync(AgentScripts.PowerShell(script), cancellationToken: cancellationToken);

    /// <summary>Ejecuta un comando. Si se indica <paramref name="input"/>, se escribe en su entrada estándar.</summary>
    Task<SshResult> RunAsync(string command, string? input = null, CancellationToken cancellationToken = default);

    /// <summary>Sube un fichero por SFTP. En Windows la ruta es del estilo <c>/C:/Windows/Temp/x.zip</c>.</summary>
    Task UploadAsync(Stream content, string remotePath, CancellationToken cancellationToken = default);
}

/// <summary>La huella del servidor SSH no coincide con la conocida.</summary>
public sealed class SshHostKeyMismatchException(string expected, string actual)
    : Exception($"La huella SSH del equipo ha cambiado (esperada {expected}, recibida {actual}). Si se ha reinstalado, bórrala del perfil.")
{
    public string Expected { get; } = expected;

    public string Actual { get; } = actual;
}

/// <summary>Sesión real con SSH.NET.</summary>
public sealed class SshNetSession : ISshSession
{
    private readonly SshClient _ssh;
    private readonly SftpClient _sftp;

    private SshNetSession(SshClient ssh, SftpClient sftp, string fingerprint)
    {
        _ssh = ssh;
        _sftp = sftp;
        HostKeyFingerprint = fingerprint;
    }

    public string HostKeyFingerprint { get; }

    public static async Task<SshNetSession> ConnectAsync(SshTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var info = new ConnectionInfo(target.Host, target.Port, target.User, AuthenticationMethods(target).ToArray())
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        string? fingerprint = null;
        void Check(object? sender, HostKeyEventArgs e)
        {
            fingerprint = e.FingerPrintSHA256;
            e.CanTrust = target.ExpectedHostKey is null || string.Equals(target.ExpectedHostKey, e.FingerPrintSHA256, StringComparison.Ordinal);
        }

        var ssh = new SshClient(info);
        var sftp = new SftpClient(info);
        ssh.HostKeyReceived += Check;
        sftp.HostKeyReceived += Check;
        try
        {
            await ssh.ConnectAsync(cancellationToken);
            await sftp.ConnectAsync(cancellationToken);
        }
        catch (SshConnectionException) when (target.ExpectedHostKey is not null && fingerprint is not null && fingerprint != target.ExpectedHostKey)
        {
            ssh.Dispose();
            sftp.Dispose();
            throw new SshHostKeyMismatchException(target.ExpectedHostKey, fingerprint);
        }
        catch
        {
            ssh.Dispose();
            sftp.Dispose();
            throw;
        }
        return new SshNetSession(ssh, sftp, fingerprint ?? "");
    }

    public async Task<SshResult> RunAsync(string command, string? input = null, CancellationToken cancellationToken = default)
    {
        using var cmd = _ssh.CreateCommand(command, Encoding.UTF8);
        cmd.CommandTimeout = TimeSpan.FromMinutes(10);
        var execution = cmd.ExecuteAsync(cancellationToken);
        if (input is not null)
        {
            await using var stdin = cmd.CreateInputStream();
            var bytes = Encoding.UTF8.GetBytes(input);
            await stdin.WriteAsync(bytes, cancellationToken);
        }
        await execution;
        return new SshResult(cmd.ExitStatus ?? -1, cmd.Result, cmd.Error);
    }

    public Task UploadAsync(Stream content, string remotePath, CancellationToken cancellationToken = default) =>
        _sftp.UploadFileAsync(content, remotePath, cancellationToken);

    public void Dispose()
    {
        _ssh.Dispose();
        _sftp.Dispose();
    }

    private static IEnumerable<AuthenticationMethod> AuthenticationMethods(SshTarget target)
    {
        var keys = new List<IPrivateKeySource>();
        foreach (var file in KeyFiles(target))
        {
            try
            {
                keys.Add(new PrivateKeyFile(file, target.KeyPassphrase));
            }
            catch (Exception ex) when (ex is SshException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Clave cifrada sin frase, formato no soportado...: se prueba con lo demás.
            }
        }
        if (keys.Count > 0)
            yield return new PrivateKeyAuthenticationMethod(target.User, keys.ToArray());
        if (target.Password is { Length: > 0 } password)
        {
            yield return new PasswordAuthenticationMethod(target.User, password);
            var interactive = new KeyboardInteractiveAuthenticationMethod(target.User);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                    prompt.Response = password;
            };
            yield return interactive;
        }
    }

    private static IEnumerable<string> KeyFiles(SshTarget target)
    {
        if (target.KeyFile is { Length: > 0 } explicitKey)
        {
            yield return explicitKey;
            yield break;
        }
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        foreach (var name in new[] { "id_ed25519", "id_ecdsa", "id_rsa" })
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path))
                yield return path;
        }
    }
}
