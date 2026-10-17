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
    internal static MainViewModel ViewModelWithData(string? settingsPath = null)
    {
        var vm = new MainViewModel(new GrpcAgentConnector(), new ImmediateDispatcher(), new FakeDialogs(), new AppSettings(), settingsPath ?? Path.GetTempFileName(), Path.Combine(Path.GetTempPath(), $"plcmon-agents-{Guid.NewGuid():N}.json"));
        var now = Timestamp.FromDateTime(DateTime.UtcNow);
        var snapshot = new Snapshot();
        snapshot.Devices.Add(new Device { Name = "casa1200", Driver = "s7-symbolic", Enabled = true, WritesEnabled = true, State = ConnectionState.Connected, Since = now, TagCount = 3 });
        snapshot.Devices.Add(new Device { Name = "prensa2", Driver = "s7-absolute", Enabled = true, State = ConnectionState.Faulted, Detail = "TCP : Unreachable peer", Since = now, NextRetry = now });
        snapshot.Devices.Add(new Device { Name = "linea3", Driver = "s7-symbolic", Enabled = false, State = ConnectionState.Disconnected, Detail = "Dispositivo deshabilitado", Since = now });
        snapshot.Tags.Add(Tag("clock", "Maquinas_Transfer_DB.Maquina_9.Clock_1s", "true", DataType.Boolean, Quality.Good, now, true));
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
    public Task Narrow_window_keeps_the_tag_columns_apart() => HeadlessApp.RunAsync(() =>
    {
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var vm = ViewModelWithData();
        var window = new MainWindow { DataContext = vm };
        window.Width = window.MinWidth;
        window.Show();
        window.UpdateLayout();

        // Cada texto de una fila queda dentro de su columna (si no cabe, se recorta con «…» o la columna se oculta).
        var list = window.FindControl<ListBox>("TagsList")!;
        var rows = list.GetVisualDescendants().OfType<PlcMonitor.App.Controls.TableRow>().ToArray();
        rows.ShouldNotBeEmpty();
        foreach (var row in rows)
        {
            var widths = row.Layout(row.Bounds.Width);
            widths[0].ShouldBeGreaterThan(80); // el nombre del tag sigue a la vista
            foreach (var text in row.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
            {
                var cell = row.Children.First(c => c == text || c.IsVisualAncestorOf(text));
                if (cell.Opacity == 0)
                    continue;
                var right = Avalonia.VisualExtensions.TranslatePoint(text, new Avalonia.Point(text.Bounds.Width, 0), row)!.Value.X;
                right.ShouldBeLessThanOrEqualTo(cell.Bounds.Right + 0.5, $"«{text.Text}» se sale de la columna {Grid.GetColumn(cell)}");
            }
        }
        Capture(window, "main-window-narrow.png");
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
        SelectTab(window, "Explorar");
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
        agents.Log.Add("Subiendo plcmon-agent-1.0.0-linux-x64 (49 MB)…");
        agents.Log.Add("Agente en marcha: https://192.168.0.20:5311");
        agents.Log.Add("✔ Hecho");
        window.UpdateLayout();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        texts.ShouldContain("EQUIPO");
        texts.ShouldContain("Este equipo (sin SSH)");
        Capture(window, "agents.png");

        // Este equipo: sin campos de SSH.
        agents.LocalMachine = true;
        window.UpdateLayout();
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ShouldNotContain("Usuario");
        Capture(window, "agents-local.png");
        agents.LocalMachine = false;
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Capture(window, "agents-dark.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
    });

    [Fact]
    public Task Tag_details_window_renders() => HeadlessApp.RunAsync(() =>
    {
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var vm = ViewModelWithData();
        var tag = vm.Devices[0].Tags.Single(t => t.Name == "peso");
        var config = new DeviceConfig { Name = "casa1200", WritesEnabled = true };
        var group = new ScanGroupConfig { Name = "rapido", IntervalMs = 250 };
        group.Tags.Add(new TagConfig
        {
            Name = "peso", Address = "Viruta_Datos.Viruta.Peso", Writable = true,
            History = new HistoryConfig { Enabled = true, Trigger = new HistoryTrigger { Tag = "clock", Value = "true", HoldMs = 5000 } },
        });
        group.Tags.Add(new TagConfig { Name = "clock", Address = "Maquinas_Transfer_DB.Maquina_9.Clock_1s" });
        config.ScanGroups.Add(group);
        config.ScanGroups.Add(new ScanGroupConfig { Name = "lento", IntervalMs = 2000 });
        var details = new TagDetailsViewModel(vm, tag, config, isBoolean: name => vm.FindTag("casa1200", name)?.IsBoolean == true);
        var window = new TagDetailsWindow { DataContext = details };
        window.Show();

        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("HISTÓRICO");
        (details.TriggerEnabled, details.TriggerTag, details.IsBooleanTrigger).ShouldBe((true, "clock", true));
        Capture(window, "tag-details.png");
        window.GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
        window.UpdateLayout();
        Capture(window, "tag-details-history.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Capture(window, "tag-details-dark.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
    });

    [Fact]
    public Task History_tab_renders() => HeadlessApp.RunAsync(() =>
    {
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var vm = ViewModelWithData();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.History.Tags.Add(new HistoryTagOption("peso") { IsSelected = true });
        vm.History.Tags.Add(new HistoryTagOption("clock"));
        var now = DateTimeOffset.Now;
        for (var i = 0; i < 6; i++)
            vm.History.Rows.Add(new HistoryRowViewModel(now.AddSeconds(-10 * i), "peso", (12.5 - i * 0.5).ToString(System.Globalization.CultureInfo.InvariantCulture), Quality.Good));
        vm.History.Rows.Add(new HistoryRowViewModel(now.AddMinutes(-2), "peso", "", Quality.BadNotConnected));
        vm.History.Summary = "7 puntos de 1 tags · última hora";
        vm.History.Chart.SetData([new PlcMonitor.App.Charts.ChartSeriesData("peso",
            vm.History.Rows.Where(r => r.IsGood).Select(r => new PlcMonitor.App.Charts.ChartPoint(r.Timestamp, double.Parse(r.Value, System.Globalization.CultureInfo.InvariantCulture))).OrderBy(p => p.Time).ToArray())],
            now.AddMinutes(-1), now);
        SelectTab(window, "Histórico");

        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("7 puntos de 1 tags · última hora");
        Capture(window, "history.png");
        vm.History.ShowTable = true;
        Capture(window, "history-table.png");
    });

    [Fact]
    public Task Trend_views_draw_live_values() => HeadlessApp.RunAsync(() =>
    {
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var dir = Directory.CreateTempSubdirectory("plcmon-trends-").FullName;
        var vm = ViewModelWithData(Path.Combine(dir, "app.json"));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        // Después del valor inicial de la foto (las horas del búfer no van hacia atrás).
        var now = DateTimeOffset.UtcNow.AddSeconds(241);
        for (var i = 240; i >= 0; i--)
        {
            var t = Timestamp.FromDateTimeOffset(now.AddSeconds(-i));
            var updates = new TagUpdates();
            updates.Updates.Add(new TagUpdate { Device = "casa1200", Tag = "peso", Value = new TagValue
            {
                Quality = i is > 100 and < 115 ? Quality.BadNotConnected : Quality.Good, DataType = DataType.Float,
                DoubleValue = 12 + (3 * Math.Sin(i / 20.0)), Timestamp = t,
            } });
            if (i % 15 == 0)
                updates.Updates.Add(new TagUpdate { Device = "casa1200", Tag = "clock", Value = new TagValue { Quality = Quality.Good, DataType = DataType.Boolean, BoolValue = i % 30 == 0, Timestamp = t } });
            vm.Apply(new SubscribeEvent { TagUpdates = updates });
        }

        vm.OpenTrendsCommand.Execute(null);
        var trends = vm.Trends.ShouldNotBeNull();
        trends.NewCommand.Execute(null);
        trends.Name = "Báscula";
        trends.AddTagCommand.Execute(trends.Available.Single(c => c.Tag == "peso"));
        window.UpdateLayout();
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("TAGS DE LA VISTA");
        Capture(window, "trends-edit.png");

        trends.OpenCommand.Execute(null);
        var live = trends.Live.ShouldNotBeNull();
        trends.Refresh(now);
        live.Chart.Series.ShouldHaveSingleItem().Points.Count.ShouldBeGreaterThan(200);
        Capture(window, "trend.png");
        trends.EditCommand.Execute(null);
        trends.AddTagCommand.Execute(trends.Available.Single(c => c.Tag == "clock"));
        trends.OpenCommand.Execute(null);
        trends.Refresh(now);
        trends.Live!.Chart.Series.Count.ShouldBe(2);
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Capture(window, "trend-dark.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;

        // Tendencia de disparo: solo mientras clock = true, con 3 s antes y 2 s después.
        trends.EditCommand.Execute(null);
        trends.RemoveTagCommand.Execute(new TrendTagRef("casa1200", "clock"));
        trends.TriggerEnabled = true;
        trends.TriggerTag = trends.TriggerChoices.Single(c => c.Tag == "clock");
        trends.TriggerBeforeSeconds = 3;
        trends.TriggerHoldSeconds = 2;
        window.UpdateLayout();
        Capture(window, "trends-trigger-edit.png");
        trends.OpenCommand.Execute(null);
        trends.Refresh(now);
        trends.Live!.TriggerActive.ShouldBeTrue();
        trends.Live.Chart.Series.ShouldHaveSingleItem().Points.Count(p => p.Value is null).ShouldBeGreaterThan(3);
        Capture(window, "trend-trigger.png");
        vm.CloseTrendsCommand.Execute(null);
        Directory.Delete(dir, recursive: true);
    });

    [Fact]
    public Task Csv_viewer_shows_an_exported_history() => HeadlessApp.RunAsync(() =>
    {
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var path = Path.Combine(Path.GetTempPath(), $"plcmon-{Guid.NewGuid():N}.csv");
        var start = new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);
        var csv = new System.Text.StringBuilder("device,tag,timestamp,value,quality\n");
        for (var i = 0; i < 600; i++)
        {
            var t = start.AddSeconds(i * 6).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
            csv.Append(System.Globalization.CultureInfo.InvariantCulture, $"casa1200,temperatura,{t},{20 + (5 * Math.Sin(i / 40.0)):0.##},Good\n");
            csv.Append(System.Globalization.CultureInfo.InvariantCulture, $"casa1200,presion,{t},{3 + Math.Cos(i / 25.0):0.###},Good\n");
        }
        File.WriteAllText(path, csv.ToString());
        var viewer = new CsvViewerViewModel(new FakeDialogs());
        viewer.Load(path).ShouldBeTrue(viewer.Error);
        var window = new CsvViewerWindow { DataContext = viewer };
        window.Show();

        viewer.Chart.Series.Select(s => s.Name).ShouldBe(["presion", "temperatura"]);
        viewer.Summary.ShouldContain("2 tags · 1200 puntos");
        Capture(window, "csv-viewer.png");
        viewer.Chart.AutoY = false;
        viewer.Chart.YMinInput = 0;
        viewer.Chart.YMaxInput = 40;
        viewer.Chart.Zoom(start.AddMinutes(10), start.AddMinutes(30));
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Capture(window, "csv-viewer-dark.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        File.Delete(path);
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

    private static void SelectTab(Window window, string header)
    {
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == header);
        window.UpdateLayout();
    }

    /// <summary>Si PLCMON_UI_CAPTURE_DIR está definida, guarda una captura para revisarla.</summary>
    internal static void Capture(Window window, string file)
    {
        if (Environment.GetEnvironmentVariable("PLCMON_UI_CAPTURE_DIR") is not { Length: > 0 } dir)
            return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        using var stream = File.Create(Path.Combine(dir, file));
        frame?.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
