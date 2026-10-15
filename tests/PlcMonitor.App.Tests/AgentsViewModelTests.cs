using PlcMonitor.Agent.Contracts.Client;
using PlcMonitor.Agent.Tests;
using PlcMonitor.App.Services;
using PlcMonitor.App.ViewModels;
using PlcMonitor.Deploy;
using PlcMonitor.Deploy.Tests;
using static PlcMonitor.Deploy.Tests.FakeSsh;

namespace PlcMonitor.App.Tests;

/// <summary>Perfiles de agentes y operaciones por SSH (sesión simulada).</summary>
public sealed class AgentsViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("plcmon-agents-").FullName;
    private readonly FakeDialogs _dialogs = new();
    private readonly List<SshTarget> _targets = [];

    public AgentsViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "agents"));
        File.WriteAllBytes(Path.Combine(_dir, "agents", "plcmon-agent-1.0.0-linux-x64.tar.gz"), [1, 2, 3]);
    }

    private string ProfilesPath => Path.Combine(_dir, "agents.json");

    private (MainViewModel Main, AgentsViewModel Agents) Create(FakeSsh ssh)
    {
        var main = new MainViewModel(new GrpcAgentConnector(), new ImmediateDispatcher(), _dialogs, new AppSettings(), Path.Combine(_dir, "app.json"), ProfilesPath,
            (target, _) =>
            {
                _targets.Add(target);
                return Task.FromResult<ISshSession>(ssh);
            });
        main.OpenAgentsCommand.Execute(null);
        var agents = main.Agents.ShouldNotBeNull();
        agents.PackageFolder = Path.Combine(_dir, "agents");
        return (main, agents);
    }

    private static FakeSsh Linux(bool hasSecurity = false) => new FakeSsh()
        .On("uname -sm", Ok("Linux x86_64\n"))
        .On("hostname", Ok("pc-linea\n"))
        .On("id -u", Ok("1000\n"))
        .On("sudo -n true", Fail("sudo: a password is required"))
        .On(c => c.EndsWith("bash -s", StringComparison.Ordinal), (_, input) =>
            input!.Contains("agent.settings.json && echo yes", StringComparison.Ordinal) ? Ok(hasSecurity ? "yes\n" : "no\n")
            : input.Contains("systemctl restart", StringComparison.Ordinal) ? Ok("active\n")
            : input.Contains("disable --now", StringComparison.Ordinal) ? Ok("desinstalado\n")
            : Fail("script no previsto"))
        .On(c => c.Contains("installed=yes", StringComparison.Ordinal), (_, _) => Ok("installed=yes\nactive=active\nsince=hoy\n"));

    [Fact]
    public void First_start_creates_a_local_profile()
    {
        var (main, agents) = Create(new FakeSsh());
        main.Profiles.ShouldHaveSingleItem().Name.ShouldBe("Local");
        agents.Selected!.Url.ShouldBe(AppSettings.DefaultAgentUrl);
        agents.Info.ShouldContain("Sin TLS");
    }

    [Fact]
    public void Profiles_are_saved_with_the_token_readable_only_by_the_user()
    {
        var (main, agents) = Create(new FakeSsh());
        agents.NewCommand.Execute(null);
        agents.Name = "Línea 3";
        agents.Url = "https://pc-linea:5311";
        agents.Token = "secreto";
        agents.Thumbprint = "ABCD";
        agents.SaveCommand.Execute(null);

        var saved = AgentProfileStore.Load(ProfilesPath).Single(p => p.Name == "Línea 3");
        saved.Endpoint.ShouldBe(new AgentEndpoint("https://pc-linea:5311", "secreto", "ABCD"));
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(ProfilesPath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        // El token solo aparece en StoredToken (protegido en Windows), nunca en otro campo.
        var json = File.ReadAllText(ProfilesPath);
        json.ShouldNotContain("\"Token\"", Case.Sensitive);
        json.ShouldNotContain("Endpoint", Case.Sensitive);
        main.Profiles.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Install_fills_the_profile_and_forgets_the_password()
    {
        var ssh = Linux();
        var (main, agents) = Create(ssh);
        agents.SshHost = "192.168.0.20";
        agents.SshUser = "tecnico";
        agents.SshPassword = "clave";

        await agents.InstallCommand.ExecuteAsync(null);

        var profile = agents.Selected!;
        profile.Url.ShouldBe("https://192.168.0.20:5311");
        profile.Token.ShouldNotBeNullOrEmpty();
        profile.CertificateThumbprint.ShouldNotBeNullOrEmpty();
        profile.SshHostKey.ShouldBe("huella-ssh");
        profile.Platform.ShouldBe("Linux x86_64");
        profile.InstalledAt.ShouldNotBeNull();
        agents.SshPassword.ShouldBeEmpty();
        agents.Log.ShouldContain(l => l.StartsWith("Huella SSH del equipo", StringComparison.Ordinal));
        agents.Log.ShouldContain("Agente en marcha: https://192.168.0.20:5311");
        agents.Log.Last().ShouldBe("✔ Hecho");
        _targets.ShouldHaveSingleItem().ShouldBe(new SshTarget("192.168.0.20", 22, "tecnico", "clave"));
        main.Message.ShouldContain("Agente instalado en 192.168.0.20");

        var reloaded = AgentProfileStore.Load(ProfilesPath).Single(p => p.Id == profile.Id);
        reloaded.Token.ShouldBe(profile.Token);
        File.ReadAllText(ProfilesPath).ShouldNotContain("clave");

        // La segunda vez se exige la misma huella SSH.
        agents.SshPassword = "clave";
        await agents.StatusCommand.ExecuteAsync(null);
        _targets[1].ExpectedHostKey.ShouldBe("huella-ssh");
        agents.Log.ShouldContain("El agente está en marcha desde hoy.");
    }

    [Fact]
    public async Task Failures_are_shown_without_losing_the_profile()
    {
        var (main, agents) = Create(Linux());
        agents.SshHost = "pc";
        agents.SshUser = "tecnico";

        await agents.InstallCommand.ExecuteAsync(null);

        agents.Log.Last().ShouldStartWith("✖ sudo pide contraseña");
        main.IsMessageError.ShouldBeTrue();
        agents.IsBusy.ShouldBeFalse();
        agents.Selected!.Token.ShouldBeNull();
    }

    [Fact]
    public async Task Update_on_a_host_with_unknown_credentials_warns()
    {
        var (_, agents) = Create(Linux(hasSecurity: true));
        agents.SshHost = "pc";
        agents.SshUser = "tecnico";
        agents.SshPassword = "clave";

        await agents.InstallCommand.ExecuteAsync(null);

        agents.Log.ShouldContain(l => l.Contains("Regenerar credenciales", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Uninstall_asks_and_clears_the_credentials()
    {
        var (_, agents) = Create(Linux());
        agents.SshHost = "pc";
        agents.SshUser = "tecnico";
        agents.SshPassword = "clave";
        await agents.InstallCommand.ExecuteAsync(null);

        _dialogs.Answer = false;
        await agents.UninstallCommand.ExecuteAsync(null);
        agents.Selected!.Token.ShouldNotBeNull();

        _dialogs.Answer = true;
        agents.SshPassword = "clave";
        await agents.UninstallCommand.ExecuteAsync(null);
        agents.Selected!.Token.ShouldBeNull();
        agents.Selected.InstalledAt.ShouldBeNull();
        agents.Log.ShouldContain("Agente desinstalado");
    }

    [Fact]
    public async Task Removing_a_profile_does_not_touch_the_host()
    {
        var ssh = new FakeSsh();
        var (main, agents) = Create(ssh);
        agents.NewCommand.Execute(null);
        main.Profiles.Count.ShouldBe(2);

        await agents.DeleteCommand.ExecuteAsync(null);

        main.Profiles.ShouldHaveSingleItem();
        ssh.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task Connecting_uses_the_token_of_the_selected_profile()
    {
        var token = AgentSecurity.NewToken();
        await using var agent = new AgentFactory();
        agent.Settings["Agent:Security:TokenSha256"] = AgentSecurity.HashToken(token);
        var connector = new TokenAwareConnector(agent);
        var ui = new LockingDispatcher();
        await using var main = new MainViewModel(connector, ui, _dialogs, new AppSettings(), Path.Combine(_dir, "app.json"), ProfilesPath);

        await main.ConnectCommand.ExecuteAsync(null);
        main.IsConnected.ShouldBeFalse();
        main.Message.ShouldContain("rechaza el token");

        main.SelectedProfile!.Token = token;
        await main.ConnectCommand.ExecuteAsync(null);
        main.IsConnected.ShouldBeTrue(main.Message);
        connector.Endpoints.Last().Token.ShouldBe(token);
    }

    /// <summary>
    /// De punta a punta con SSH real (PLCMON_IT_SSH_HOST, _PORT, _USER, _PASSWORD y PLCMON_IT_AGENT_PACKAGES): la app
    /// instala el agente, guarda URL, token y huella, y se conecta a él por TLS. Al final lo desinstala.
    /// </summary>
    [Fact]
    public async Task Install_with_real_ssh_and_connect_over_tls()
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
        var host = Env("PLCMON_IT_SSH_HOST");
        var packages = Env("PLCMON_IT_AGENT_PACKAGES");
        Assert.SkipWhen(host is null || packages is null, "PLCMON_IT_SSH_HOST / PLCMON_IT_AGENT_PACKAGES no definidas");
        var ui = new LockingDispatcher();
        await using var main = new MainViewModel(new GrpcAgentConnector(), ui, _dialogs, new AppSettings(), Path.Combine(_dir, "app.json"), ProfilesPath);
        main.OpenAgentsCommand.Execute(null);
        var agents = main.Agents!;
        agents.NewCommand.Execute(null);
        agents.Name = "Contenedor";
        agents.SshHost = host!;
        agents.SshPort = int.Parse(Env("PLCMON_IT_SSH_PORT") ?? "22", System.Globalization.CultureInfo.InvariantCulture);
        agents.SshUser = Env("PLCMON_IT_SSH_USER") ?? "root";
        agents.SshPassword = Env("PLCMON_IT_SSH_PASSWORD") ?? "";
        agents.PackageFolder = packages!;
        agents.RegenerateCredentials = true;

        await agents.InstallCommand.ExecuteAsync(null);
        agents.Log.Last().ShouldBe("✔ Hecho", string.Join("\n", agents.Log));

        main.SelectedProfile = agents.Selected;
        main.CloseAgentsCommand.Execute(null);
        await main.ConnectCommand.ExecuteAsync(null);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!main.IsConnected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            await main.ConnectCommand.ExecuteAsync(null);
        }
        main.IsConnected.ShouldBeTrue(main.Message);
        main.SelectedProfile!.Url.ShouldStartWith("https://");
        await main.DisconnectCommand.ExecuteAsync(null);

        main.OpenAgentsCommand.Execute(null);
        main.Agents!.Selected = main.Profiles.Single(p => p.Name == "Contenedor");
        main.Agents.SshPassword = Env("PLCMON_IT_SSH_PASSWORD") ?? "";
        await main.Agents.UninstallCommand.ExecuteAsync(null);
        main.Agents.Log.ShouldContain("Agente desinstalado");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Conecta con el agente en memoria enviando el token del endpoint.</summary>
    private sealed class TokenAwareConnector(AgentFactory agent) : IAgentConnector
    {
        public List<AgentEndpoint> Endpoints { get; } = [];

        public (PlcMonitor.Agent.Contracts.AgentService.AgentServiceClient Client, IDisposable Channel) Connect(AgentEndpoint endpoint)
        {
            Endpoints.Add(endpoint);
            var channel = Grpc.Net.Client.GrpcChannel.ForAddress(agent.Server.BaseAddress, new Grpc.Net.Client.GrpcChannelOptions
            {
                HttpHandler = agent.Server.CreateHandler(),
                Credentials = endpoint.Token is null ? null : Grpc.Core.ChannelCredentials.Create(Grpc.Core.ChannelCredentials.Insecure,
                    Grpc.Core.CallCredentials.FromInterceptor((_, m) =>
                    {
                        m.Add("authorization", $"Bearer {endpoint.Token}");
                        return Task.CompletedTask;
                    })),
                UnsafeUseInsecureChannelCallCredentials = true,
            });
            return (new PlcMonitor.Agent.Contracts.AgentService.AgentServiceClient(channel), channel);
        }
    }
}
