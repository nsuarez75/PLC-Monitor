using PlcMonitor.Agent.Contracts;
using PlcMonitor.Agent.Tests;
using PlcMonitor.App.Services;
using PlcMonitor.App.ViewModels;
using static PlcMonitor.Agent.Tests.AgentFactory;

namespace PlcMonitor.App.Tests;

/// <summary>Alta de tags escribiendo su dirección (la forma de hacerlo con el driver absoluto, sin explorador).</summary>
public sealed class AddTagTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"plcmon-app-{Guid.NewGuid():N}.json");
    private readonly string _profilesPath = Path.Combine(Path.GetTempPath(), $"plcmon-agents-{Guid.NewGuid():N}.json");
    private readonly LockingDispatcher _ui = new();

    private async Task<MainViewModel> ConnectAsync(AgentFactory agent)
    {
        var vm = new MainViewModel(new InMemoryConnector(agent), _ui, new FakeDialogs(), new AppSettings(), _settingsPath, _profilesPath);
        await vm.ConnectCommand.ExecuteAsync(null);
        vm.IsConnected.ShouldBeTrue(vm.Message);
        await Wait.UntilAsync(() => _ui.Read(() => vm.Devices.Count > 0 && vm.IsLive), "dispositivos");
        _ui.Read(() => vm.SelectedDevice = vm.Devices[0]);
        return vm;
    }

    [Fact]
    public async Task A_tag_is_tested_and_added_by_its_address()
    {
        await using var agent = new AgentFactory(Device("plc1"));
        agent.Driver.Plc("plc1").Values["DB1.DBW10"] = 12;
        await using var vm = await ConnectAsync(agent);
        await Wait.UntilAsync(() => _ui.Read(() => vm.SelectedDevice!.IsConnected), "conectado");
        var add = vm.AddTag;

        add.OpenCommand.Execute(null);
        add.IsOpen.ShouldBeTrue();
        add.AddCommand.CanExecute(null).ShouldBeFalse();
        add.Address = "DB1.DBW10";
        add.Name = "consigna";
        add.GroupName = "lento";
        add.IntervalMs = 2000;
        add.Writable = true;
        await add.TestCommand.ExecuteAsync(null);
        add.TestResult.ShouldBe("✔ 12 (Int32)");
        add.TestIsError.ShouldBeFalse();

        await add.AddCommand.ExecuteAsync(null);

        var config = await vm.Client!.GetDeviceConfigAsync(new DeviceRequest { Device = "plc1" });
        var group = config.ScanGroups.Single(g => g.Name == "lento");
        group.IntervalMs.ShouldBe(2000);
        var tag = group.Tags.ShouldHaveSingleItem();
        (tag.Name, tag.Address, tag.Writable, tag.OpcUaHidden).ShouldBe(("consigna", "DB1.DBW10", true, false));
        (add.Address, add.Name, add.TestResult, add.GroupName).ShouldBe(("", "", "", "lento"));
        vm.Message.ShouldBe("Añadido consigna a plc1 (grupo lento, 2000 ms)");
        await Wait.UntilAsync(() => _ui.Read(() => vm.SelectedDevice!.Tags.Any(t => t.Name == "consigna")), "tag nuevo en la lista");

        add.Address = "DB1.DBW10";
        add.Name = "consigna";
        await add.AddCommand.ExecuteAsync(null);
        vm.Message.ShouldBe("consigna ya es un tag de plc1");
        vm.IsMessageError.ShouldBeTrue();
    }

    [Fact]
    public async Task A_wrong_address_is_reported_when_tested()
    {
        await using var agent = new AgentFactory(Device("plc1"));
        await using var vm = await ConnectAsync(agent);
        await Wait.UntilAsync(() => _ui.Read(() => vm.SelectedDevice!.IsConnected), "conectado");

        vm.AddTag.Address = "DB9.DBW0"; // el PLC simulado no la tiene
        await vm.AddTag.TestCommand.ExecuteAsync(null);

        vm.AddTag.TestIsError.ShouldBeTrue();
        vm.AddTag.TestResult.ShouldBe("✖ no existe"); // el error tal como lo da el driver
    }

    [Fact]
    public async Task Explore_is_offered_only_when_the_driver_can_browse()
    {
        await using var agent = new AgentFactory(Device("plc1"));
        await using var vm = await ConnectAsync(agent);

        vm.SelectedDeviceCanBrowse.ShouldBeTrue(); // el driver simulado explora, como el simbólico
        vm.EmptyTagsHint.ShouldContain("«Explorar»");
    }

    [Fact]
    public void The_hint_shows_absolute_addresses_for_the_absolute_driver()
    {
        var context = new FakeContext();
        var device = new DeviceViewModel(context, new Device { Name = "prensa", Driver = AddTagViewModel.AbsoluteDriver });
        var add = new AddTagViewModel(context, () => device, id => new Driver { Id = id ?? "", CanBrowse = false });

        add.Hint.ShouldStartWith("Dirección absoluta: DB1.DBX0.3 (bit) · DB1.DBW10 (Word)");
    }

    public void Dispose()
    {
        File.Delete(_settingsPath);
        File.Delete(_profilesPath);
    }

    private sealed class FakeContext : IAgentContext
    {
        public AgentService.AgentServiceClient? Client => null;

        public IDialogs Dialogs { get; } = new FakeDialogs();

        public void Report(string message, bool isError = false)
        {
        }

        public string Describe(Exception exception) => exception.Message;
    }
}
