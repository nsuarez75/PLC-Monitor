using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Google.Protobuf.WellKnownTypes;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Services;
using PlcMonitor.App.ViewModels;
using PlcMonitor.App.Views;

namespace PlcMonitor.App.Tests;

/// <summary>La ventana principal se construye y enlaza con el ViewModel (sin agente: eventos sintéticos).</summary>
public class MainWindowTests
{
    private static MainViewModel ViewModelWithData()
    {
        var vm = new MainViewModel(new GrpcAgentConnector(), new ImmediateDispatcher(), new FakeDialogs(), new AppSettings(), Path.GetTempFileName(), Path.Combine(Path.GetTempPath(), $"plcmon-agents-{Guid.NewGuid():N}.json"));
        var now = Timestamp.FromDateTime(DateTime.UtcNow);
        var snapshot = new Snapshot();
        snapshot.Devices.Add(new Device { Name = "casa1200", Driver = "s7-symbolic", Enabled = true, WritesEnabled = true, State = ConnectionState.Connected, Since = now, TagCount = 3 });
        snapshot.Devices.Add(new Device { Name = "prensa2", Driver = "s7-absolute", Enabled = true, State = ConnectionState.Faulted, Detail = "TCP : Unreachable peer", Since = now, NextRetry = now });
        snapshot.Devices.Add(new Device { Name = "linea3", Driver = "s7-symbolic", Enabled = false, State = ConnectionState.Disconnected, Detail = "Dispositivo deshabilitado", Since = now });
        snapshot.Tags.Add(Tag("clock", "Maquinas_Transfer_DB.Maquina_9.Clock_1s", "true", DataType.Boolean, Quality.Good, now, false));
        snapshot.Tags.Add(Tag("peso", "Viruta_Datos.Viruta.Peso", "12.5", DataType.Float, Quality.Good, now, true));
        snapshot.Tags.Add(Tag("malo", "NoExiste_DB.x", "", DataType.Unknown, Quality.BadAddressInvalid, now, false, "El símbolo 'NoExiste_DB.x' no existe en la CPU."));
        vm.Apply(new SubscribeEvent { Snapshot = snapshot });
        vm.Message = "Conectado a http://127.0.0.1:5310";
        return vm;
    }

    private static Tag Tag(string name, string address, string text, DataType type, Quality quality, Timestamp time, bool writable, string error = "") => new()
    {
        Device = "casa1200",
        Name = name,
        Address = address,
        Group = "rapido",
        IntervalMs = 250,
        Writable = writable,
        Value = new TagValue { Text = text, DataType = type, Quality = quality, Timestamp = time, Error = error },
    };

    [Fact]
    public Task Main_window_shows_devices_and_tags() => HeadlessApp.RunAsync(() =>
    {
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var window = new MainWindow { DataContext = ViewModelWithData() };
        window.Show();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => t is not null).ToArray();
        texts.ShouldContain("casa1200");
        texts.ShouldContain("prensa2");
        texts.ShouldContain("Viruta_Datos.Viruta.Peso");
        texts.ShouldContain("12.5");
        texts.ShouldContain("El símbolo 'NoExiste_DB.x' no existe en la CPU.");
        Capture(window, "main-window.png");
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Capture(window, "main-window-dark.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
    });

    [Fact]
    public Task Device_editor_covers_the_content() => HeadlessApp.RunAsync(() =>
    {
        var vm = ViewModelWithData();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        var drivers = new[] { new Driver { Id = "s7-symbolic", DefaultSettingsJson = """{"host":"","security":"Auto"}""" }, new Driver { Id = "s7-absolute", DefaultSettingsJson = """{"host":""}""" } };

        vm.Editor = new DeviceEditorViewModel(vm, drivers, _ => { });
        window.UpdateLayout();

        window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Nuevo dispositivo").IsEffectivelyVisible.ShouldBeTrue();
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("Nuevo dispositivo");
        vm.Editor.SettingsJson.ShouldContain("security");
        Capture(window, "device-editor.png");
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Capture(window, "device-editor-dark.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
    });

    [Fact]
    public Task Enter_in_the_browse_box_runs_the_search() => HeadlessApp.RunAsync(() =>
    {
        var vm = ViewModelWithData();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
        window.UpdateLayout();
        var box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText?.StartsWith("Buscar", StringComparison.Ordinal) == true);
        box.Focus();

        // Antes lanzaba ArgumentException ("False" como texto a un comando de bool) y cerraba la app.
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);

        vm.Browse.SearchCommand.CanExecute(null).ShouldBeTrue();
    });

    [Fact]
    public Task Agents_panel_shows_profiles_and_activity() => HeadlessApp.RunAsync(() =>
    {
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var vm = ViewModelWithData();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.OpenAgentsCommand.Execute(null);
        var agents = vm.Agents!;
        agents.NewCommand.Execute(null);
        agents.Name = "Línea 3";
        agents.Url = "https://192.168.0.20:5311";
        agents.Thumbprint = "9F2C4A7E1B3D5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8";
        agents.Token = "token-de-ejemplo";
        agents.SshHost = "192.168.0.20";
        agents.SshUser = "tecnico";
        agents.Log.Add("Instalando: conectando por SSH con tecnico@192.168.0.20:22…");
        agents.Log.Add("Equipo: Linux x86_64 (paquete linux-x64)");
        agents.Log.Add("Subiendo plcmon-agent-1.0.0-linux-x64.tar.gz (49 MB)…");
        agents.Log.Add("Agente en marcha: https://192.168.0.20:5311");
        agents.Log.Add("✔ Hecho");
        window.UpdateLayout();

        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("EQUIPO (SSH)");
        Capture(window, "agents.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Capture(window, "agents-dark.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
    });

    /// <summary>
    /// Integración: la ventana real conectada a un agente real (PLCMON_IT_AGENT_URL). Se omite si no está definida.
    /// Con PLCMON_UI_CAPTURE_DIR guarda la captura.
    /// </summary>
    [Fact]
    public async Task Main_window_against_a_real_agent()
    {
        var url = Environment.GetEnvironmentVariable("PLCMON_IT_AGENT_URL");
        Assert.SkipWhen(string.IsNullOrEmpty(url), "PLCMON_IT_AGENT_URL no definida");
        await HeadlessApp.RunAsync(async () =>
        {
            var vm = new MainViewModel(new GrpcAgentConnector(), new AvaloniaDispatcher(), new FakeDialogs(),
                new AppSettings { AgentUrl = url! }, Path.GetTempFileName(), Path.Combine(Path.GetTempPath(), $"plcmon-agents-{Guid.NewGuid():N}.json"));
            var window = new MainWindow { DataContext = vm };
            window.Show();

            await vm.ConnectCommand.ExecuteAsync(null);
            vm.IsConnected.ShouldBeTrue(vm.Message);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!(vm.IsLive && vm.SelectedDevice?.Tags.Count > 0 && vm.SelectedDevice.Tags.All(t => t.IsGood)) && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            vm.SelectedDevice.ShouldNotBeNull();
            Capture(window, "real-agent.png");
            await vm.DisposeAsync();
        });
    }

    /// <summary>Si PLCMON_UI_CAPTURE_DIR está definida, guarda una captura para revisarla.</summary>
    private static void Capture(Window window, string file)
    {
        if (Environment.GetEnvironmentVariable("PLCMON_UI_CAPTURE_DIR") is not { Length: > 0 } dir)
            return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        using var stream = File.Create(Path.Combine(dir, file));
        frame?.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
