using System.Text;

namespace PlcMonitor.Deploy.Tests;

/// <summary>Sesión SSH simulada: respuestas por prefijo de comando y registro de lo ejecutado y subido.</summary>
internal sealed class FakeSsh : ISshSession
{
    private readonly List<(Func<string, bool> Match, Func<string, string?, SshResult> Reply)> _rules = [];

    public List<(string Command, string? Input)> Commands { get; } = [];

    public Dictionary<string, byte[]> Uploads { get; } = [];

    public string HostKeyFingerprint => IsLocal ? "" : "huella-ssh";

    /// <summary>Simula el propio equipo (<see cref="LocalSession"/>).</summary>
    public bool IsLocal { get; init; }

    /// <summary>Scripts de PowerShell pedidos como administrador (en el propio equipo, los que abren UAC).</summary>
    public List<string> ElevatedScripts { get; } = [];

    public FakeSsh On(string prefix, SshResult result) => On(c => c.StartsWith(prefix, StringComparison.Ordinal), (_, _) => result);

    public FakeSsh On(Func<string, bool> match, Func<string, string?, SshResult> reply)
    {
        _rules.Insert(0, (match, reply));
        return this;
    }

    public static SshResult Ok(string output = "") => new(0, output, "");

    public static SshResult Fail(string error, int code = 1) => new(code, "", error);

    public Task<SshResult> RunAsync(string command, string? input = null, CancellationToken cancellationToken = default)
    {
        Commands.Add((command, input));
        foreach (var (match, reply) in _rules)
        {
            if (match(command))
                return Task.FromResult(reply(command, input));
        }
        return Task.FromResult(Fail($"comando no previsto: {command}", 127));
    }

    public Task<SshResult> RunElevatedPowerShellAsync(string script, CancellationToken cancellationToken = default)
    {
        ElevatedScripts.Add(script);
        return RunAsync(AgentScripts.PowerShell(script), cancellationToken: cancellationToken);
    }

    public async Task UploadAsync(Stream content, string remotePath, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        Uploads[remotePath] = buffer.ToArray();
    }

    /// <summary>Scripts de PowerShell enviados con -EncodedCommand, ya decodificados.</summary>
    public IEnumerable<string> PowerShellScripts => Commands
        .Where(c => c.Command.Contains("-EncodedCommand ", StringComparison.Ordinal))
        .Select(c => Encoding.Unicode.GetString(Convert.FromBase64String(c.Command[(c.Command.LastIndexOf(' ') + 1)..])));

    public void Dispose()
    {
    }
}
