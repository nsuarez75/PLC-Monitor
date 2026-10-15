using PlcMonitor.Agent.Contracts;
using PlcMonitor.Agent.Tests;
using PlcMonitor.App.Services;
using PlcMonitor.App.ViewModels;
using static PlcMonitor.Agent.Tests.AgentFactory;
using Tag = PlcMonitor.Engine.Configuration.TagConfiguration;

namespace PlcMonitor.App.Tests;

/// <summary>ViewModels de la app contra el agente en memoria con driver simulado.</summary>
public sealed class MainViewModelTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"plcmon-app-{Guid.NewGuid():N}.json");
    private readonly LockingDispatcher _ui = new();
    private readonly FakeDialogs _dialogs = new();

    public MainViewModelTests()
    {
        MainViewModel.ResubscribeDelay = TimeSpan.FromMilliseconds(100);
    }

    private static Tag T(string address, string? name = null, bool writable = false) => new() { Address = address, Name = name, Writable = writable };

    private MainViewModel Create(AgentFactory agent, AppSettings? settings = null) =>
        new(new InMemoryConnector(agent), _ui, _dialogs, settings ?? new AppSettings(), _settingsPath);

    private async Task<MainViewModel> ConnectAsync(AgentFactory agent)
    {
        var vm = Create(agent);
        await vm.ConnectCommand.ExecuteAsync(null);
        vm.IsConnected.ShouldBeTrue(vm.Message);
        await Wait.UntilAsync(() => _ui.Read(() => vm.IsLive), "suscripción en vivo");
        return vm;
    }

    private TagViewModel TagOf(MainViewModel vm, string device, string tag) =>
        _ui.Read(() => vm.Devices.Single(d => d.Name == device).Tags.Single(t => t.Name == tag));

    [Fact]
    public async Task Connecting_shows_devices_and_live_values()
    {
        await using var agent = new AgentFactory(Device("plc1", tags: [T("DB1.DBW0", "velocidad")]), Device("plc2", enabled: false));
        var plc = agent.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 10;
        await using var vm = await ConnectAsync(agent);

        _ui.Read(() => vm.Devices.Select(d => d.Name).ToArray()).ShouldBe(["plc1", "plc2"]);
        _ui.Read(() => vm.SelectedDevice?.Name).ShouldBe("plc1");
        vm.Drivers.ShouldHaveSingleItem().Id.ShouldBe("fake");
        vm.AgentDescription.ShouldContain(Environment.MachineName);
        await Wait.UntilAsync(() => _ui.Read(() => TagOf(vm, "plc1", "velocidad").ValueText) == "10", "valor inicial");

        plc.Values["DB1.DBW0"] = 11;
        await Wait.UntilAsync(() => _ui.Read(() => TagOf(vm, "plc1", "velocidad").ValueText) == "11", "cambio en vivo");
        var tag = TagOf(vm, "plc1", "velocidad");
        _ui.Read(() => (tag.IsGood, tag.DataType, tag.Group)).ShouldBe((true, "Int32", "g (50 ms)"));
        _ui.Read(() => vm.Devices[0].IsConnected).ShouldBeTrue();
        _ui.Read(() => vm.Devices[1].StateText).ShouldBe("Desactivado");
    }

    [Fact]
    public async Task Successful_connection_is_remembered()
    {
        await using var agent = new AgentFactory();
        var settings = new AppSettings();
        await using var vm = Create(agent, settings);
        vm.AgentUrl = "http://pc-linea:5310";

        await vm.ConnectCommand.ExecuteAsync(null);

        AppSettings.Load(_settingsPath).AgentUrl.ShouldBe("http://pc-linea:5310");
        vm.RecentAgents.ShouldContain("http://pc-linea:5310");
    }

    [Fact]
    public async Task Unreachable_agent_reports_an_error()
    {
        var settings = new AppSettings { AgentUrl = "http://127.0.0.1:1" };
        await using var vm = new MainViewModel(new GrpcAgentConnector(), _ui, _dialogs, settings, _settingsPath);

        await vm.ConnectCommand.ExecuteAsync(null);

        vm.IsConnected.ShouldBeFalse();
        vm.IsMessageError.ShouldBeTrue();
        vm.Message.ShouldContain("No se puede conectar con el agente en http://127.0.0.1:1");
        File.Exists(_settingsPath).ShouldBeFalse();
    }

    [Fact]
    public async Task Write_asks_for_confirmation_and_goes_through_the_agent()
    {
        await using var agent = new AgentFactory(Device("plc1", writesEnabled: true, tags: [T("DB1.DBW2", "consigna", writable: true)]));
        var plc = agent.Driver.Plc("plc1");
        plc.Values["DB1.DBW2"] = 0;
        await using var vm = await ConnectAsync(agent);
        var tag = TagOf(vm, "plc1", "consigna");
        await Wait.UntilAsync(() => _ui.Read(() => tag.IsGood));

        tag.NewValue = "42";
        _dialogs.Answer = false;
        await tag.WriteCommand.ExecuteAsync(null);
        plc.Writes.ShouldBeEmpty();

        _dialogs.Answer = true;
        await tag.WriteCommand.ExecuteAsync(null);

        _dialogs.Questions.Count.ShouldBe(2);
        _dialogs.Questions[1].ShouldContain("«42» en plc1/consigna");
        plc.Writes.ShouldHaveSingleItem().Value.ShouldBe(42);
        tag.NewValue.ShouldBeEmpty();
        await Wait.UntilAsync(() => _ui.Read(() => tag.ValueText) == "42", "valor escrito");
    }

    [Fact]
    public async Task Write_is_only_possible_on_writable_tags()
    {
        await using var agent = new AgentFactory(Device("plc1", tags: [T("DB1.DBW0", "ro")]));
        await using var vm = await ConnectAsync(agent);
        var tag = TagOf(vm, "plc1", "ro");

        tag.NewValue = "1";

        tag.WriteCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Device_can_be_toggled()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false));
        await using var vm = await ConnectAsync(agent);
        var device = _ui.Read(() => vm.Devices.Single());

        await device.ToggleEnabledCommand.ExecuteAsync(null);

        await Wait.UntilAsync(() => _ui.Read(() => device.IsConnected), "conectado tras activar");
        _ui.Read(() => device.ToggleText).ShouldBe("Desactivar");
        (await ConfigurationFileLoad(agent)).Devices[0].Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Browse_and_add_a_tag()
    {
        await using var agent = new AgentFactory(Device("plc1"));
        var plc = agent.Driver.Plc("plc1");
        plc.Values["DB1.Motor"] = 5;
        plc.Values["DB1.Bomba"] = 6;
        await using var vm = await ConnectAsync(agent);
        await Wait.UntilAsync(() => _ui.Read(() => vm.Devices[0].IsConnected));

        vm.Browse.Filter = "motor";
        await vm.Browse.SearchCommand.ExecuteAsync(null);
        vm.Browse.Results.ShouldHaveSingleItem().Address.ShouldBe("DB1.Motor");
        vm.Browse.Summary.ShouldContain("1 mostrados de 1 coincidencias · 2 símbolos");

        vm.Browse.GroupName = "rapido";
        vm.Browse.IntervalMs = 200;
        await vm.Browse.AddCommand.ExecuteAsync(vm.Browse.Results[0]);

        await Wait.UntilAsync(() => _ui.Read(() => vm.Devices[0].Tags.Any(t => t.Name == "DB1.Motor" && t.ValueText == "5")), "tag añadido en vivo");
        TagOf(vm, "plc1", "DB1.Motor").Group.ShouldBe("rapido (200 ms)");
        var saved = await ConfigurationFileLoad(agent);
        saved.Devices[0].ScanGroups.Single(g => g.Name == "rapido").Tags.ShouldHaveSingleItem().Address.ShouldBe("DB1.Motor");

        await vm.Browse.AddCommand.ExecuteAsync(vm.Browse.Results[0]);
        vm.Message.ShouldContain("ya es un tag");
    }

    [Fact]
    public async Task New_device_is_created_from_the_editor_and_selected()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false));
        await using var vm = await ConnectAsync(agent);

        vm.NewDeviceCommand.Execute(null);
        var editor = vm.Editor.ShouldNotBeNull();
        editor.IsNew.ShouldBeTrue();
        editor.DriverId.ShouldBe("fake");
        editor.SettingsJson.ShouldContain("\"host\"");
        editor.Name = "prensa";
        editor.SettingsJson = """{ "host": "10.9.9.9" }""";
        editor.Enabled = false;
        await editor.SaveCommand.ExecuteAsync(null);

        vm.Editor.ShouldBeNull();
        await Wait.UntilAsync(() => _ui.Read(() => vm.SelectedDevice?.Name) == "prensa", "dispositivo nuevo seleccionado");
        (await ConfigurationFileLoad(agent)).Devices.Select(d => d.Name).ShouldBe(["plc1", "prensa"]);
    }

    [Fact]
    public async Task Editor_shows_validation_errors_and_keeps_tags_when_renaming()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false, tags: [T("DB1.DBW0", "a"), T("DB1.DBW2", "b")]));
        await using var vm = await ConnectAsync(agent);

        await vm.EditDeviceCommand.ExecuteAsync(null);
        var editor = vm.Editor.ShouldNotBeNull();
        editor.TagSummary.ShouldBe("2 tags en 1 grupos (se conservan)");
        editor.SettingsJson = """{ "host": "" }""";
        await editor.SaveCommand.ExecuteAsync(null);
        editor.Errors.ShouldContain("Falta host");
        vm.Editor.ShouldBe(editor);

        editor.SettingsJson = """{ "host": "10.0.0.2" }""";
        editor.Name = "linea1";
        await editor.SaveCommand.ExecuteAsync(null);

        vm.Editor.ShouldBeNull();
        await Wait.UntilAsync(() => _ui.Read(() => vm.Devices.Select(d => d.Name).SequenceEqual(["linea1"])), "renombrado");
        _ui.Read(() => vm.Devices[0].Tags.Select(t => t.Name).ToArray()).ShouldBe(["a", "b"]);
        _ui.Read(() => vm.SelectedDevice?.Name).ShouldBe("linea1");
    }

    [Fact]
    public async Task Device_and_tag_can_be_removed_after_confirmation()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false, tags: [T("DB1.DBW0", "a"), T("DB1.DBW2", "b")]), Device("plc2", enabled: false));
        await using var vm = await ConnectAsync(agent);

        await TagOf(vm, "plc1", "a").RemoveCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => _ui.Read(() => vm.Devices[0].Tags.Count) == 1, "tag quitado");

        _ui.Post(() => vm.SelectedDevice = vm.Devices.Single(d => d.Name == "plc2"));
        await vm.DeleteDeviceCommand.ExecuteAsync(null);

        await Wait.UntilAsync(() => _ui.Read(() => vm.Devices.Count) == 1, "dispositivo eliminado");
        _ui.Read(() => vm.SelectedDevice?.Name).ShouldBe("plc1");
        _dialogs.Questions.Count.ShouldBe(2);
        (await ConfigurationFileLoad(agent)).Devices.Select(d => d.Name).ShouldBe(["plc1"]);
    }

    [Fact]
    public async Task Disconnect_clears_the_model()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false));
        await using var vm = await ConnectAsync(agent);

        await vm.DisconnectCommand.ExecuteAsync(null);

        vm.IsConnected.ShouldBeFalse();
        vm.Devices.ShouldBeEmpty();
        vm.ConnectionText.ShouldBe("Sin conexión");
        vm.ConnectCommand.CanExecute(null).ShouldBeTrue();
    }

    private static Task<Engine.Configuration.AgentConfiguration> ConfigurationFileLoad(AgentFactory agent) =>
        Engine.Configuration.ConfigurationFile.LoadAsync(agent.ConfigPath);

    public void Dispose() => File.Delete(_settingsPath);
}
