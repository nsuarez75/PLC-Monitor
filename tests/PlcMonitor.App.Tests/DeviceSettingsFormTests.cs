using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.VisualTree;
using PlcMonitor.Abstractions;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.Agent.Tests;
using PlcMonitor.App.Services;
using PlcMonitor.App.ViewModels;
using PlcMonitor.App.Views;
using PlcMonitor.Drivers.Modbus;
using PlcMonitor.Drivers.S7Absolute;
using PlcMonitor.Drivers.S7Symbolic;
using static PlcMonitor.Agent.Tests.AgentFactory;

namespace PlcMonitor.App.Tests;

/// <summary>Formularios de ajustes de los drivers en el editor de dispositivos (en vez del JSON).</summary>
public sealed class DeviceSettingsFormTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"plcmon-app-{Guid.NewGuid():N}.json");
    private readonly string _profilesPath = Path.Combine(Path.GetTempPath(), $"plcmon-agents-{Guid.NewGuid():N}.json");
    private readonly LockingDispatcher _ui = new();

    /// <summary>Los drivers tal como los anuncia el agente (ajustes por defecto reales).</summary>
    private static Driver[] RealDrivers() =>
    [
        Info(new S7SymbolicDriverFactory()),
        Info(new S7AbsoluteDriverFactory()),
        Info(new ModbusDriverFactory()),
        new Driver { Id = "otro", DefaultSettingsJson = """{"x":1}""" },
    ];

    private static Driver Info(IDriverFactory f) => new() { Id = f.TypeId, Name = f.DisplayName, DefaultSettingsJson = f.DefaultSettings().GetRawText() };

    private static Task<IReadOnlyList<string>?> Ports(params string[] ports) => Task.FromResult<IReadOnlyList<string>?>(ports);

    public void Dispose()
    {
        File.Delete(_settingsPath);
        File.Delete(_profilesPath);
    }

    [Fact]
    public void Modbus_form_round_trips_the_driver_settings()
    {
        var form = (ModbusSettingsForm)DriverSettingsForm.For("modbus", () => Ports())!;
        form.Load(new ModbusDriverFactory().DefaultSettings().GetRawText());
        (form.Transport.Value, form.Port, form.UnitId, form.BaudRate, form.ByteOrder.Value).ShouldBe(("Tcp", 502, 1, 9600, "ABCD"));
        form.IsNetwork.ShouldBeTrue();

        form.Transport = ModbusSettingsForm.TransportOptions.Single(o => o.Value == "Rtu");
        form.SerialPort = "/dev/ttyUSB0";
        form.BaudRate = 19200;
        form.Parity = ModbusSettingsForm.ParityOptions.Single(o => o.Value == "Even");
        form.UnitId = 3;
        form.OneBasedAddresses = true;
        form.ByteOrder = ModbusSettingsForm.ByteOrderOptions.Single(o => o.Value == "CDAB");

        var json = JsonDocument.Parse(form.ToJson()).RootElement;
        var settings = json.Deserialize<ModbusSettings>(DriverJson.Options)!;
        (settings.Transport, settings.SerialPort, settings.BaudRate, settings.Parity, settings.UnitId, settings.OneBasedAddresses, settings.ByteOrder)
            .ShouldBe((ModbusTransport.Rtu, "/dev/ttyUSB0", 19200, ModbusParity.Even, 3, true, ModbusByteOrder.CDAB));
        new ModbusDriverFactory().ValidateSettings(json).ShouldBeEmpty();
        form.IsSerial.ShouldBeTrue();
    }

    [Fact]
    public void Siemens_forms_round_trip_their_settings()
    {
        var symbolic = (S7SymbolicSettingsForm)DriverSettingsForm.For("s7-symbolic", () => Ports())!;
        symbolic.Load("""{"host":"10.0.0.1","password":"********","security":"Legacy","timeoutMs":5000}""");
        (symbolic.Host, symbolic.Password, symbolic.Security.Value, symbolic.TimeoutMs).ShouldBe(("10.0.0.1", "********", "Legacy", 5000));
        symbolic.Username = "operador";
        var s = JsonDocument.Parse(symbolic.ToJson()).RootElement.Deserialize<S7SymbolicSettings>(DriverJson.Options)!;
        (s.Host, s.Password, s.Username, s.Security).ShouldBe(("10.0.0.1", "********", "operador", S7SymbolicSecurity.Legacy));

        var absolute = (S7AbsoluteSettingsForm)DriverSettingsForm.For("s7-absolute", () => Ports())!;
        absolute.Load(new S7AbsoluteDriverFactory().DefaultSettings().GetRawText());
        absolute.Host = "192.168.0.10";
        absolute.Cpu = S7AbsoluteSettingsForm.CpuOptions.Single(o => o.Value == "S7300");
        absolute.Slot.ShouldBe(2); // al elegir S7-300 se propone su slot
        var a = JsonDocument.Parse(absolute.ToJson()).RootElement;
        a.Deserialize<S7AbsoluteSettings>(DriverJson.Options)!.Cpu.ShouldBe(S7CpuFamily.S7300);
        new S7AbsoluteDriverFactory().ValidateSettings(a).ShouldBeEmpty();

        DriverSettingsForm.For("otro", () => Ports()).ShouldBeNull();
    }

    [Fact]
    public void Unknown_properties_and_other_casing_are_kept()
    {
        var form = (ModbusSettingsForm)DriverSettingsForm.For("modbus", () => Ports())!;
        form.Load("""{"Host":"plc","futuro":{"a":1},"UNITID":7}""");
        form.Host.ShouldBe("plc");
        form.UnitId.ShouldBe(7);

        var json = JsonNode.Parse(form.ToJson())!.AsObject();

        json["futuro"]!["a"]!.GetValue<int>().ShouldBe(1);
        json["host"]!.GetValue<string>().ShouldBe("plc");
        json.ContainsKey("Host").ShouldBeFalse();
        json.ContainsKey("UNITID").ShouldBeFalse();
        json["unitId"]!.GetValue<int>().ShouldBe(7);
    }

    [Fact]
    public async Task Serial_ports_come_from_the_agent()
    {
        var form = (ModbusSettingsForm)DriverSettingsForm.For("modbus", () => Ports("/dev/serial/by-id/usb-FTDI-if00", "/dev/ttyUSB0"))!;
        await form.RefreshPortsAsync();
        form.SerialPorts.ShouldBe(["/dev/serial/by-id/usb-FTDI-if00", "/dev/ttyUSB0"]);
        form.SerialPort.ShouldBe("/dev/serial/by-id/usb-FTDI-if00");
        form.PortsHint.ShouldContain("2 puerto(s)");

        var none = (ModbusSettingsForm)DriverSettingsForm.For("modbus", () => Ports())!;
        await none.RefreshPortsAsync();
        none.PortsHint.ShouldContain("escríbelo a mano");
        var failed = (ModbusSettingsForm)DriverSettingsForm.For("modbus", () => Task.FromResult<IReadOnlyList<string>?>(null))!;
        await failed.RefreshPortsAsync();
        failed.PortsHint.ShouldContain("No se pudo preguntar");
    }

    [Fact]
    public void Editor_switches_between_form_and_json()
    {
        var vm = MainWindowTests.ViewModelWithData();
        var editor = new DeviceEditorViewModel(vm, RealDrivers(), _ => { });
        editor.DriverId = "modbus";
        var form = editor.Form.ShouldBeOfType<ModbusSettingsForm>();
        (editor.IsFormVisible, editor.IsJsonVisible).ShouldBe((true, false));

        form.Host = "10.1.1.1";
        editor.ShowJson = true;
        editor.SettingsJson.ShouldContain("\"host\": \"10.1.1.1\"");
        (editor.IsFormVisible, editor.IsJsonVisible).ShouldBe((false, true));

        editor.SettingsJson = editor.SettingsJson.Replace("10.1.1.1", "10.2.2.2", StringComparison.Ordinal);
        editor.ShowJson = false;
        form.Host.ShouldBe("10.2.2.2");

        editor.ShowJson = true;
        editor.SettingsJson = "{ roto";
        editor.ShowJson = false;
        editor.ShowJson.ShouldBeTrue(); // se queda en el JSON hasta corregirlo
        editor.Errors.ShouldStartWith("El JSON no es válido");

        editor.DriverId = "otro";
        editor.Form.ShouldBeNull();
        (editor.HasForm, editor.IsJsonVisible).ShouldBe((false, true));
        editor.SettingsJson.ShouldContain("\"x\": 1");
    }

    [Fact]
    public async Task A_modbus_device_is_created_with_the_form()
    {
        await using var agent = new AgentFactory();
        agent.ExtraDrivers.Add(new ModbusDriverFactory());
        var vm = new MainViewModel(new InMemoryConnector(agent), _ui, new FakeDialogs(), new AppSettings(), _settingsPath, _profilesPath);
        await vm.ConnectCommand.ExecuteAsync(null);
        vm.IsConnected.ShouldBeTrue(vm.Message);
        string? saved = null;
        var editor = new DeviceEditorViewModel(vm, vm.Drivers, n => saved = n) { Name = "variador", DriverId = "modbus" };
        var form = editor.Form.ShouldBeOfType<ModbusSettingsForm>();

        await editor.SaveCommand.ExecuteAsync(null);
        editor.Errors.ShouldContain("Host obligatorio");

        form.Host = "127.0.0.1";
        form.UnitId = 9;
        await editor.SaveCommand.ExecuteAsync(null);

        editor.Errors.ShouldBeEmpty();
        saved.ShouldBe("variador");
        var config = await vm.Client!.GetDeviceConfigAsync(new DeviceRequest { Device = "variador" });
        config.Driver.ShouldBe("modbus");
        var settings = JsonDocument.Parse(config.SettingsJson).RootElement.Deserialize<ModbusSettings>(DriverJson.Options)!;
        (settings.Host, settings.UnitId, settings.Port).ShouldBe(("127.0.0.1", 9, 502));

        // Al editarlo, el formulario muestra lo guardado.
        var edit = new DeviceEditorViewModel(vm, vm.Drivers, config, _ => { });
        var loaded = edit.Form.ShouldBeOfType<ModbusSettingsForm>();
        (loaded.Host, loaded.UnitId).ShouldBe(("127.0.0.1", 9));
        await vm.DisposeAsync();
    }

    [Fact]
    public Task Settings_forms_render() => HeadlessApp.RunAsync(async () =>
    {
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var vm = MainWindowTests.ViewModelWithData();
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 1000 };
        window.Show();

        vm.Editor = new DeviceEditorViewModel(vm, RealDrivers(), _ => { }) { Name = "variador", DriverId = "modbus" };
        var modbus = (ModbusSettingsForm)vm.Editor.Form!;
        modbus.Host = "192.168.0.20";
        window.UpdateLayout();
        Texts(window).ShouldContain("Unit id (esclavo)");
        window.GetVisualDescendants().OfType<AutoCompleteBox>().Single().IsEffectivelyVisible.ShouldBeFalse();
        MainWindowTests.Capture(window, "device-editor-modbus.png");

        modbus.Transport = ModbusSettingsForm.TransportOptions.Single(o => o.Value == "Rtu");
        await modbus.RefreshPortsAsync();
        modbus.SerialPorts.Add("/dev/ttyUSB0");
        modbus.SerialPort = "/dev/ttyUSB0";
        modbus.PortsHint = "2 puerto(s) en el equipo del colector.";
        window.UpdateLayout();
        window.GetVisualDescendants().OfType<AutoCompleteBox>().Single().IsEffectivelyVisible.ShouldBeTrue();
        Texts(window).ShouldContain("Línea serie");
        MainWindowTests.Capture(window, "device-editor-modbus-rtu.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        MainWindowTests.Capture(window, "device-editor-modbus-rtu-dark.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        window.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true;
        window.UpdateLayout();
        Texts(window).ShouldContain("Hueco máximo");
        MainWindowTests.Capture(window, "device-editor-modbus-advanced.png");

        vm.Editor.DriverId = "s7-absolute";
        window.UpdateLayout();
        Texts(window).ShouldContain("Rack y slot");
        MainWindowTests.Capture(window, "device-editor-absolute.png");

        vm.Editor.DriverId = "s7-symbolic";
        window.UpdateLayout();
        Texts(window).ShouldContain("Seguridad");
        MainWindowTests.Capture(window, "device-editor-symbolic.png");

        vm.Editor.ShowJson = true;
        window.UpdateLayout();
        Texts(window).ShouldContain("JSON del driver");
        MainWindowTests.Capture(window, "device-editor-json.png");
    });

    private static List<string?> Texts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
}
