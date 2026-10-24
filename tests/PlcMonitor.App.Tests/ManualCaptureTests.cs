using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Google.Protobuf.WellKnownTypes;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Charts;
using PlcMonitor.App.Services;
using PlcMonitor.App.ViewModels;
using PlcMonitor.App.Views;

namespace PlcMonitor.App.Tests;

/// <summary>
/// Genera las capturas del manual con datos sintéticos y nombres de planta ficticios. Solo escribe si
/// PLCMON_UI_CAPTURE_DIR está definida: <c>PLCMON_UI_CAPTURE_DIR=docs/manual/img dotnet test tests/PlcMonitor.App.Tests</c>.
/// </summary>
public class ManualCaptureTests
{
    private const string CaptureEnv = "PLCMON_UI_CAPTURE_DIR";
    private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow.AddMinutes(-12);

    [Fact]
    public Task Manual_screenshots_use_synthetic_data_and_safe_names() => HeadlessApp.RunAsync(() =>
    {
        if (Environment.GetEnvironmentVariable(CaptureEnv) is not { Length: > 0 })
            return;
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;

        CaptureHome();
        CaptureFirstStart();
        CaptureAgents();
        CaptureDeviceEditors();
        CaptureTags();
        CaptureAddTag();
        CaptureTagDetails();
        CaptureHistory();
        CaptureBrowse();
        CaptureTrendsEditor();
        CaptureCharts();
        CapturePanel();
        CaptureTransforms();
        CaptureOpcUa();
    });

    private static void CaptureHome()
    {
        var (vm, _) = DemoVm();
        var window = MainWindow(vm);
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
        texts.ShouldContain("prensa-linea3");
        texts.ShouldContain("velocidad");
        Capture(window, "01-window.png");
    }

    private static void CaptureFirstStart()
    {
        var vm = NewVm();
        vm.Profiles.Clear();
        vm.Profiles.Add(new AgentProfile { Name = "Planta demo", Url = "https://planta-demo.local:5311" });
        vm.SelectedProfile = vm.Profiles[0];
        var window = MainWindow(vm);
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("Sin conexión");
        Capture(window, "02-primer-arranque.png");
    }

    private static void CaptureAgents()
    {
        var (vm, _) = DemoVm();
        var profile = new AgentProfile
        {
            Name = "Planta demo",
            Url = "https://planta-demo.local:5311",
            SshHost = "planta-demo.local",
            SshUser = "operador",
            Platform = "Linux x86_64",
            AgentPort = 5311,
        };
        var other = new AgentProfile { Name = "Banco de pruebas", Url = "https://banco-demo.local:5311", LocalMachine = true };
        vm.Profiles.Clear();
        vm.Profiles.Add(profile);
        vm.Profiles.Add(other);
        vm.SelectedProfile = profile;
        vm.OpenAgentsCommand.Execute(null);
        vm.Agents!.Selected = profile;
        var window = MainWindow(vm);
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("COLECTORES");
        Capture(window, "03-agentes.png");
    }

    private static void CaptureDeviceEditors()
    {
        var (vm, _) = DemoVm();
        vm.Page = AppPage.Tags;
        vm.Editor = Editor(vm, "prensa-linea3", "s7-symbolic", """{"host":"prensa-linea3.local","password":"********","username":"operador","timeoutMs":10000,"security":"Tls"}""");
        CaptureWindow(vm, "04-dispositivo-s7-simbolico.png", "Seguridad");
        vm.Editor = Editor(vm, "banco-demo", "s7-absolute", """{"host":"banco-demo.local","port":102,"cpu":"S71200","rack":0,"slot":1}""");
        CaptureWindow(vm, "04-dispositivo-s7-absoluto.png", "CPU");
        vm.Editor = Editor(vm, "horno-linea3", "modbus", """{"host":"horno-linea3.local","port":502,"unitId":1,"transport":"Tcp","byteOrder":"ABCD"}""");
        CaptureWindow(vm, "04-dispositivo-modbus.png", "Orden de bytes");
        vm.Editor = Editor(vm, "horno-linea3", "modbus", """{"host":"horno-linea3.local","port":502,"unitId":1,"transport":"Tcp","byteOrder":"ABCD"}""");
        vm.Editor!.ShowJson = true;
        CaptureWindow(vm, "04-dispositivo-json.png", "JSON del driver");
    }

    private static void CaptureTags()
    {
        var (vm, _) = DemoVm();
        var window = MainWindow(vm);
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
        texts.ShouldContain("Línea 3");
        texts.ShouldContain("Calidad");
        Capture(window, "05-tags.png");
    }

    private static void CaptureAddTag()
    {
        var (vm, _) = DemoVm();
        var window = MainWindow(vm);
        vm.AddTag.OpenCommand.Execute(null);
        vm.AddTag.Address = "Prensa_DB.Datos.Velocidad";
        vm.AddTag.Name = "velocidad";
        vm.AddTag.GroupName = "rapido";
        vm.AddTag.IntervalMs = 500;
        window.UpdateLayout();
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("AÑADIR TAG POR DIRECCIÓN");
        Capture(window, "05-alta-tag.png");

        var (modbus, _) = DemoVm();
        var modbusWindow = MainWindow(modbus);
        modbus.SelectedDevice = modbus.Devices.Single(d => d.Driver == "modbus");
        modbusWindow.UpdateLayout();
        modbus.AddTag.OpenCommand.Execute(null);
        modbus.AddTag.ModbusTable = AddTagViewModel.ModbusTables[0];
        modbus.AddTag.ModbusNumber = 100;
        modbus.AddTag.ModbusType = AddTagViewModel.ModbusTypes.Single(t => t.Value == "FLOAT");
        modbus.AddTag.RangeCount = 4;
        modbus.AddTag.Name = "temperatura_zona";
        modbusWindow.UpdateLayout();
        modbusWindow.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("Registros de retención (HR · 4x)");
        Capture(modbusWindow, "05-alta-tag-modbus.png");
    }

    private static void CaptureTagDetails()
    {
        var (vm, _) = DemoVm();
        var device = vm.Devices.Single(d => d.Name == "prensa-linea3");
        var tag = device.Tags.Single(t => t.Name == "fuerza");
        var config = new DeviceConfig { Name = device.Name, WritesEnabled = true, OpcUaPublished = true };
        var group = new ScanGroupConfig { Name = "rapido", IntervalMs = 500 };
        group.Tags.Add(new TagConfig
        {
            Name = "fuerza",
            Address = "Prensa_DB.Datos.Fuerza",
            Writable = true,
            Unit = "kN",
            Decimals = 1,
            Folder = "Calidad",
            History = new HistoryConfig { Enabled = true, MinIntervalMs = 1000, Deadband = 0.5 },
        });
        config.ScanGroups.Add(group);
        var details = new TagDetailsViewModel(vm, tag, config, transforms: ["Estado de la prensa"]);
        var window = new TagDetailsWindow { DataContext = details, Width = 760 };
        window.Show();
        window.UpdateLayout();
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("HISTÓRICO");
        Capture(window, "05-detalle-tag.png");
        window.Close();
    }

    private static void CaptureHistory()
    {
        var (vm, _) = DemoVm();
        var history = vm.History;
        history.Range = HistoryViewModel.Ranges.Single(r => r.Value == TimeSpan.FromHours(8));
        history.Aggregate = HistoryViewModel.Aggregates.Single(a => a.Value == HistoryAggregate.Average);
        history.Interval = HistoryViewModel.Intervals.Single(i => i.Value == TimeSpan.FromMinutes(5));
        foreach (var option in history.Tags)
            option.IsSelected = option.Name is "velocidad" or "fuerza" or "presion";
        history.Summary = "288 puntos de 3 tags · últimas 8 h · media cada 5 min";
        var points = Enumerable.Range(0, 96).Select(i => Start.AddHours(-8).AddMinutes(i * 5)).ToArray();
        history.Chart.Title = "Histórico de prensa-linea3";
        history.Chart.Source = "Últimas 8 h · media cada 5 min";
        history.Chart.SeparateAxes = true;
        history.Chart.SetData(
        [
            HistorySeries("velocidad", points, Speed, "rpm", 0),
            HistorySeries("fuerza", points, Force, "kN", 1),
            HistorySeries("presion", points, Pressure, "bar", 2),
        ]);
        vm.DeviceTab = MainViewModel.HistoryTab;
        var window = MainWindow(vm);
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("prensa-linea3 / velocidad");
        Capture(window, "06-historico-grafica.png");

        history.ShowTable = true;
        foreach (var (time, i) in points.Select((time, i) => (time, i)).TakeLast(3).Reverse())
        {
            history.Rows.Add(Row(time, "prensa-linea3 / velocidad", Speed(i), "rpm", 0));
            history.Rows.Add(Row(time, "prensa-linea3 / fuerza", Force(i), "kN", 1));
            history.Rows.Add(Row(time, "prensa-linea3 / presion", Pressure(i), "bar", 2));
        }
        window.UpdateLayout();
        Capture(window, "06-historico-tabla.png");

        static double Speed(int i) => 1180 + Math.Sin(i / 7.0) * 90;
        static double Force(int i) => 24 + Math.Sin(i / 5.0) * 4;
        static double Pressure(int i) => 5.6 + Math.Cos(i / 9.0) * 0.4;
        static HistoryRowViewModel Row(DateTimeOffset time, string tag, double value, string unit, int decimals) =>
            new(time, tag, value.ToString(CultureInfo.InvariantCulture), Quality.Good)
            {
                DisplayValue = $"{value.ToString($"F{decimals}", CultureInfo.InvariantCulture)} {unit}",
                Unit = unit,
            };
    }

    private static void CaptureBrowse()
    {
        var (vm, _) = DemoVm();
        vm.DeviceTab = 2;
        var browse = vm.Browse;
        browse.Filter = "Motor";
        browse.Summary = "12 símbolos que contienen «Motor» · 2 ya son tags";
        (string Address, string Native, string Type, bool Added)[] items =
        [
            ("Prensa_DB.Motor.Velocidad", "Real", "Float", true),
            ("Prensa_DB.Motor.Corriente", "Real", "Float", false),
            ("Prensa_DB.Motor.Temperatura", "Real", "Float", false),
            ("Prensa_DB.Motor.Marcha", "Bool", "Boolean", true),
            ("Prensa_DB.Motor.Fallo", "Bool", "Boolean", false),
            ("Prensa_DB.Motor.Horas", "DInt", "Int32", false),
            ("Prensa_DB.Motor.Arranques", "Int", "Int16", false),
            ("Prensa_DB.Motor.Frecuencia", "Real", "Float", false),
        ];
        foreach (var item in items)
            browse.Results.Add(new BrowseItemViewModel(item.Address, item.Native, item.Type) { IsAdded = item.Added });
        var window = MainWindow(vm);
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("Prensa_DB.Motor.Velocidad");
        Capture(window, "05-explorar.png");
    }

    private static void CaptureTrendsEditor()
    {
        var (vm, _) = DemoVm();
        var path = Path.Combine(Path.GetTempPath(), $"plcmon-manual-trends-{Guid.NewGuid():N}.json");
        var trends = new TrendsViewModel(vm, path);
        trends.Views.Add(new TrendViewItem(new TrendView
        {
            Name = "Presión y fuerza",
            Agent = vm.AgentKey,
            Kind = ChartKind.Trend,
            DataSource = TrendDataSource.Live,
            WindowMinutes = 15,
            Tags = [new TrendTagRef("prensa-linea3", "fuerza"), new TrendTagRef("prensa-linea3", "presion")],
        }));
        trends.Views.Add(new TrendViewItem(new TrendView
        {
            Name = "Ciclo de prensa",
            Agent = vm.AgentKey,
            Kind = ChartKind.Cycles,
            DataSource = TrendDataSource.Live,
            Tags = [new TrendTagRef("prensa-linea3", "velocidad"), new TrendTagRef("prensa-linea3", "fuerza")],
        }));
        trends.Views.Add(new TrendViewItem(new TrendView
        {
            Name = "Temperatura del horno",
            Agent = vm.AgentKey,
            Kind = ChartKind.Trend,
            DataSource = TrendDataSource.History,
            HistoryMinutes = 480,
            WindowMinutes = 60,
            Tags = [new TrendTagRef("horno-linea3", "temperatura")],
        }));
        trends.Selected = trends.Views[0];
        vm.Trends = trends;
        vm.Page = AppPage.Trends;
        var window = MainWindow(vm);
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("Presión y fuerza");
        Capture(window, "07-tendencias-editor.png");
    }

    private static void CaptureCharts()
    {
        var (vm, _) = DemoVm();
        var now = Start.AddMinutes(10);
        var points = Enumerable.Range(0, 180).Select(i => now.AddSeconds(-179 + i)).ToArray();

        var trend = new ChartViewModel { Kind = ChartKind.Trend, Title = "Velocidad y fuerza", Source = "En vivo · últimos 5 min", ShowStats = true, SeparateAxes = true };
        trend.SetData(
        [
            ChartSeries("prensa-linea3 / velocidad", points, i => 1200 + Math.Sin(i / 15.0) * 120, "rpm", 0),
            ChartSeries("prensa-linea3 / fuerza", points, i => 24 + Math.Sin(i / 11.0) * 5, "kN", 1),
        ]);
        CaptureChart(vm, trend, "07-tendencia.png");

        var states = new ChartViewModel { Kind = ChartKind.States, Title = "Marcha y fallo", Source = "En vivo · últimos 5 min" };
        states.SetData(
        [
            ChartSeries("prensa-linea3 / marcha", points, i => i is > 30 and < 150 ? 1 : 0, "", 0, isBoolean: true),
            ChartSeries("prensa-linea3 / fallo", points, i => i is > 100 and < 108 ? 1 : 0, "", 1, isBoolean: true),
        ]);
        CaptureChart(vm, states, "07-cronograma.png");

        var cycles = new ChartViewModel { Kind = ChartKind.Cycles, Title = "Ciclo de prensa", Source = "3 ciclos superpuestos", XLabel = "s desde el disparo" };
        cycles.Curves =
        [
            new XYCurve("fuerza", Enumerable.Range(0, 41).Select(i => new XYPoint(i * 0.25, 5 + Math.Sin(i / 4.0) * 20)).ToArray(), 1)
                { Display = new SeriesFormat("kN", 1) },
            new XYCurve("velocidad", Enumerable.Range(0, 41).Select(i => new XYPoint(i * 0.25, 900 + Math.Sin(i / 6.0) * 500)).ToArray(), 0)
                { Display = new SeriesFormat("rpm", 0) },
        ];
        CaptureChart(vm, cycles, "07-ciclos.png");

        var xy = new ChartViewModel { Kind = ChartKind.XY, Title = "Velocidad frente a fuerza", Source = "En vivo · últimos 15 min", XLabel = "Velocidad (rpm)" };
        xy.Curves =
        [
            new XYCurve("prensa-linea3", Enumerable.Range(0, 80).Select(i => new XYPoint(600 + i * 10, 15 + Math.Sin(i / 8.0) * 8 + i * 0.08)).ToArray(), 0)
                { Display = new SeriesFormat("kN", 1) },
        ];
        CaptureChart(vm, xy, "07-xy.png");

        var histogram = new ChartViewModel { Kind = ChartKind.Histogram, Title = "Distribución de fuerza", Source = "Últimas 8 h · 480 muestras", LowLimit = 18, HighLimit = 30 };
        histogram.SetData([ChartSeries("fuerza", Enumerable.Range(0, 480).Select(i => now.AddMinutes(-480 + i)).ToArray(), i => 24 + Math.Sin(i / 17.0) * 4 + ((i * 37) % 11) / 10.0, "kN", 0)]);
        CaptureChart(vm, histogram, "07-histograma.png");

        var gauges = new ChartViewModel { Kind = ChartKind.Gauges, Title = "Indicadores de la línea", Source = "Valor actual", LowLimit = 18, HighLimit = 30 };
        gauges.SetData(
        [
            ChartSeries("velocidad", [now], _ => 1260, "rpm", 0),
            ChartSeries("fuerza", [now], _ => 24.6, "kN", 1),
            ChartSeries("presion", [now], _ => 5.8, "bar", 2),
            ChartSeries("temperatura", [now], _ => 184.2, "°C", 3),
        ]);
        CaptureChart(vm, gauges, "07-indicadores.png");

        var bars = new ChartViewModel { Kind = ChartKind.Bars, Title = "Producción por día", Source = "Últimos 7 días · media", BarPeriod = BarPeriod.Day };
        bars.SetData(
        [
            ChartSeries("prensa-linea3 / velocidad", Enumerable.Range(0, 7 * 24).Select(i => now.AddHours(-168 + i)).ToArray(), i => 1100 + (i % 24) * 25 + (i % 7) * 40, "rpm", 0),
            ChartSeries("prensa-linea3 / fuerza", Enumerable.Range(0, 7 * 24).Select(i => now.AddHours(-168 + i)).ToArray(), i => 20 + (i % 12) * 0.8, "kN", 1),
        ]);
        CaptureChart(vm, bars, "07-barras.png");
    }

    private static void CapturePanel()
    {
        var (vm, _) = DemoVm();
        var trends = new TrendsViewModel(vm, Path.Combine(Path.GetTempPath(), $"plcmon-manual-panel-{Guid.NewGuid():N}.json"));
        var saved = new[]
        {
            new TrendViewItem(new TrendView { Name = "Velocidad", Agent = vm.AgentKey, Kind = ChartKind.Trend, DataSource = TrendDataSource.Live, WindowMinutes = 15, Tags = [new TrendTagRef("prensa-linea3", "velocidad")] }),
            new TrendViewItem(new TrendView { Name = "Fuerza", Agent = vm.AgentKey, Kind = ChartKind.Trend, DataSource = TrendDataSource.Live, WindowMinutes = 15, Tags = [new TrendTagRef("prensa-linea3", "fuerza")] }),
            new TrendViewItem(new TrendView { Name = "Marcha y fallo", Agent = vm.AgentKey, Kind = ChartKind.States, DataSource = TrendDataSource.Live, WindowMinutes = 15, Tags = [new TrendTagRef("prensa-linea3", "marcha")] }),
            new TrendViewItem(new TrendView { Name = "Presión", Agent = vm.AgentKey, Kind = ChartKind.Trend, DataSource = TrendDataSource.Live, WindowMinutes = 15, Tags = [new TrendTagRef("prensa-linea3", "presion")] }),
        };
        foreach (var view in saved)
            trends.Views.Add(view);
        var panel = new TrendPanel { Name = "Línea 3 · turno", Columns = 2, SyncZoom = true, Views = [.. saved.Select(v => v.View.Id)] };
        var item = new TrendPanelItem(panel);
        trends.Panels.Add(item);
        trends.SelectedPanel = item;
        var now = Start.AddMinutes(10);
        var points = Enumerable.Range(0, 120).Select(i => now.AddSeconds(-119 + i)).ToArray();
        var cells = new List<PanelCellViewModel>();
        AddCell("Velocidad", ChartKind.Trend, i => 1200 + Math.Sin(i / 13.0) * 100, "rpm");
        AddCell("Fuerza", ChartKind.Trend, i => 24 + Math.Sin(i / 9.0) * 4, "kN");
        AddCell("Marcha y fallo", ChartKind.States, i => i is > 20 and < 100 ? 1 : 0, "", boolean: true);
        AddCell("Presión", ChartKind.Trend, i => 5.7 + Math.Cos(i / 17.0) * 0.4, "bar");
        SetPrivate(trends, nameof(TrendsViewModel.OpenPanel), new OpenPanelViewModel(panel, cells));
        vm.Trends = trends;
        vm.Page = AppPage.Trends;
        var window = MainWindow(vm);
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("Línea 3 · turno");
        Capture(window, "08-panel.png");

        void AddCell(string title, ChartKind kind, Func<int, double> value, string unit, bool boolean = false)
        {
            var chart = new ChartViewModel { Kind = kind, Title = title, EmptyText = "" };
            chart.SetData([ChartSeries(title, points, value, unit, cells.Count, isBoolean: boolean)]);
            var live = new LiveTrendViewModel(() => [], chart: chart);
            cells.Add(new PanelCellViewModel(title, new OpenedView(live, null)));
        }
    }

    private static void CaptureTransforms()
    {
        var (vm, _) = DemoVm();
        vm.IsConnected = true;
        vm.OpenTransformsCommand.Execute(null);
        var window = MainWindow(vm);
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("Estado de la prensa");
        Capture(window, "09-transformaciones.png");
    }

    private static void CaptureOpcUa()
    {
        var (vm, _) = DemoVm();
        vm.IsConnected = true;
        var opc = new OpcUaViewModel(vm);
        var info = new OpcUaInfo
        {
            Enabled = true,
            Port = 4840,
            AllowWrites = true,
            State = OpcUaState.Running,
            EndpointUrl = "opc.tcp://planta-demo.local:4840",
            CertificateThumbprint = "6F:3A:2C:11:8E:90:4D:7B:52:A1:CC:0F:8B:64:27:D9:31:AE:05:42",
        };
        info.Users.Add(new OpcUaUser { Name = "operador", Role = OpcUaRole.Read });
        info.Users.Add(new OpcUaUser { Name = "mantenimiento", Role = OpcUaRole.Write });
        info.Certificates.Add(new OpcUaCertificate
        {
            Thumbprint = "A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90:A1:B2:C3:D4",
            Subject = "CN=UaExpert, O=Unified Automation",
            ApplicationUri = "urn:freeopcua:client",
            Trusted = true,
            NotAfter = Timestamp.FromDateTime(DateTime.UtcNow.AddYears(1)),
        });
        opc.Apply(info);
        vm.OpcUaPanel = opc;
        vm.Page = AppPage.OpcUa;
        var window = MainWindow(vm);
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("Servidor OPC UA");
        Capture(window, "10-opc-ua.png");
    }

    private static (MainViewModel Vm, DateTimeOffset Start) DemoVm()
    {
        var vm = NewVm();
        vm.Apply(new SubscribeEvent { Snapshot = DemoSnapshot() });
        var updates = new TagUpdates();
        for (var i = 1; i <= 150; i++)
        {
            var time = Timestamp.FromDateTimeOffset(Start.AddSeconds(i * 5));
            var phase = i / 10.0;
            Add("marcha", i is > 20 and < 140 ? "true" : "false", DataType.Boolean);
            Add("velocidad", (i is > 20 and < 140 ? 1200 + Math.Sin(phase) * 110 : 0).ToString("0", CultureInfo.InvariantCulture), DataType.Float);
            Add("fuerza", (24 + Math.Sin(phase * 1.3) * 5).ToString("0.0", CultureInfo.InvariantCulture), DataType.Float);
            Add("presion", (5.6 + Math.Cos(phase * 0.8) * 0.4).ToString("0.00", CultureInfo.InvariantCulture), DataType.Float);
            updates.Updates.Add(new TagUpdate { Device = "horno-linea3", Tag = "temperatura", Value = Good((182 + Math.Sin(phase * 0.5) * 4).ToString("0.0", CultureInfo.InvariantCulture), DataType.Float, time) });

            void Add(string name, string text, DataType type) =>
                updates.Updates.Add(new TagUpdate { Device = "prensa-linea3", Tag = name, Value = Good(text, type, time) });
        }
        vm.Apply(new SubscribeEvent { TagUpdates = updates });
        vm.IsConnected = true;
        vm.IsLive = true;
        vm.AgentDescription = "planta-demo · linux-x64 · v1.16.0";
        vm.Message = "Conectado a planta-demo.local · 4 dispositivos · 12 tags";
        vm.SelectedDevice = vm.Devices.First();
        return (vm, Start);
    }

    private static Snapshot DemoSnapshot()
    {
        var start = Timestamp.FromDateTimeOffset(Start);
        var snapshot = new Snapshot();
        snapshot.Devices.Add(new Device { Name = "prensa-linea3", Driver = "s7-symbolic", Enabled = true, WritesEnabled = true, OpcUaPublished = true, State = ConnectionState.Connected, Since = start, TagCount = 6 });
        snapshot.Devices.Add(new Device { Name = "horno-linea3", Driver = "modbus", Enabled = true, WritesEnabled = false, OpcUaPublished = true, State = ConnectionState.Connected, Since = start, TagCount = 4 });
        snapshot.Devices.Add(new Device { Name = "banco-demo", Driver = "s7-absolute", Enabled = true, WritesEnabled = true, State = ConnectionState.Faulted, Detail = "No se puede conectar: host inalcanzable", Since = start, NextRetry = start, TagCount = 2 });

        snapshot.Tags.Add(Tag("prensa-linea3", "marcha", "Prensa_DB.Datos.Marcha", "true", DataType.Boolean, true, true, "Línea 3", start));
        snapshot.Tags.Add(Tag("prensa-linea3", "velocidad", "Prensa_DB.Datos.Velocidad", "1200", DataType.Float, true, true, "Línea 3", start, "rpm", 0));
        snapshot.Tags.Add(Tag("prensa-linea3", "fuerza", "Prensa_DB.Datos.Fuerza", "24.6", DataType.Float, true, true, "Calidad", start, "kN", 1));
        snapshot.Tags.Add(Tag("prensa-linea3", "presion", "Prensa_DB.Datos.Presion", "5.62", DataType.Float, true, true, "Calidad", start, "bar", 2));
        snapshot.Tags.Add(Tag("prensa-linea3", "contador", "Prensa_DB.Datos.Contador", "18432", DataType.Int32, false, true, "Consumibles", start));
        snapshot.Tags.Add(Tag("prensa-linea3", "estado", "Prensa_DB.Datos.Estado", "1", DataType.Int16, false, false, "Estado", start, transform: "Estado de la prensa"));
        snapshot.Tags.Add(Tag("horno-linea3", "temperatura", "HR:100", "184.2", DataType.Float, false, true, "Horno", start, "°C", 1));
        snapshot.Tags.Add(Tag("horno-linea3", "consigna", "HR:104", "185.0", DataType.Float, true, false, "Horno", start, "°C", 1));
        snapshot.Tags.Add(Tag("horno-linea3", "quemador", "C:10", "true", DataType.Boolean, true, true, "Horno", start));
        snapshot.Tags.Add(Tag("horno-linea3", "alarma", "C:11", "false", DataType.Boolean, false, false, "Horno", start));
        snapshot.Tags.Add(Tag("banco-demo", "entrada", "DB1.DBW0", "", DataType.Int16, false, false, "Banco", start, error: "Sin conexión con el dispositivo"));
        snapshot.Tags.Add(Tag("banco-demo", "salida", "DB1.DBW2", "", DataType.Int16, false, false, "Banco", start, error: "Sin conexión con el dispositivo"));

        snapshot.Transforms.Add(new ValueTransform
        {
            Name = "Estado de la prensa",
            Entries = { new TransformEntry { Value = 0, Text = "Parada" }, new TransformEntry { Value = 1, Text = "Marcha" }, new TransformEntry { Value = 2, Text = "Avería" } },
        });
        return snapshot;
    }

    private static Tag Tag(string device, string name, string address, string text, DataType type, bool writable, bool historized, string folder,
        Timestamp time, string unit = "", int? decimals = null, string transform = "", string error = "")
    {
        var tag = new Tag
        {
            Device = device,
            Name = name,
            Address = address,
            Group = "rapido",
            IntervalMs = 500,
            Writable = writable,
            Historized = historized,
            Folder = folder,
            Unit = unit,
            Transform = transform,
            Value = Good(text, type, time, error),
        };
        if (decimals is { } d)
            tag.Decimals = d;
        return tag;
    }

    private static TagValue Good(string text, DataType type, Timestamp time, string error = "")
    {
        var value = new TagValue { Text = text, DataType = type, Quality = error.Length > 0 ? Quality.Bad : Quality.Good, Timestamp = time, Error = error };
        if (error.Length > 0)
            return value;
        switch (type)
        {
            case DataType.Boolean:
                value.BoolValue = string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
                break;
            case DataType.Float:
            case DataType.Double:
                value.DoubleValue = double.Parse(text, CultureInfo.InvariantCulture);
                break;
            case DataType.Int16:
            case DataType.Int32:
            case DataType.Int64:
                value.IntValue = long.Parse(text, CultureInfo.InvariantCulture);
                break;
            case DataType.UInt16:
            case DataType.UInt32:
            case DataType.UInt64:
                value.UintValue = ulong.Parse(text, CultureInfo.InvariantCulture);
                break;
            default:
                value.StringValue = text;
                break;
        }
        return value;
    }

    private static ChartSeriesData HistorySeries(string name, DateTimeOffset[] times, Func<int, double> value, string unit, int color)
    {
        var points = times.Select((time, i) => new ChartPoint(time, value(i))).ToArray();
        return new ChartSeriesData($"prensa-linea3 / {name}", points) { Display = new SeriesFormat(unit, unit.Length == 0 ? null : 1) };
    }

    private static ChartSeriesData ChartSeries(string name, DateTimeOffset[] times, Func<int, double> value, string unit, int color, bool isBoolean = false)
    {
        var points = times.Select((time, i) => new ChartPoint(time, value(i))).ToArray();
        return new ChartSeriesData(name, points, Step: true, IsBoolean: isBoolean) { Display = new SeriesFormat(unit, isBoolean || unit.Length == 0 ? null : 1) };
    }

    private static MainWindow MainWindow(MainViewModel vm)
    {
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        if (vm.SelectedDevice is null && vm.Devices.Count > 0)
        {
            vm.SelectedDevice = vm.Devices[0];
            window.UpdateLayout();
        }
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void CaptureWindow(MainViewModel vm, string file, string expected)
    {
        var window = MainWindow(vm);
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain(expected);
        Capture(window, file);
    }

    private static void CaptureChart(MainViewModel vm, ChartViewModel chart, string file)
    {
        var stack = new StackPanel { Margin = new Thickness(22), Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = chart.Title, FontSize = 20, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(new TextBlock { Text = chart.Source, FontSize = 12, Foreground = Brushes.Gray });
        stack.Children.Add(new ChartPanel { DataContext = chart, Height = 540 });
        var window = new Window
        {
            Title = chart.Title,
            Width = 1040,
            Height = 700,
            Background = new SolidColorBrush(Color.Parse("#F7F7F8")),
            Content = stack,
            DataContext = vm,
        };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain(chart.Title);
        Capture(window, file);
        window.Close();
    }

    private static void SetPrivate(object target, string property, object value) =>
        target.GetType().GetProperty(property)!.SetValue(target, value);

    private static MainViewModel NewVm()
    {
        var vm = new MainViewModel(new GrpcAgentConnector(), new ImmediateDispatcher(), new FakeDialogs(), new AppSettings(),
            Path.Combine(Path.GetTempPath(), $"plcmon-manual-{Guid.NewGuid():N}.json"),
            Path.Combine(Path.GetTempPath(), $"plcmon-manual-agents-{Guid.NewGuid():N}.json"));
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.Drivers))!.SetValue(vm, Drivers);
        vm.Profiles.Clear();
        vm.Profiles.Add(new AgentProfile { Name = "Planta demo", Url = "https://planta-demo.local:5311" });
        vm.SelectedProfile = vm.Profiles[0];
        return vm;
    }

    private static IReadOnlyList<Driver> Drivers =>
    [
        new Driver { Id = "s7-symbolic", Name = "Siemens S7 simbólico (S7CommPlus, TLS o sin TLS)", CanBrowse = true, CanWrite = true, DefaultSettingsJson = "{}" },
        new Driver { Id = "s7-absolute", Name = "Siemens S7 absoluto (PUT/GET)", CanBrowse = false, CanWrite = true, DefaultSettingsJson = "{}" },
        new Driver { Id = "modbus", Name = "Modbus TCP / RTU", CanBrowse = false, CanWrite = true, DefaultSettingsJson = "{}" },
    ];

    private static DeviceEditorViewModel Editor(MainViewModel vm, string name, string driver, string json) =>
        new(vm, Drivers, new DeviceConfig
        {
            Name = name,
            Driver = driver,
            SettingsJson = json,
            Enabled = true,
            WritesEnabled = true,
            OpcUaPublished = true,
        }, _ => { });

    private static void Capture(Window window, string file)
    {
        MainWindowTests.Capture(window, file);
    }
}
