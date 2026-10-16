using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.Agent.Contracts.Client;

namespace PlcMonitor.Deploy.Tests;

/// <summary>
/// Instalación real por SSH en un equipo Linux con systemd.
/// Variables: PLCMON_IT_SSH_HOST, PLCMON_IT_SSH_PORT, PLCMON_IT_SSH_USER, PLCMON_IT_SSH_PASSWORD y
/// PLCMON_IT_AGENT_PACKAGES (carpeta con plcmon-agent-*-&lt;rid&gt;). Se omite si faltan.
/// </summary>
public class RealSshTests
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Install_connect_update_and_uninstall_on_a_real_host()
    {
        var host = Env("PLCMON_IT_SSH_HOST");
        var packages = Env("PLCMON_IT_AGENT_PACKAGES");
        Assert.SkipWhen(host is null || packages is null, "PLCMON_IT_SSH_HOST / PLCMON_IT_AGENT_PACKAGES no definidas");
        var target = new SshTarget(host!, int.Parse(Env("PLCMON_IT_SSH_PORT") ?? "22", System.Globalization.CultureInfo.InvariantCulture),
            Env("PLCMON_IT_SSH_USER") ?? "root", Env("PLCMON_IT_SSH_PASSWORD"));
        var progress = new List<string>();
        var options = new InstallOptions([packages!]);

        InstallResult installed;
        string fingerprint;
        using (var ssh = await SshNetSession.ConnectAsync(target, Ct))
        {
            fingerprint = ssh.HostKeyFingerprint;
            var deployer = new AgentDeployer(ssh, new Progress<string>(progress.Add));
            // Se parte de un equipo limpio para probar la instalación nueva (con credenciales nuevas).
            if ((await deployer.GetStatusAsync(Ct)).Installed)
                await deployer.UninstallAsync(keepConfiguration: false, target.Password, cancellationToken: Ct);
            installed = await deployer.InstallAsync(host!, options, target.Password, Ct);
            (await deployer.GetStatusAsync(Ct)).Running.ShouldBeTrue();
        }
        installed.Token.ShouldNotBeNull();

        var endpoint = new AgentEndpoint(installed.Url, installed.Token, installed.CertificateThumbprint);
        var machine = await GetMachineAsync(endpoint);
        machine.ShouldNotBeNullOrEmpty();

        // Actualización: conserva token y certificado; el agente sigue respondiendo con los mismos.
        using (var ssh = await SshNetSession.ConnectAsync(target with { ExpectedHostKey = fingerprint }, Ct))
        {
            var deployer = new AgentDeployer(ssh, new Progress<string>(progress.Add));
            var updated = await deployer.InstallAsync(host!, options, target.Password, Ct);
            (updated.Token, updated.CertificateThumbprint).ShouldBe((null, null));
            (await deployer.GetLogsAsync(target.Password, cancellationToken: Ct)).ShouldContain("Motor arrancado");
        }
        (await GetMachineAsync(endpoint)).ShouldBe(machine);

        // Huella SSH distinta: se rechaza la conexión.
        await Should.ThrowAsync<SshHostKeyMismatchException>(() => SshNetSession.ConnectAsync(target with { ExpectedHostKey = "otra" }, Ct));

        using (var ssh = await SshNetSession.ConnectAsync(target with { ExpectedHostKey = fingerprint }, Ct))
        {
            var deployer = new AgentDeployer(ssh);
            await deployer.UninstallAsync(keepConfiguration: true, target.Password, cancellationToken: Ct);
            var status = await deployer.GetStatusAsync(Ct);
            status.Installed.ShouldBeFalse();
            status.Running.ShouldBeFalse();
        }
    }

    /// <summary>Espera a que el agente responda (máximo 20 s) y devuelve el nombre de su máquina.</summary>
    private static async Task<string> GetMachineAsync(AgentEndpoint endpoint)
    {
        using var channel = AgentChannelFactory.Create(endpoint);
        var client = new AgentService.AgentServiceClient(channel);
        for (var i = 0; ; i++)
        {
            try
            {
                return (await client.GetInfoAsync(new Empty(), cancellationToken: Ct)).Machine;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable && i < 100)
            {
                await Task.Delay(200, Ct);
            }
        }
    }
}
