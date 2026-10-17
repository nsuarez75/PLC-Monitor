using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PlcMonitor.Agent.Contracts.Client;
using static PlcMonitor.Deploy.Tests.FakeSsh;

namespace PlcMonitor.Deploy.Tests;

public sealed class AgentDeployerTests : IDisposable
{
    private readonly string _packages = Directory.CreateTempSubdirectory("plcmon-pkg-").FullName;
    private readonly List<string> _progress = [];

    public AgentDeployerTests()
    {
        File.WriteAllBytes(Path.Combine(_packages, "plcmon-agent-1.0.0-linux-x64"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_packages, "plcmon-agent-1.0.0-win-x64.exe"), [4, 5]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AgentDeployer Deployer(FakeSsh ssh) => new(ssh, new SyncProgress(_progress));

    private InstallOptions Options(bool regenerate = false) => new([_packages], Port: 5311, RegenerateCredentials: regenerate);

    /// <summary>Equipo Linux x64 con usuario normal y sudo con contraseña.</summary>
    private static FakeSsh Linux(bool hasSecurity = false, bool root = false, bool sudoNoPassword = false) => new FakeSsh()
        .On("uname -sm", Ok("Linux x86_64\n"))
        .On("hostname", Ok("pc-linea\n"))
        .On("id -u", Ok(root ? "0\n" : "1000\n"))
        .On("sudo -n true", sudoNoPassword ? Ok() : Fail("sudo: a password is required"))
        .On(c => c.EndsWith("bash -s", StringComparison.Ordinal), (_, input) =>
            input!.Contains("agent.settings.json && echo yes", StringComparison.Ordinal) ? Ok(hasSecurity ? "yes\n" : "no\n")
            : input.Contains("systemctl restart", StringComparison.Ordinal) ? Ok("active\n")
            : input.Contains("disable --now", StringComparison.Ordinal) ? Ok("desinstalado\n")
            : Fail("script no previsto"));

    private static string InstallScript(FakeSsh ssh) => ssh.Commands.Single(c => c.Input?.Contains("systemctl restart", StringComparison.Ordinal) == true).Input!;

    [Theory]
    [InlineData("Linux x86_64", "linux-x64")]
    [InlineData("Linux aarch64", "linux-arm64")]
    public async Task Linux_architecture_is_detected(string uname, string rid)
    {
        var ssh = new FakeSsh().On("uname -sm", Ok(uname));
        (await Deployer(ssh).DetectPlatformAsync(Ct)).ShouldBe(new RemotePlatform(RemoteOs.Linux, rid, $"Linux {uname.Split(' ')[1]}"));
    }

    [Fact]
    public async Task Windows_is_detected_when_uname_is_missing()
    {
        var ssh = new FakeSsh()
            .On("uname -sm", Fail("'uname' no se reconoce como un comando interno o externo"))
            .On("powershell", Ok("AMD64\r\n"));
        var platform = await Deployer(ssh).DetectPlatformAsync(Ct);
        (platform.Os, platform.Rid).ShouldBe((RemoteOs.Windows, "win-x64"));
        ssh.PowerShellScripts.ShouldHaveSingleItem().ShouldBe("$env:PROCESSOR_ARCHITECTURE");
    }

    [Fact]
    public async Task Unsupported_system_is_reported()
    {
        var ssh = new FakeSsh().On("uname -sm", Ok("FreeBSD amd64")).On("powershell", Fail("no"));
        (await Should.ThrowAsync<DeployException>(() => Deployer(ssh).DetectPlatformAsync(Ct))).Message.ShouldContain("FreeBSD");
    }

    [Fact]
    public async Task Fresh_linux_install_creates_credentials_and_the_service()
    {
        var ssh = Linux();

        var result = await Deployer(ssh).InstallAsync("192.168.0.20", Options(), "secreto", Ct);

        result.Url.ShouldBe("https://192.168.0.20:5311");
        result.Token.ShouldNotBeNull();
        result.CertificateThumbprint.ShouldNotBeNull();
        result.HostKeyFingerprint.ShouldBe("huella-ssh");
        var upload = ssh.Uploads.ShouldHaveSingleItem();
        upload.Key.ShouldStartWith("/tmp/plcmon-agent-");
        upload.Value.ShouldBe(new byte[] { 1, 2, 3 });

        var install = ssh.Commands.Single(c => c.Input?.Contains("systemctl restart", StringComparison.Ordinal) == true);
        install.Command.ShouldBe("sudo -S -p '' bash -s");
        install.Input!.ShouldStartWith("secreto\n");
        var script = install.Input;
        script.ShouldContain(upload.Key);
        script.ShouldContain("useradd --system");
        script.ShouldContain("ExecStart=/opt/plc-monitor/agent/plcmon-agent");
        script.ShouldContain("TimeoutStopSec=15"); // margen para parar ordenadamente aunque el sistema baje el valor por defecto
        script.ShouldContain("ufw allow 5311/tcp");

        var settings = Decode(script, "agent.settings.json");
        using var json = JsonDocument.Parse(settings);
        AgentSecurity.Matches(result.Token, json.RootElement.GetProperty("Agent").GetProperty("Security").GetProperty("TokenSha256").GetString()!).ShouldBeTrue();
        json.RootElement.GetProperty("Kestrel").GetProperty("Endpoints").GetProperty("Grpc").GetProperty("Url").GetString().ShouldBe("https://0.0.0.0:5311");
        Decode(script, "agent.pfx").ShouldNotBeEmpty();
        _progress.ShouldContain("Equipo: Linux x86_64 (paquete linux-x64)");
        _progress.ShouldContain("Agente en marcha: https://192.168.0.20:5311");
    }

    [Fact]
    public async Task Update_keeps_the_existing_credentials()
    {
        var ssh = Linux(hasSecurity: true);

        var result = await Deployer(ssh).InstallAsync("pc", Options(), "secreto", Ct);

        (result.Token, result.CertificateThumbprint).ShouldBe((null, null));
        InstallScript(ssh).ShouldNotContain("agent.settings.json\"");
        _progress.ShouldContain("El equipo ya tiene credenciales: se conservan (actualización).");
    }

    [Fact]
    public async Task Credentials_can_be_regenerated_on_update()
    {
        var ssh = Linux(hasSecurity: true);
        var result = await Deployer(ssh).InstallAsync("pc", Options(regenerate: true), "secreto", Ct);
        result.Token.ShouldNotBeNull();
        InstallScript(ssh).ShouldContain("agent.settings.json\"");
    }

    [Theory]
    [InlineData(true, false, "bash -s")]
    [InlineData(false, true, "sudo -n bash -s")]
    public async Task Root_or_passwordless_sudo_do_not_send_a_password(bool root, bool nopasswd, string expected)
    {
        var ssh = Linux(root: root, sudoNoPassword: nopasswd);
        await Deployer(ssh).InstallAsync("pc", Options(), password: null, Ct);
        var install = ssh.Commands.Single(c => c.Input?.Contains("systemctl restart", StringComparison.Ordinal) == true);
        install.Command.ShouldBe(expected);
        install.Input!.ShouldStartWith("set -e");
    }

    [Fact]
    public async Task Sudo_without_password_available_is_explained()
    {
        var ssh = Linux();
        (await Should.ThrowAsync<DeployException>(() => Deployer(ssh).InstallAsync("pc", Options(), password: null, Ct))).Message.ShouldContain("sudo pide contraseña");
    }

    [Fact]
    public async Task Missing_package_is_reported_with_the_folders()
    {
        var ssh = new FakeSsh().On("uname -sm", Ok("Linux aarch64"));
        var ex = await Should.ThrowAsync<DeployException>(() => Deployer(ssh).InstallAsync("pc", Options(), "x", Ct));
        ex.Message.ShouldContain("linux-arm64");
        ex.Message.ShouldContain(_packages);
    }

    [Fact]
    public async Task Remote_failure_is_reported_with_its_output()
    {
        var ssh = Linux().On(c => c == "sudo -S -p '' bash -s", (_, input) =>
            input!.Contains("systemctl restart", StringComparison.Ordinal) ? new SshResult(1, "", "tar: Error is not recoverable") : Ok("no\n"));
        (await Should.ThrowAsync<DeployException>(() => Deployer(ssh).InstallAsync("pc", Options(), "x", Ct))).Message.ShouldContain("tar: Error is not recoverable");
    }

    [Fact]
    public async Task Windows_install_uses_powershell_with_service_and_firewall()
    {
        var ssh = new FakeSsh()
            .On("uname -sm", Fail("no se reconoce"))
            .On("hostname", Ok("PC-WIN\r\n"))
            .On(c => c.StartsWith("powershell", StringComparison.Ordinal), (c, _) =>
            {
                var script = Encoding.Unicode.GetString(Convert.FromBase64String(c[(c.LastIndexOf(' ') + 1)..]));
                return script switch
                {
                    "$env:PROCESSOR_ARCHITECTURE" => Ok("AMD64"),
                    "[IO.Path]::GetTempPath()" => Ok(@"C:\Users\admin\AppData\Local\Temp\" + "\r\n"),
                    _ when script.Contains("agent.settings.json\") {", StringComparison.Ordinal) => Ok("no"),
                    _ when script.Contains("Start-Service", StringComparison.Ordinal) => Ok("Firewall: abierto el puerto 5311/tcp\r\nactive"),
                    _ => Fail("script no previsto"),
                };
            });

        var result = await Deployer(ssh).InstallAsync("pc-win", Options(), "secreto", Ct);

        result.Platform.Rid.ShouldBe("win-x64");
        result.Token.ShouldNotBeNull();
        var upload = ssh.Uploads.ShouldHaveSingleItem();
        upload.Key.ShouldStartWith("/C:/Users/admin/AppData/Local/Temp/plcmon-agent-");
        upload.Key.ShouldEndWith(".exe");
        var install = ssh.PowerShellScripts.Single(s => s.Contains("Start-Service", StringComparison.Ordinal));
        install.ShouldContain(@"$A = 'C:\Users\admin\AppData\Local\Temp\plcmon-agent-");
        install.ShouldContain("New-Service -Name PLCMonitorAgent");
        install.ShouldContain("-LocalPort 5311");
        install.ShouldContain("agent.pfx");
        ssh.Commands.ShouldNotContain(c => c.Command.StartsWith("sudo", StringComparison.Ordinal));
        _progress.ShouldContain("Firewall: abierto el puerto 5311/tcp");
    }

    private static FakeSsh Windows(Func<string, SshResult> powerShell) => new FakeSsh()
        .On("uname -sm", Fail("no se reconoce"))
        .On(c => c.StartsWith("powershell", StringComparison.Ordinal), (c, _) =>
        {
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(c[(c.LastIndexOf(' ') + 1)..]));
            return script == "$env:PROCESSOR_ARCHITECTURE" ? Ok("AMD64") : powerShell(script);
        });

    [Fact]
    public async Task Windows_logs_are_read_from_the_agent_files_without_elevation()
    {
        var ssh = Windows(script => script.Contains("plcmon-agent-*.log", StringComparison.Ordinal)
            ? Ok("2026-10-17 09:30:00.000 INF PlcMonitor.Engine.TagEngine: Motor arrancado\r\n")
            : Fail("script no previsto"));

        (await Deployer(ssh).GetLogsAsync(null, lines: 50, cancellationToken: Ct)).ShouldContain("Motor arrancado");
        ssh.ElevatedScripts.ShouldBeEmpty();
        ssh.PowerShellScripts.Single(s => s.Contains("logs", StringComparison.Ordinal)).ShouldContain("-Tail 50");
    }

    [Fact]
    public async Task Windows_agent_without_log_files_says_how_to_get_them()
    {
        var ssh = Windows(_ => Ok("nolog\r\n"));
        (await Deployer(ssh).GetLogsAsync(null, cancellationToken: Ct)).ShouldContain("Instalar / actualizar");
    }

    [Fact]
    public void Windows_update_keeps_the_logs()
    {
        AgentScripts.WindowsInstall(@"C:\Temp\a.exe", null, 5311).ShouldContain("'history', 'logs'");
    }

    [Fact]
    public async Task Status_is_parsed()
    {
        var ssh = new FakeSsh().On("uname -sm", Ok("Linux x86_64"))
            .On(c => c.Contains("installed=yes", StringComparison.Ordinal), (_, _) => Ok("installed=yes\nactive=active\nsince=Thu 2026-10-15 07:00:00 CEST\n"));
        var status = await Deployer(ssh).GetStatusAsync(Ct);
        status.Installed.ShouldBeTrue();
        status.Running.ShouldBeTrue();
        status.Since.ShouldBe("Thu 2026-10-15 07:00:00 CEST");
    }

    [Fact]
    public async Task Uninstall_can_keep_the_configuration()
    {
        var ssh = Linux();
        await Deployer(ssh).UninstallAsync(keepConfiguration: true, "secreto", cancellationToken: Ct);
        var script = ssh.Commands.Single(c => c.Input?.Contains("disable --now", StringComparison.Ordinal) == true).Input!;
        script.ShouldContain("agent.json.backup");
        script.ShouldContain("agent.key.backup");
        script.ShouldContain("rm -rf \"$D\"");
        _progress.ShouldContain("Agente desinstalado");
    }

    [Fact]
    public void Generated_linux_scripts_are_valid_bash()
    {
        var files = new AgentSecurityFiles(AgentScripts.SettingsJson("ABC", 5311, "clave"), [1, 2, 3]);
        foreach (var script in new[]
                 {
                     AgentScripts.LinuxInstall("/tmp/x", files, 5311),
                     AgentScripts.LinuxInstall("/tmp/x", null, 5311),
                     AgentScripts.LinuxUninstall(true, 5311),
                     AgentScripts.LinuxUninstall(false, 5311),
                     AgentScripts.LinuxStatus,
                 })
        {
            var check = Process.Start(new ProcessStartInfo("bash", "-n") { RedirectStandardInput = true, RedirectStandardError = true })!;
            check.StandardInput.Write(script);
            check.StandardInput.Close();
            check.WaitForExit();
            check.ExitCode.ShouldBe(0, check.StandardError.ReadToEnd() + "\n" + script);
        }
    }

    [Fact]
    public void Packages_are_found_by_runtime()
    {
        AgentPackages.Find("linux-x64", [_packages]).ShouldEndWith("plcmon-agent-1.0.0-linux-x64");
        AgentPackages.Find("win-x64", [_packages, "/no/existe"]).ShouldEndWith("plcmon-agent-1.0.0-win-x64.exe");
        AgentPackages.Find("linux-arm64", [_packages]).ShouldBeNull();
    }

    [Fact]
    public void Embedded_agent_is_used_when_no_folder_has_it()
    {
        var bundle = typeof(AgentDeployerTests).Assembly;
        AgentPackages.Embedded(bundle).ShouldBe(["linux-riscv64"]);

        var path = AgentPackages.Find("linux-riscv64", [_packages], bundle).ShouldNotBeNull();

        File.ReadAllText(path).ShouldBe("agente-de-prueba");
        Path.GetFileName(path).ShouldEndWith("-linux-riscv64");
        // Una carpeta con el agente tiene preferencia (para probar otra versión).
        AgentPackages.Find("linux-x64", [_packages], bundle).ShouldEndWith("plcmon-agent-1.0.0-linux-x64");
        AgentPackages.Find("win-arm64", [_packages], bundle).ShouldBeNull();
    }

    [Fact]
    public void Linux_install_replaces_everything_but_configuration_and_history()
    {
        var script = AgentScripts.LinuxInstall("/tmp/plcmon-agent-x", null, 5311);
        script.ShouldContain("! -name agent.json ! -name agent.key ! -name agent.settings.json ! -name agent.pfx ! -name history -exec rm -rf {} +");
        script.ShouldContain("install -m 0755 \"$A\" \"$D/plcmon-agent\"");
        script.ShouldContain("Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/opt/plc-monitor/agent/.bundle");
        script.ShouldNotContain("tar ");
    }

    /// <summary>Saca de un script el contenido en base64 que se escribe en <paramref name="file"/>.</summary>
    private static string Decode(string script, string file)
    {
        var line = script.Split('\n').Single(l => l.Contains($"/{file}\"", StringComparison.Ordinal) && l.Contains("base64 -d", StringComparison.Ordinal));
        var b64 = line.Split('\'')[1];
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

    public void Dispose() => Directory.Delete(_packages, recursive: true);

    private sealed class SyncProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink.Add(value);
    }
}
