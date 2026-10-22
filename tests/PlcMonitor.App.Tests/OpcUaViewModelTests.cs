using System.Net;
using System.Net.Sockets;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.Agent.Tests;
using PlcMonitor.App.Services;
using PlcMonitor.App.ViewModels;

namespace PlcMonitor.App.Tests;

/// <summary>Pestaña OPC UA de la app contra el agente en memoria.</summary>
public sealed class OpcUaViewModelTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"plcmon-app-{Guid.NewGuid():N}.json");
    private readonly string _profilesPath = Path.Combine(Path.GetTempPath(), $"plcmon-agents-{Guid.NewGuid():N}.json");
    private readonly FakeDialogs _dialogs = new();

    public OpcUaViewModelTests()
    {
        OpcUaViewModel.AutoRefreshInterval = TimeSpan.FromMilliseconds(100);
    }

    private async Task<MainViewModel> ConnectAsync(AgentFactory agent)
    {
        var vm = new MainViewModel(new InMemoryConnector(agent), new ImmediateDispatcher(), _dialogs, new AppSettings(), _settingsPath, _profilesPath);
        await vm.ConnectCommand.ExecuteAsync(null);
        vm.IsConnected.ShouldBeTrue(vm.Message);
        return vm;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task The_page_shows_the_server_of_the_connected_agent()
    {
        await using var agent = new AgentFactory();
        await using var vm = await ConnectAsync(agent);

        await vm.OpenOpcUaCommand.ExecuteAsync(null);

        vm.IsOpcUaPage.ShouldBeTrue();
        var panel = vm.OpcUaPanel.ShouldNotBeNull();
        (panel.Enabled, panel.Port, panel.AllowWrites, panel.StateText).ShouldBe((false, 4840, false, "Parado"));
        panel.Users.ShouldBeEmpty();
        panel.IsDirty.ShouldBeFalse();
        panel.CanEdit.ShouldBeTrue();
    }

    [Fact]
    public async Task Enabling_with_a_user_starts_the_server_and_forgets_the_password()
    {
        await using var agent = new AgentFactory();
        await using var vm = await ConnectAsync(agent);
        await vm.OpenOpcUaCommand.ExecuteAsync(null);
        var panel = vm.OpcUaPanel!;

        panel.AddUserCommand.Execute(null);
        panel.Users[0].Name = "ignition";
        await panel.SaveCommand.ExecuteAsync(null);
        panel.Errors.ShouldBe("Escribe una contraseña para «ignition».");

        panel.Users[0].Password = "clave-de-prueba";
        panel.Enabled = true;
        panel.Port = FreePort();
        await panel.SaveCommand.ExecuteAsync(null);

        panel.Errors.ShouldBeEmpty();
        panel.IsRunning.ShouldBeTrue(panel.Detail);
        panel.EndpointUrl.ShouldEndWith($":{panel.Port}");
        panel.CertificateThumbprint.ShouldNotBeNullOrEmpty();
        var user = panel.Users.ShouldHaveSingleItem();
        (user.Name, user.Password, user.HasPassword, user.PasswordHint).ShouldBe(("ignition", "", true, "(sin cambios)"));
        panel.IsDirty.ShouldBeFalse();
        vm.Message.ShouldStartWith("Servidor OPC UA en marcha: opc.tcp://");
    }

    [Fact]
    public async Task Allowing_writes_asks_first()
    {
        await using var agent = new AgentFactory();
        await using var vm = await ConnectAsync(agent);
        await vm.OpenOpcUaCommand.ExecuteAsync(null);
        var panel = vm.OpcUaPanel!;
        panel.AllowWrites = true;

        _dialogs.Answer = false;
        await panel.SaveCommand.ExecuteAsync(null);

        _dialogs.Questions.ShouldHaveSingleItem().ShouldContain("¿Permitir escribir en los PLC desde OPC UA?");
        panel.IsDirty.ShouldBeTrue();
        (await vm.Client!.GetOpcUaAsync(new Google.Protobuf.WellKnownTypes.Empty())).AllowWrites.ShouldBeFalse();
    }

    [Fact]
    public async Task While_visible_the_page_refreshes_itself_without_touching_edits()
    {
        await using var agent = new AgentFactory();
        await using var vm = await ConnectAsync(agent);
        await vm.OpenOpcUaCommand.ExecuteAsync(null);
        var panel = vm.OpcUaPanel!;
        panel.Port = 4999; // cambio sin guardar
        var port = FreePort();

        // Otro equipo (o la CLI) activa el servidor: la pestaña lo ve sola.
        await vm.Client!.PutOpcUaAsync(new PutOpcUaRequest
        {
            Enabled = true,
            Port = port,
            Users = { new OpcUaUser { Name = "ignition", Password = "clave-de-prueba" } },
        });
        await Wait.UntilAsync(() => panel.IsRunning, "estado en marcha sin pulsar «Actualizar»");

        panel.EndpointUrl.ShouldEndWith($":{port}");
        panel.Port.ShouldBe(4999);
        panel.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public async Task Leaving_the_page_stops_refreshing()
    {
        await using var agent = new AgentFactory();
        await using var vm = await ConnectAsync(agent);
        await vm.OpenOpcUaCommand.ExecuteAsync(null);
        var panel = vm.OpcUaPanel!;

        vm.ShowTagsCommand.Execute(null);
        await vm.Client!.PutOpcUaAsync(new PutOpcUaRequest
        {
            Enabled = true,
            Port = FreePort(),
            Users = { new OpcUaUser { Name = "ignition", Password = "clave-de-prueba" } },
        });
        await Task.Delay(500);

        panel.IsRunning.ShouldBeFalse();
        await vm.OpenOpcUaCommand.ExecuteAsync(null);
        panel.IsRunning.ShouldBeTrue(panel.Detail);
    }

    [Fact]
    public async Task Disconnecting_closes_the_page()
    {
        await using var agent = new AgentFactory();
        await using var vm = await ConnectAsync(agent);
        await vm.OpenOpcUaCommand.ExecuteAsync(null);

        await vm.DisconnectCommand.ExecuteAsync(null);

        vm.OpcUaPanel.ShouldBeNull();
        vm.IsTagsPage.ShouldBeTrue();
        vm.OpenOpcUaCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task With_the_read_only_token_nothing_can_be_changed()
    {
        var full = PlcMonitor.Agent.Contracts.Client.AgentSecurity.NewToken();
        var reader = PlcMonitor.Agent.Contracts.Client.AgentSecurity.NewToken();
        await using var agent = new AgentFactory();
        agent.Settings["Agent:Security:TokenSha256"] = PlcMonitor.Agent.Contracts.Client.AgentSecurity.HashToken(full);
        agent.Settings["Agent:Security:ReadOnlyTokenSha256"] = PlcMonitor.Agent.Contracts.Client.AgentSecurity.HashToken(reader);
        await using var vm = new MainViewModel(new TokenConnector(agent), new ImmediateDispatcher(), _dialogs, new AppSettings(), _settingsPath, _profilesPath);
        vm.SelectedProfile!.Token = reader;
        await vm.ConnectCommand.ExecuteAsync(null);
        await vm.OpenOpcUaCommand.ExecuteAsync(null);
        var panel = vm.OpcUaPanel!;

        panel.Enabled = true;

        panel.CanEdit.ShouldBeFalse();
        panel.SaveCommand.CanExecute(null).ShouldBeFalse();
        panel.StateText.ShouldBe("Parado");
    }

    public void Dispose()
    {
        File.Delete(_settingsPath);
        File.Delete(_profilesPath);
    }

    /// <summary>Conexión con el token del perfil (sin TLS: el agente en memoria).</summary>
    private sealed class TokenConnector(AgentFactory agent) : IAgentConnector
    {
        public (AgentService.AgentServiceClient Client, IDisposable Channel) Connect(PlcMonitor.Agent.Contracts.Client.AgentEndpoint endpoint)
        {
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
            return (new AgentService.AgentServiceClient(channel), channel);
        }
    }
}
