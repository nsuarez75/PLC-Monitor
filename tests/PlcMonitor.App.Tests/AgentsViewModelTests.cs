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
    private int _localSessions;

    public AgentsViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "agents"));
        File.WriteAllBytes(Path.Combine(_dir, "agents", "plcmon-agent-1.0.0-linux-x64"), [1, 2, 3]);
    }

    private string ProfilesPath => Path.Combine(_dir, "agents.json");

    private (MainViewModel Main, AgentsViewModel Agents) Create(FakeSsh ssh)
    {
        var main = new MainViewModel(new GrpcAgentConnector(), new ImmediateDispatcher(), _dialogs, new AppSettings(), Path.Combine(_dir, "app.json"), ProfilesPath,
            (target, _) =>
            {
                _targets.Add(target);
                return Task.FromResult<ISshSession>(ssh);
            },
            () =>
            {
                _localSessions++;
                return ssh;
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
    public async Task This_machine_installs_without_ssh()
    {
        var (main, agents) = Create(LocalLinux());
        agents.NewCommand.Execute(null);
        agents.Name = "Este PC";
        agents.LocalMachine = true;

        agents.IsSsh.ShouldBeFalse();
        agents.InstallCommand.CanExecute(null).ShouldBeTrue();
        await agents.InstallCommand.ExecuteAsync(null);

        _targets.ShouldBeEmpty();
        _localSessions.ShouldBe(1);
        var profile = agents.Selected!;
        profile.LocalMachine.ShouldBeTrue();
        profile.Url.ShouldBe("https://127.0.0.1:5311");
        profile.Token.ShouldNotBeNullOrEmpty();
        profile.SshHostKey.ShouldBeNull();
        profile.ManagedLabel.ShouldBe("Este equipo (sin SSH)");
        agents.Log[0].ShouldBe("Instalando: en este equipo (sin SSH)…");
        agents.Log.ShouldContain("Pidiendo permiso de administrador (ventana del sistema)…");
        agents.Log.Last().ShouldBe("✔ Hecho");
        main.Message.ShouldContain("Agente instalado en este equipo");
        agents.Info.ShouldStartWith("Este equipo");
        AgentProfileStore.Load(ProfilesPath).Single(p => p.Id == profile.Id).LocalMachine.ShouldBeTrue();
    }

    /// <summary>Este equipo con Linux: sudo pide contraseña y hay pkexec.</summary>
    private static FakeSsh LocalLinux() => new FakeSsh { IsLocal = true }
        .On("uname -sm", Ok("Linux x86_64\n"))
        .On("hostname", Ok("mi-pc\n"))
        .On("id -u", Ok("1000\n"))
        .On("sudo -n true", Fail("sudo: a password is required"))
        .On("command -v pkexec", Ok("/usr/bin/pkexec\n"))
        .On(c => c.EndsWith("bash -s", StringComparison.Ordinal), (_, input) =>
            input!.Contains("agent.settings.json && echo yes", StringComparison.Ordinal) ? Ok("no\n")
            : input.Contains("systemctl restart", StringComparison.Ordinal) ? Ok("active\n")
            : Fail("script no previsto"));

    [Fact]
    public async Task Selection_and_activity_survive_an_operation()
    {
        var (main, agents) = Create(Linux());
        var profile = agents.Selected!;
        main.SelectedProfile = profile;
        agents.SshHost = "192.168.0.20";
        agents.SshUser = "tecnico";
        agents.SshPassword = "clave";

        await agents.InstallCommand.ExecuteAsync(null);

        // Antes se sustituía el perfil en la colección: se perdía la selección y se borraba la actividad.
        main.SelectedProfile.ShouldBeSameAs(profile);
        agents.Selected.ShouldBeSameAs(profile);
        main.Profiles.ShouldContain(profile);
        agents.Log.ShouldContain("✔ Hecho");
        agents.Url.ShouldBe("https://192.168.0.20:5311");
        agents.Token.ShouldBe(profile.Token);
    }

    [Fact]
    public async Task Installing_the_selected_profile_updates_the_url_to_connect_to()
    {
        // Caso real en Windows: perfil «Local» (http://127.0.0.1:5310) seleccionado, se instala y Conectar seguía yendo a 5310.
        var (main, agents) = Create(LocalLinux());
        var profile = main.SelectedProfile.ShouldNotBeNull();
        agents.Selected.ShouldBeSameAs(profile);
        main.AgentUrl.ShouldBe(AppSettings.DefaultAgentUrl);
        agents.LocalMachine = true;

        await agents.InstallCommand.ExecuteAsync(null);

        main.SelectedProfile.ShouldBeSameAs(profile);
        main.AgentUrl.ShouldBe("https://127.0.0.1:5311");
        main.CloseAgentsCommand.Execute(null);
        main.AgentUrl.ShouldBe(profile.Url);
    }

    [Fact]
    public void Package_folder_is_remembered()
    {
        var (main, agents) = Create(new FakeSsh());
        agents.PackageFolder = "/srv/paquetes";

        AppSettings.Load(Path.Combine(_dir, "app.json")).AgentPackagesFolder.ShouldBe("/srv/paquetes");
        main.OpenAgentsCommand.Execute(null);
        main.Agents!.PackageFolder.ShouldBe("/srv/paquetes");
    }

    [Fact]
    public void Release_packages_are_found_from_a_build_folder()
    {
        var repo = Path.Combine(_dir, "repo");
        var agentsDir = Directory.CreateDirectory(Path.Combine(repo, "artifacts", "release", "1.1.0", "agents")).FullName;
        File.WriteAllBytes(Path.Combine(agentsDir, "plcmon-agent-1.1.0-linux-x64"), [1]);
        var bin = Directory.CreateDirectory(Path.Combine(repo, "src", "PlcMonitor.App", "bin", "Debug", "net10.0")).FullName;

        AgentPackages.Find("linux-x64", AgentPackages.DefaultFolders(bin)).ShouldBe(Path.Combine(agentsDir, "plcmon-agent-1.1.0-linux-x64"));
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
    public async Task Credentials_are_renewed_without_reinstalling_and_the_read_only_token_is_shown_once()
    {
        var ssh = Linux(hasSecurity: true);
        var (_, agents) = Create(ssh);
        agents.SshHost = "pc";
        agents.SshUser = "tecnico";
        agents.SshPassword = "clave";
        await agents.InstallCommand.ExecuteAsync(null);
        var old = (agents.Selected!.Token, agents.Selected.CertificateThumbprint);

        _dialogs.Answer = false;
        agents.SshPassword = "clave";
        await agents.RenewCredentialsCommand.ExecuteAsync(null);
        (agents.Selected.Token, agents.Selected.CertificateThumbprint).ShouldBe(old);

        _dialogs.Answer = true;
        agents.SshPassword = "clave";
        agents.CreateReadOnlyToken = true;
        await agents.RenewCredentialsCommand.ExecuteAsync(null);

        _dialogs.Questions.Last().ShouldContain("Se crea un token de solo lectura nuevo.");
        agents.Selected.Token.ShouldNotBeNullOrEmpty();
        agents.Selected.Token.ShouldNotBe(old.Token);
        agents.Selected.CertificateThumbprint.ShouldNotBe(old.CertificateThumbprint);
        ssh.Uploads.Count.ShouldBe(1); // solo el de la instalación
        var index = agents.Log.IndexOf("Token de solo lectura (cópialo ahora: la app no lo guarda):");
        index.ShouldBeGreaterThanOrEqualTo(0);
        var reader = agents.Log[index + 1];
        reader.Length.ShouldBe(43);
        reader.ShouldNotBe(agents.Selected.Token);
        File.ReadAllText(ProfilesPath).ShouldNotContain(reader);
        agents.CreateReadOnlyToken.ShouldBeFalse();
        agents.SshPassword.ShouldBeEmpty();
        agents.Log.Last().ShouldBe("✔ Hecho");
    }

    [Fact]
    public async Task Fresh_install_can_create_the_read_only_token()
    {
        var (_, agents) = Create(Linux());
        agents.SshHost = "pc";
        agents.SshUser = "tecnico";
        agents.SshPassword = "clave";
        agents.CreateReadOnlyToken = true;

        await agents.InstallCommand.ExecuteAsync(null);

        agents.Log.ShouldContain("Token de solo lectura (cópialo ahora: la app no lo guarda):");
    }

    [Fact]
    public async Task Read_only_token_connects_but_does_not_offer_writes()
    {
        var full = AgentSecurity.NewToken();
        var reader = AgentSecurity.NewToken();
        await using var agent = new AgentFactory(AgentFactory.Device("plc1", writesEnabled: true,
            tags: [new PlcMonitor.Engine.Configuration.TagConfiguration { Address = "DB1.DBW2", Name = "consigna", Writable = true }]));
        agent.Settings["Agent:Security:TokenSha256"] = AgentSecurity.HashToken(full);
        agent.Settings["Agent:Security:ReadOnlyTokenSha256"] = AgentSecurity.HashToken(reader);
        var ui = new LockingDispatcher();
        await using var main = new MainViewModel(new TokenAwareConnector(agent), ui, _dialogs, new AppSettings(), Path.Combine(_dir, "app.json"), ProfilesPath);

        main.SelectedProfile!.Token = reader;
        await main.ConnectCommand.ExecuteAsync(null);

        main.IsConnected.ShouldBeTrue(main.Message);
        main.ReadOnlyAccess.ShouldBeTrue();
        main.AgentDescription.ShouldEndWith(" · solo lectura");
        await Wait.UntilAsync(() => ui.Read(() => main.AllTags.Any()), "tags");
        ui.Read(() => main.AllTags.Single().Writable).ShouldBeFalse();
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
