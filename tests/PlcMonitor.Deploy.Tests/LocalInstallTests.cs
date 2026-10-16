using System.Text;
using static PlcMonitor.Deploy.Tests.FakeSsh;

namespace PlcMonitor.Deploy.Tests;

/// <summary>Instalación en el propio equipo, sin SSH: pkexec o sudo en Linux, UAC en Windows.</summary>
public sealed class LocalInstallTests : IDisposable
{
    private readonly string _packages = Directory.CreateTempSubdirectory("plcmon-pkg-").FullName;
    private readonly List<string> _progress = [];

    public LocalInstallTests()
    {
        File.WriteAllBytes(Path.Combine(_packages, "plcmon-agent-1.0.0-linux-x64"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_packages, "plcmon-agent-1.0.0-win-x64.exe"), [4, 5]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AgentDeployer Deployer(FakeSsh ssh) => new(ssh, new SyncProgress(_progress));

    private InstallOptions Options(string? sudoPassword = null) => new([_packages], SudoPassword: sudoPassword);

    /// <summary>Este equipo con Linux, usuario normal y sudo con contraseña.</summary>
    private static FakeSsh LocalLinux(bool hasPkexec = true, int pkexecExit = 0, string journal = "") => new FakeSsh { IsLocal = true }
        .On("uname -sm", Ok("Linux x86_64\n"))
        .On("hostname", Ok("mi-pc\n"))
        .On("id -u", Ok("1000\n"))
        .On("sudo -n true", Fail("sudo: a password is required"))
        .On("command -v pkexec", hasPkexec ? Ok("/usr/bin/pkexec\n") : Fail("", 1))
        .On(c => c.EndsWith("bash -s", StringComparison.Ordinal), (c, input) =>
            c.StartsWith("pkexec", StringComparison.Ordinal) && pkexecExit != 0 ? new SshResult(pkexecExit, "", "Error executing command as another user: Not authorized")
            : input!.Contains("agent.settings.json && echo yes", StringComparison.Ordinal) ? Ok("no\n")
            : input.Contains("systemctl restart", StringComparison.Ordinal) ? Ok("active\n")
            : input.Contains("disable --now", StringComparison.Ordinal) ? Ok("desinstalado\n")
            : input.StartsWith("journalctl", StringComparison.Ordinal) ? Ok(c.StartsWith("pkexec", StringComparison.Ordinal) ? "línea del root\n" : journal)
            : Fail("script no previsto"));

    private static (string Command, string? Input) Install(FakeSsh ssh) =>
        ssh.Commands.Single(c => c.Input?.Contains("systemctl restart", StringComparison.Ordinal) == true);

    [Fact]
    public async Task Linux_without_password_asks_with_pkexec_once()
    {
        var ssh = LocalLinux();

        var result = await Deployer(ssh).InstallAsync("127.0.0.1", Options(), password: null, Ct);

        result.Url.ShouldBe("https://127.0.0.1:5311");
        result.Token.ShouldNotBeNull();
        result.HostKeyFingerprint.ShouldBeEmpty();
        Install(ssh).Command.ShouldBe("pkexec bash -s");
        // Comprobar si ya hay credenciales no necesita administrador: solo se pide permiso una vez.
        ssh.Commands.Count(c => c.Command.StartsWith("pkexec", StringComparison.Ordinal)).ShouldBe(1);
        ssh.Commands.Single(c => c.Input?.Contains("agent.settings.json && echo yes", StringComparison.Ordinal) == true).Command.ShouldBe("bash -s");
        ssh.Uploads.ShouldHaveSingleItem().Key.ShouldStartWith("/tmp/plcmon-agent-");
        _progress.ShouldContain("Pidiendo permiso de administrador (ventana del sistema)…");
    }

    [Fact]
    public async Task Linux_with_sudo_password_does_not_use_pkexec()
    {
        var ssh = LocalLinux();
        await Deployer(ssh).InstallAsync("127.0.0.1", Options(sudoPassword: "clave"), password: null, Ct);
        var install = Install(ssh);
        install.Command.ShouldBe("sudo -S -p '' bash -s");
        install.Input!.ShouldStartWith("clave\n");
        ssh.Commands.ShouldNotContain(c => c.Command.StartsWith("pkexec", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelled_pkexec_is_reported()
    {
        var ssh = LocalLinux(pkexecExit: 126);
        (await Should.ThrowAsync<DeployException>(() => Deployer(ssh).InstallAsync("127.0.0.1", Options(), null, Ct)))
            .Message.ShouldContain("denegado o cancelado");
    }

    [Fact]
    public async Task Without_pkexec_the_sudo_password_is_requested()
    {
        var ssh = LocalLinux(hasPkexec: false);
        (await Should.ThrowAsync<DeployException>(() => Deployer(ssh).UninstallAsync(true, null, cancellationToken: Ct)))
            .Message.ShouldContain("indica la contraseña de sudo");
    }

    [Fact]
    public async Task Logs_are_read_without_asking_when_the_user_can()
    {
        var ssh = LocalLinux(journal: "2026-10-16T10:00:00+0200 mi-pc plcmon-agent[1]: arrancado\n");
        (await Deployer(ssh).GetLogsAsync(null, cancellationToken: Ct)).ShouldContain("arrancado");
        ssh.Commands.ShouldNotContain(c => c.Command.StartsWith("pkexec", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Logs_ask_for_permission_when_the_journal_is_hidden()
    {
        var ssh = LocalLinux(journal: "-- No entries --\n");
        (await Deployer(ssh).GetLogsAsync(null, cancellationToken: Ct)).ShouldContain("línea del root");
    }

    [Fact]
    public async Task Windows_elevates_only_the_install_script()
    {
        var ssh = new FakeSsh { IsLocal = true }
            .On("uname -sm", Fail("'uname' no se reconoce"))
            .On("hostname", Ok("MI-PC\r\n"))
            .On(c => c.StartsWith("powershell", StringComparison.Ordinal), (c, _) =>
            {
                var script = Encoding.Unicode.GetString(Convert.FromBase64String(c[(c.LastIndexOf(' ') + 1)..]));
                return script switch
                {
                    "$env:PROCESSOR_ARCHITECTURE" => Ok("AMD64"),
                    "[IO.Path]::GetTempPath()" => Ok(@"C:\Users\yo\AppData\Local\Temp\" + "\r\n"),
                    _ when script.Contains("agent.settings.json\") {", StringComparison.Ordinal) => Ok("no"),
                    _ when script.Contains("Start-Service", StringComparison.Ordinal) => Ok("active"),
                    _ => Fail("script no previsto"),
                };
            });

        await Deployer(ssh).InstallAsync("127.0.0.1", Options(), null, Ct);

        // Una sola ventana de UAC: la de la instalación.
        ssh.ElevatedScripts.ShouldHaveSingleItem().ShouldContain("New-Service -Name PLCMonitorAgent");
    }

    [Fact]
    public void Elevated_wrapper_keeps_output_and_error_in_files()
    {
        var wrapper = AgentScripts.ElevatedWrapper("'hola'", @"C:\T\o'ut.txt", @"C:\T\err.txt");
        wrapper.ShouldContain("'hola'");
        wrapper.ShouldContain(@"WriteAllLines('C:\T\o''ut.txt', $__lines)");
        wrapper.ShouldContain(@"WriteAllText('C:\T\err.txt'");
        wrapper.ShouldContain("exit $__code");
    }

    // ---------------- LocalSession real (Linux) ----------------

    [Fact]
    public async Task Local_session_runs_commands_with_input_and_exit_code()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Comandos de Linux");
        using var local = new LocalSession();
        local.IsLocal.ShouldBeTrue();

        var echo = await local.RunAsync("bash -s", "echo \"ñ $((2+3))\"\necho error >&2\nexit 3\n", Ct);

        echo.ExitCode.ShouldBe(3);
        echo.Output.ShouldBe("ñ 5\n");
        echo.Error.ShouldBe("error\n");
        (await local.RunAsync("comando-que-no-existe-plcmon", cancellationToken: Ct)).ExitCode.ShouldBe(127);
    }

    [Fact]
    public async Task Local_session_upload_copies_the_package()
    {
        using var local = new LocalSession();
        var target = Path.Combine(_packages, "copia");
        await using (var source = File.OpenRead(Path.Combine(_packages, "plcmon-agent-1.0.0-linux-x64")))
            await local.UploadAsync(source, target, Ct);
        File.ReadAllBytes(target).ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task Local_status_works_against_this_machine()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Linux");
        using var local = new LocalSession();
        var status = await new AgentDeployer(local).GetStatusAsync(Ct);
        status.Platform.Os.ShouldBe(RemoteOs.Linux);
        status.ServiceState.ShouldNotBeNullOrEmpty();
    }

    public void Dispose() => Directory.Delete(_packages, recursive: true);

    private sealed class SyncProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink.Add(value);
    }
}
