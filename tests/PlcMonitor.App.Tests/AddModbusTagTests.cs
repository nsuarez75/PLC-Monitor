using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FluentModbus;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.Agent.Tests;
using PlcMonitor.App.Services;
using PlcMonitor.App.ViewModels;
using PlcMonitor.App.Views;
using PlcMonitor.Drivers.Modbus;
using PlcMonitor.Engine.Configuration;
using DriverJson = PlcMonitor.Abstractions.DriverJson;

namespace PlcMonitor.App.Tests;

/// <summary>Alta de tags Modbus con los desplegables (y por rangos) contra el agente en memoria y un esclavo Modbus TCP simulado.</summary>
public sealed class AddModbusTagTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"plcmon-app-{Guid.NewGuid():N}.json");
    private readonly string _profilesPath = Path.Combine(Path.GetTempPath(), $"plcmon-agents-{Guid.NewGuid():N}.json");
    private readonly LockingDispatcher _ui = new();
    private readonly ModbusTcpServer _slave = new();
    private readonly int _port;

    public AddModbusTagTests()
    {
        _slave.AddUnit(1);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        _slave.Start(new IPEndPoint(IPAddress.Loopback, _port));
    }

    public void Dispose()
    {
        _slave.Dispose();
        File.Delete(_settingsPath);
        File.Delete(_profilesPath);
    }

    private AgentFactory Agent()
    {
        var agent = new AgentFactory(new DeviceConfiguration
        {
            Name = "variador",
            Driver = ModbusDriverFactory.Id,
            Settings = JsonSerializer.SerializeToElement(new ModbusSettings { Host = "127.0.0.1", Port = _port }, DriverJson.Options),
            Reconnect = new ReconnectSettings { InitialDelayMs = 100, MaxDelayMs = 200 },
        });
        agent.ExtraDrivers.Add(new ModbusDriverFactory());
        return agent;
    }

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
    public async Task The_address_is_composed_with_the_dropdowns()
    {
        await using var agent = Agent();
        await using var vm = await ConnectAsync(agent);
        var add = vm.AddTag;
        add.OpenCommand.Execute(null);

        add.IsModbus.ShouldBeTrue();
        add.Hint.ShouldStartWith("Modbus: HR0");
        vm.EmptyTagsHint.ShouldContain("HR0");
        add.Address.ShouldBe("HR0");
        add.ModbusNumber = 100;
        add.ModbusType = Pick(AddTagViewModel.ModbusTypes, "FLOAT");
        add.Address.ShouldBe("HR100:FLOAT");
        add.HasByteOrder.ShouldBeTrue();
        add.ModbusOrder = Pick(AddTagViewModel.ModbusOrders, "CDAB");
        add.Address.ShouldBe("HR100:FLOAT:CDAB");
        add.ModbusType = Pick(AddTagViewModel.ModbusTypes, "BIT");
        add.ModbusBit = 3;
        add.Address.ShouldBe("HR100.3");
        add.HasByteOrder.ShouldBeFalse();
        add.ModbusType = Pick(AddTagViewModel.ModbusTypes, "STRING");
        add.ModbusStringLength = 8;
        add.Address.ShouldBe("HR100:STRING[8]:CDAB");

        add.Writable = true;
        add.ModbusTable = Pick(AddTagViewModel.ModbusTables, "DI");
        add.Address.ShouldBe("DI100");
        (add.IsRegisterTable, add.CanBeWritable, add.Writable).ShouldBe((false, false, false));
        add.ModbusTable = Pick(AddTagViewModel.ModbusTables, "IR");
        add.ModbusType = Pick(AddTagViewModel.ModbusTypes, "UINT16");
        add.Address.ShouldBe("IR100:UINT16");
        add.CanBeWritable.ShouldBeFalse();
    }

    [Fact]
    public async Task A_range_is_tested_and_added_at_once()
    {
        for (var i = 0; i < 5; i++)
            _slave.GetHoldingRegisters(1).SetBigEndian(10 + (i * 2), 1.5f * i);
        await using var agent = Agent();
        await using var vm = await ConnectAsync(agent);
        await Wait.UntilAsync(() => _ui.Read(() => vm.SelectedDevice!.IsConnected), "conectado");
        var add = vm.AddTag;
        add.OpenCommand.Execute(null);
        add.ModbusNumber = 10;
        add.ModbusType = Pick(AddTagViewModel.ModbusTypes, "FLOAT");
        add.RangeCount = 5;
        add.Name = "nivel";

        add.IsRange.ShouldBeTrue();
        add.RangeAddresses().ShouldBe(["HR10:FLOAT", "HR12:FLOAT", "HR14:FLOAT", "HR16:FLOAT", "HR18:FLOAT"]);
        add.RangePreview.ShouldBe("5 tags: HR10:FLOAT … HR18:FLOAT");
        await add.TestCommand.ExecuteAsync(null);
        add.TestResult.ShouldBe("✔ 5 leídos (Float): 0 · 1.5 · 3 · 4.5 · 6");

        await add.AddCommand.ExecuteAsync(null);

        vm.Message.ShouldBe("Añadidos 5 tags (nivel_1 … nivel_5) a variador (grupo default, 1000 ms)");
        var config = await vm.Client!.GetDeviceConfigAsync(new DeviceRequest { Device = "variador" });
        config.ScanGroups.Single().Tags.Select(t => (t.Name, t.Address)).ShouldBe(
            [("nivel_1", "HR10:FLOAT"), ("nivel_2", "HR12:FLOAT"), ("nivel_3", "HR14:FLOAT"), ("nivel_4", "HR16:FLOAT"), ("nivel_5", "HR18:FLOAT")]);
        // Listo para seguir con el siguiente bloque.
        (add.ModbusNumber, add.RangeCount, add.Address).ShouldBe((20, 1, "HR20:FLOAT"));
        await Wait.UntilAsync(() => _ui.Read(() => vm.SelectedDevice!.Tags.Count == 5 && vm.SelectedDevice.Tags.All(t => t.Quality == Quality.Good)), "tags leídos");
    }

    [Fact]
    public async Task Bit_ranges_walk_through_the_registers_and_errors_are_reported()
    {
        await using var agent = Agent();
        await using var vm = await ConnectAsync(agent);
        await Wait.UntilAsync(() => _ui.Read(() => vm.SelectedDevice!.IsConnected), "conectado");
        var add = vm.AddTag;
        add.OpenCommand.Execute(null);
        add.ModbusType = Pick(AddTagViewModel.ModbusTypes, "BIT");
        add.ModbusNumber = 4;
        add.ModbusBit = 14;
        add.RangeCount = 4;
        add.RangeAddresses().ShouldBe(["HR4.14", "HR4.15", "HR5.0", "HR5.1"]);

        // Escribir a mano otra dirección anula el rango.
        add.Address = "HR70000";
        add.IsRange.ShouldBeFalse();
        await add.TestCommand.ExecuteAsync(null);
        add.TestIsError.ShouldBeTrue();
        add.TestResult.ShouldContain("entre 0 y 65535");
    }

    [Fact]
    public Task Modbus_add_tag_renders() => HeadlessApp.RunAsync(() =>
    {
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var vm = MainWindowTests.ViewModelWithData();
        vm.Apply(new SubscribeEvent
        {
            DeviceConfigured = new DeviceConfigured { Device = new Device { Name = "variador", Driver = "modbus", Enabled = true, State = ConnectionState.Connected, Since = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow) } },
        });
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.SelectedDevice = vm.Devices.Single(d => d.Name == "variador");
        var add = vm.AddTag;
        add.OpenCommand.Execute(null);
        add.ModbusNumber = 10;
        add.ModbusType = Pick(AddTagViewModel.ModbusTypes, "FLOAT");
        add.ModbusOrder = Pick(AddTagViewModel.ModbusOrders, "CDAB");
        add.RangeCount = 8;
        add.Name = "nivel";
        add.TestResult = "✔ 8 leídos (Float): 0 · 1.5 · 3 · 4.5 · 6 · 7.5 …";
        window.UpdateLayout();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        texts.ShouldContain("8 tags: HR10:FLOAT:CDAB … HR24:FLOAT:CDAB");
        MainWindowTests.Capture(window, "add-tag-modbus.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        MainWindowTests.Capture(window, "add-tag-modbus-dark.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
    });

    private static SettingOption Pick(IReadOnlyList<SettingOption> options, string value) => options.Single(o => o.Value == value);
}
