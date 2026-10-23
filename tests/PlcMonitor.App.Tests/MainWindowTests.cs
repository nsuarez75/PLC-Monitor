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
    public Task Dragging_a_header_border_resizes_the_tag_columns() => HeadlessApp.RunAsync(() =>
    {
        var settingsPath = Path.GetTempFileName();
        var vm = ViewModelWithData(settingsPath);
        var window = new MainWindow { DataContext = vm };
        window.Width = window.MinWidth;
        window.Show();
        window.UpdateLayout();

        var header = window.FindControl<PlcMonitor.App.Controls.TableRow>("TagsHeader")!;
        var row = window.FindControl<ListBox>("TagsList")!.GetVisualDescendants().OfType<PlcMonitor.App.Controls.TableRow>().First();
        var before = header.Layout(header.Bounds.Width);

        // Arrastra 120 px a la derecha el borde entre TAG y DIRECCIÓN.
        var border = Avalonia.VisualExtensions.TranslatePoint(header, new Avalonia.Point(before[0] + (header.ColumnSpacing / 2), header.Bounds.Height / 2), window)!.Value;
        window.MouseDown(border, Avalonia.Input.MouseButton.Left);
        window.MouseMove(border + new Avalonia.Point(120, 0));
        window.MouseUp(border + new Avalonia.Point(120, 0), Avalonia.Input.MouseButton.Left);
        window.UpdateLayout();

        var after = header.Layout(header.Bounds.Width);
        // Gana los 120 px aunque la vecina no los tenga: sale de las siguientes, sin bajar ninguna del mínimo
        // (en la ventana mínima DIRECCIÓN ya está oculta y la vecina es VALOR).
        after[0].ShouldBe(before[0] + 120, 0.5);
        after.Sum().ShouldBe(before.Sum(), 0.5);
        after.Where(w => w > 0).ShouldAllBe(w => w >= PlcMonitor.App.Controls.TableRow.MinResizeWidth - 0.5);
        // Las filas siguen a la cabecera.
        row.Layout(row.Bounds.Width).ShouldBe(after, 0.5);
        // Se guarda al soltar y «Restablecer» vuelve a los de serie.
        AppSettings.Load(settingsPath).TagColumns.ShouldBe(vm.TagColumns);
        Capture(window, "main-window-resized-columns.png");
        vm.ResetTagColumnsCommand.Execute(null);
        vm.TagColumns.ShouldBe(MainViewModel.DefaultTagColumns);
        AppSettings.Load(settingsPath).TagColumns.ShouldBeNull();
    });

    [Fact]
    public Task Disabled_tag_row_is_dimmed_and_says_so() => HeadlessApp.RunAsync(() =>
    {
        var vm = ViewModelWithData();
        var off = Tag("parado", "DB1.DBW8", "", DataType.Unknown, Quality.Bad, Timestamp.FromDateTime(DateTime.UtcNow), true, "Tag desactivado");
        off.Disabled = true;
        var snapshot = new Snapshot();
        snapshot.Devices.Add(new Device { Name = "casa1200", Driver = "s7-symbolic", Enabled = true, WritesEnabled = true, State = ConnectionState.Connected, TagCount = 1 });
        snapshot.Tags.Add(off);
        vm.Apply(new SubscribeEvent { Snapshot = snapshot });
        var window = new MainWindow { DataContext = vm };
        window.Width = 1400;
        window.Show();
        window.UpdateLayout();

        var row = window.FindControl<ListBox>("TagsList")!.GetVisualDescendants().OfType<PlcMonitor.App.Controls.TableRow>().Single();
        row.Classes.ShouldContain("off");
        row.Opacity.ShouldBe(0.5);
        var texts = row.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
        texts.ShouldContain("Desactivado");
        row.GetVisualDescendants().OfType<TextBox>().ShouldAllBe(t => !t.IsEffectivelyVisible, "no se ofrece escribir");
        texts.ShouldContain("—");
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("1 desactivado");
        Capture(window, "main-window-disabled-tag.png");
    });

    [Fact]
    public void Hidden_columns_are_not_shown_and_their_width_goes_to_the_proportional_ones()
    {
        var row = new PlcMonitor.App.Controls.TableRow { Columns = MainViewModel.DefaultTagColumns, ColumnSpacing = 12, MinStarWidth = 45 };
        const double width = 1400;
        var before = row.Layout(width);
        before.ShouldAllBe(w => w > 0);

        row.HiddenColumns = "1,6";
        var after = row.Layout(width);

        (after[1], after[6]).ShouldBe((0, 0));
        after[0].ShouldBeGreaterThan(before[0]);
        // Lo que dejan libre (más sus separaciones) lo reparten las proporcionales; las fijas no cambian.
        (after.Sum() + (12 * 4)).ShouldBe(width, 0.5);
        (after[3], after[4], after[5]).ShouldBe((before[3], before[4], before[5]));
        // Ocultar por falta de ancho no vuelve a contar una columna ya oculta.
        row.Layout(500).Count(w => w > 0).ShouldBeGreaterThan(1);
    }

    [Fact]
    public Task Tag_columns_can_be_hidden_from_the_header_and_it_is_remembered() => HeadlessApp.RunAsync(() =>
    {
        var settingsPath = Path.GetTempFileName();
        var vm = ViewModelWithData(settingsPath);
        var window = new MainWindow { DataContext = vm };
        window.Width = 1400;
        window.Show();
        window.UpdateLayout();
        var header = window.FindControl<PlcMonitor.App.Controls.TableRow>("TagsHeader")!;
        var row = window.FindControl<ListBox>("TagsList")!.GetVisualDescendants().OfType<PlcMonitor.App.Controls.TableRow>().First();

        vm.ToggleTagColumnCommand.Execute(1);
        vm.ToggleTagColumnCommand.Execute(5);
        vm.ToggleTagColumnCommand.Execute(0); // la del nombre no se oculta
        window.UpdateLayout();

        vm.TagHiddenColumns.ShouldBe("1,5");
        (vm.IsTagColumnHidden(1), vm.IsTagColumnHidden(2)).ShouldBe((true, false));
        var widths = header.Layout(header.Bounds.Width);
        (widths[0] > 0, widths[1], widths[5]).ShouldBe((true, 0, 0));
        row.Layout(row.Bounds.Width).ShouldBe(widths, 0.5);
        AppSettings.Load(settingsPath).TagHiddenColumns.ShouldBe("1,5");
        Capture(window, "main-window-hidden-columns.png");

        vm.ToggleTagColumnCommand.Execute(5);
        vm.TagHiddenColumns.ShouldBe("1");
        vm.ShowAllTagColumnsCommand.Execute(null);
        vm.TagHiddenColumns.ShouldBe("");
        AppSettings.Load(settingsPath).TagHiddenColumns.ShouldBeNull();
    });

    [Theory]
    [InlineData("1,4", "1,4")]
    [InlineData("4,1,1", "1,4")]
    [InlineData("0,2,9,x", "2")]
    [InlineData(null, "")]
    public void Saved_hidden_columns_are_cleaned_up(string? saved, string applied)
    {
        var vm = new MainViewModel(new GrpcAgentConnector(), new ImmediateDispatcher(), new FakeDialogs(), new AppSettings { TagHiddenColumns = saved },
            Path.GetTempFileName(), Path.Combine(Path.GetTempPath(), $"plcmon-agents-{Guid.NewGuid():N}.json"));
        vm.TagHiddenColumns.ShouldBe(applied);
    }

    [Theory]
    [InlineData(2, 50)]   // proporcional ← fija
    [InlineData(2, -40)]  // proporcional → fija
    [InlineData(4, 30)]   // fija ← fija
    [InlineData(0, -500)] // se limita: la columna conserva el ancho mínimo
    public void Resizing_moves_width_between_neighbours_only(int left, double delta)
    {
        var row = new PlcMonitor.App.Controls.TableRow { Columns = MainViewModel.DefaultTagColumns, ColumnSpacing = 12, MinStarWidth = 45 };
        const double width = 1100;
        var before = row.Layout(width);
        row.Columns = row.ResizedColumns(width, left, delta)!;
        var after = row.Layout(width);

        var moved = Math.Max(delta, PlcMonitor.App.Controls.TableRow.MinResizeWidth - before[left]);
        after[left].ShouldBe(before[left] + moved, 0.5);
        after[left + 1].ShouldBe(before[left + 1] - moved, 0.5);
        for (var i = 0; i < before.Length; i++)
        {
            if (i != left && i != left + 1)
                after[i].ShouldBe(before[i], 0.5);
        }
    }

    [Theory]
    [InlineData("2*,2.2*,1.6*,90,130,100,250", true)]
    [InlineData("3*,1*,1.6*,90,130,100,250", true)]
    [InlineData("2*,2*", false)]
    [InlineData("2*,x,1.6*,90,130,100,250", false)]
    public void Saved_tag_columns_are_applied_only_if_valid(string saved, bool applied)
    {
        var vm = new MainViewModel(new GrpcAgentConnector(), new ImmediateDispatcher(), new FakeDialogs(), new AppSettings { TagColumns = saved },
            Path.GetTempFileName(), Path.Combine(Path.GetTempPath(), $"plcmon-agents-{Guid.NewGuid():N}.json"));
        vm.TagColumns.ShouldBe(applied ? saved : MainViewModel.DefaultTagColumns);
    }

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
    public Task Absolute_devices_add_tags_by_address_without_explore() => HeadlessApp.RunAsync(() =>
    {
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var vm = ViewModelWithData();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        // El agente de prueba no declara drivers con explorador: es lo que pasa con el absoluto (PUT/GET).
        vm.SelectedDevice = vm.Devices.Single(d => d.Name == "prensa2");
        vm.AddTag.OpenCommand.Execute(null);
        vm.AddTag.Address = "DB1.REAL20";
        vm.AddTag.Name = "presion";
        vm.AddTag.TestResult = "✔ 4.25 (Float)";
        window.UpdateLayout();

        vm.SelectedDeviceCanBrowse.ShouldBeFalse();
        window.GetVisualDescendants().OfType<TabItem>().Where(t => t.IsVisible).Select(t => t.Header as string).ShouldNotContain("Explorar");
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        texts.ShouldContain("AÑADIR TAG POR DIRECCIÓN");
        texts.ShouldContain("Añádelos con «Añadir tag» indicando su dirección (p.ej. DB1.DBW10).");
        Capture(window, "add-tag.png");
    });

    [Fact]
    public Task Opc_ua_page_shows_status_users_and_clients() => HeadlessApp.RunAsync(() =>
    {
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var vm = ViewModelWithData();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        var panel = new OpcUaViewModel(vm);
        var info = new OpcUaInfo
        {
            Enabled = true,
            Port = 4840,
            AllowWrites = false,
            State = OpcUaState.Running,
            EndpointUrl = "opc.tcp://pc-linea:4840",
            CertificateThumbprint = "5B6DC3EF56191242F38DD65DEE9033239B3D9CDC",
            Users = { new OpcUaUser { Name = "ignition", Role = OpcUaRole.Read }, new OpcUaUser { Name = "operador", Role = OpcUaRole.Write } },
            Certificates =
            {
                new OpcUaCertificate { Thumbprint = "19A47C9E966FDC7A593A25C669F074C86C81B770", Subject = "CN=Ignition OPC UA Client, O=Inductive Automation", ApplicationUri = "urn:pc-scada:ignition:opcua", NotAfter = Timestamp.FromDateTime(new DateTime(2036, 5, 1, 0, 0, 0, DateTimeKind.Utc)) },
                new OpcUaCertificate { Thumbprint = "815860D4E766301B215B6ADE58CD63B46C1602C7", Subject = "CN=UaExpert@portatil", ApplicationUri = "urn:portatil:UnifiedAutomation:UaExpert", NotAfter = Timestamp.FromDateTime(new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc)), Trusted = true },
            },
        };
        panel.Apply(info);
        vm.OpcUaPanel = panel;
        vm.Page = AppPage.OpcUa;
        window.UpdateLayout();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => t is not null).ToArray();
        texts.ShouldContain("Servidor OPC UA");
        texts.ShouldContain("En marcha");
        texts.ShouldContain("Pendiente de aceptar");
        texts.ShouldContain("Ignition OPC UA Client");
        texts.ShouldContain("Aceptar");
        var height = window.Height;
        window.Height = 1350;
        window.UpdateLayout();
        Capture(window, "opcua.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Capture(window, "opcua-dark.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        window.Height = height;
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
        agents.Log.Add("Token de solo lectura (cópialo ahora: la app no lo guarda):");
        agents.Log.Add("q8Zt0VbS3nKp1xWm7RcY2uLf9HdA4gJe6TsN5oBiEkM");
        agents.Log.Add("✔ Hecho");
        window.UpdateLayout();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        texts.ShouldContain("EQUIPO");
        texts.ShouldContain("Este equipo (sin SSH)");
        texts.ShouldContain("Renovar credenciales");
        texts.ShouldContain("Crear token de solo lectura");
        Capture(window, "agents.png");

        // Abajo: opciones de la operación, «Renovar credenciales» y el token de lectura en la actividad.
        var height = window.Height;
        window.Height = 1500;
        window.UpdateLayout();
        Capture(window, "agents-actions.png");
        window.Height = height;
        window.UpdateLayout();

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
    public Task Tag_details_fit_a_short_screen_with_buttons_visible() => HeadlessApp.RunAsync(() =>
    {
        var vm = ViewModelWithData();
        var tag = vm.Devices[0].Tags.Single(t => t.Name == "peso");
        var config = new DeviceConfig { Name = "casa1200" };
        var group = new ScanGroupConfig { Name = "rapido", IntervalMs = 250 };
        group.Tags.Add(new TagConfig { Name = "peso", Address = "Viruta_Datos.Viruta.Peso", History = new HistoryConfig { Enabled = true } });
        config.ScanGroups.Add(group);
        var window = new TagDetailsWindow { DataContext = new TagDetailsViewModel(vm, tag, config), MaxHeight = 480 };
        window.Show();
        window.UpdateLayout();

        window.Bounds.Height.ShouldBeLessThanOrEqualTo(480);
        var save = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault);
        var bottom = Avalonia.VisualExtensions.TranslatePoint(save, new Avalonia.Point(0, save.Bounds.Height), window)!.Value.Y;
        bottom.ShouldBeLessThanOrEqualTo(window.Bounds.Height);
        Capture(window, "tag-details-short.png");
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

        vm.History.ToggleDeleteCommand.Execute(null);
        window.UpdateLayout();
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("Borrar los tags marcados desde");
        Capture(window, "history-delete.png");
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

        // Opciones según el tipo de gráfica.
        trends.Kind = ChartKindChoice.For(PlcMonitor.App.Charts.ChartKind.Bars);
        trends.BarPeriod = PeriodBarsViewModel.Periods[1];
        window.UpdateLayout();
        Capture(window, "trends-edit-bars.png");
        trends.Kind = ChartKindChoice.For(PlcMonitor.App.Charts.ChartKind.Histogram);
        trends.LowLimit = 9;
        trends.HighLimit = 15;
        window.UpdateLayout();
        Capture(window, "trends-edit-histogram.png");
        trends.DataSource = TrendsViewModel.DataSources[1];
        trends.HistoryRange = TrendHistory.Ranges.Single(r => r.Value == 0);
        window.UpdateLayout();
        Capture(window, "trends-edit-history.png");
        trends.DataSource = TrendsViewModel.DataSources[0];
        trends.OpenCommand.Execute(null);
        trends.Refresh(now);
        window.UpdateLayout();
        Capture(window, "trend-histogram.png");
        trends.EditCommand.Execute(null);
        trends.Kind = ChartKindChoice.For(PlcMonitor.App.Charts.ChartKind.Trend);
        trends.LowLimit = trends.HighLimit = null;

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
    public Task Trend_views_read_an_exported_csv() => HeadlessApp.RunAsync(() =>
    {
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var dir = Directory.CreateTempSubdirectory("plcmon-csv-").FullName;
        var path = Path.Combine(dir, "prensa.csv");
        var start = new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);
        var csv = new System.Text.StringBuilder("device,tag,timestamp,value,quality\n");
        for (var i = 0; i < 600; i++)
        {
            var t = start.AddSeconds(i * 6).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
            csv.Append(System.Globalization.CultureInfo.InvariantCulture, $"casa1200,temperatura,{t},{20 + (5 * Math.Sin(i / 40.0)):0.##},Good\n");
            csv.Append(System.Globalization.CultureInfo.InvariantCulture, $"casa1200,presion,{t},{3 + Math.Cos(i / 25.0):0.###},Good\n");
            csv.Append(System.Globalization.CultureInfo.InvariantCulture, $"casa1200,contador,{t},{i * 3},Good\n");
        }
        File.WriteAllText(path, csv.ToString());
        var vm = ViewModelWithData(Path.Combine(dir, "app.json"));
        ((FakeDialogs)vm.Dialogs).OpenPath = path;
        var window = new MainWindow { DataContext = vm };
        window.Show();

        // En Tendencias, datos de un CSV: sus tags se eligen como los del agente.
        vm.OpenTrendsCommand.Execute(null);
        var trends = vm.Trends.ShouldNotBeNull();
        trends.NewCommand.Execute(null);
        trends.Name = "Prensa (CSV)";
        trends.DataSource = TrendsViewModel.DataSources.Single(d => d.Value == PlcMonitor.App.Charts.TrendDataSource.Csv);
        (trends.UsesCsv, trends.UsesLiveWindow, trends.AvailableCaption).ShouldBe((true, false, "TAGS DEL CSV"));
        trends.Available.ShouldBeEmpty();
        trends.Note.ShouldContain("Elige un fichero CSV");
        trends.OpenCommand.Execute(null);
        trends.KindError.ShouldBe("Elige el fichero CSV.");
        trends.ChooseCsvCommand.Execute(null);
        trends.CsvText.ShouldBe("prensa.csv");
        trends.Available.Select(c => c.Text).ShouldBe(["casa1200 / contador", "casa1200 / presion", "casa1200 / temperatura"]);
        trends.AddTagCommand.Execute(trends.Available.Single(c => c.Tag == "temperatura"));
        window.UpdateLayout();
        Capture(window, "trends-edit-csv.png");

        trends.OpenCommand.Execute(null);
        var live = trends.Live.ShouldNotBeNull();
        (live.IsLive, live.HistoryText).ShouldBe((false, "CSV prensa.csv · 1800 filas"));
        live.Chart.Series.Single().Name.ShouldBe("casa1200 / temperatura");
        (live.Chart.From, live.Chart.To).ShouldBe((start, start.AddSeconds(599 * 6)));
        trends.Selected!.Description.ShouldBe("1 tag · CSV prensa.csv");
        Capture(window, "trend-csv.png");

        // Mismos datos, otros tipos: histograma, X-Y con todos los tags y barras del contador.
        trends.EditCommand.Execute(null);
        trends.Kind = ChartKindChoice.For(PlcMonitor.App.Charts.ChartKind.Histogram);
        trends.OpenCommand.Execute(null);
        trends.Live!.Chart.Kind.ShouldBe(PlcMonitor.App.Charts.ChartKind.Histogram);
        Capture(window, "trend-csv-histogram.png");
        trends.EditCommand.Execute(null);
        trends.Kind = ChartKindChoice.For(PlcMonitor.App.Charts.ChartKind.XY);
        trends.AddAllCommand.Execute(null);
        trends.Tags.Count.ShouldBe(3);
        trends.MakeXAxisCommand.Execute(new PlcMonitor.App.Services.TrendTagRef("casa1200", "presion"));
        trends.OpenCommand.Execute(null);
        trends.Live!.Chart.Curves.Count.ShouldBe(2);
        trends.EditCommand.Execute(null);
        trends.Kind = ChartKindChoice.For(PlcMonitor.App.Charts.ChartKind.Bars);
        trends.DataSource.Value.ShouldBe(PlcMonitor.App.Charts.TrendDataSource.Csv); // las barras admiten CSV
        trends.BarMetric = PeriodBarsViewModel.Metrics.Single(m => m.Value == PlcMonitor.App.Charts.BarMetric.Increase);
        trends.BarPeriod = PeriodBarsViewModel.Periods.Single(p => p.Value == PlcMonitor.App.Charts.BarPeriod.Hour);
        trends.OpenCommand.Execute(null);
        var bars = trends.Bars.ShouldNotBeNull();
        bars.Description.ShouldBe("Incremento (contador) por hora · CSV prensa.csv");
        // El contador sube 3 por muestra: 1797 en total entre las horas que toque.
        bars.Chart.Options.Single(o => o.Name == "casa1200 / contador").Points.Sum(p => p.Value ?? 0).ShouldBe(599 * 3);
        Capture(window, "trend-csv-bars.png");

        // Las vistas de CSV se ven aunque se cambie de agente.
        PlcMonitor.App.Services.TrendViewStore.Load(PlcMonitor.App.Services.TrendViewStore.PathNextTo(Path.Combine(dir, "app.json")))
            .Single().CsvPath.ShouldBe(path);
        vm.CloseTrendsCommand.Execute(null);
        Directory.Delete(dir, recursive: true);
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
    [Fact]
    public Task Folders_transforms_and_their_panels_render() => HeadlessApp.RunAsync(async () =>
    {
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var vm = new MainViewModel(new GrpcAgentConnector(), new ImmediateDispatcher(), new FakeDialogs(), new AppSettings(), Path.GetTempFileName(),
            Path.Combine(Path.GetTempPath(), $"plcmon-agents-{Guid.NewGuid():N}.json"));
        var now = Timestamp.FromDateTime(DateTime.UtcNow);
        var snapshot = new Snapshot();
        snapshot.Devices.Add(new Device { Name = "casa1200", Driver = "s7-symbolic", Enabled = true, State = ConnectionState.Connected, Since = now, TagCount = 5 });
        var estados = new PlcMonitor.Agent.Contracts.ValueTransform { Name = "Estado motor" };
        estados.Entries.Add(new PlcMonitor.Agent.Contracts.TransformEntry { Value = 0, Text = "Parado" });
        estados.Entries.Add(new PlcMonitor.Agent.Contracts.TransformEntry { Value = 1, Text = "Marcha" });
        estados.Entries.Add(new PlcMonitor.Agent.Contracts.TransformEntry { Value = 2, Text = "Fallo" });
        snapshot.Transforms.Add(estados);
        Tag Folder(string name, string folder, string text, DataType type, string transform = "", long? number = null)
        {
            var tag = Tag(name, $"Linea.{name}", text, type, Quality.Good, now, false);
            tag.Folder = folder;
            tag.Transform = transform;
            if (number is { } n)
                tag.Value.IntValue = n;
            return tag;
        }
        snapshot.Tags.Add(Folder("motor_cinta", "Cinta 1", "1", DataType.Int16, "Estado motor", 1));
        snapshot.Tags.Add(Folder("velocidad_cinta", "Cinta 1", "1.25", DataType.Float));
        snapshot.Tags.Add(Folder("temperatura", "Horno", "182.4", DataType.Float));
        snapshot.Tags.Add(Folder("quemador", "Horno", "2", DataType.Int16, "Estado motor", 2));
        snapshot.Tags.Add(Folder("contador", "", "4512", DataType.Int32));
        vm.Apply(new SubscribeEvent { Snapshot = snapshot });
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => t is not null).ToArray();
        texts.ShouldContain("Cinta 1");
        texts.ShouldContain("Sin grupo");
        texts.ShouldContain("Marcha (1)");
        texts.ShouldContain("Fallo (2)");
        Capture(window, "tags-folders.png");
        vm.Devices[0].Rows.OfType<TagFolderViewModel>().Single(f => f.Name == "Horno").ToggleCommand.Execute(null);
        window.UpdateLayout();
        Capture(window, "tags-folders-collapsed.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Capture(window, "tags-folders-dark.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;

        vm.IsConnected = true;
        vm.OpenTransformsCommand.Execute(null);
        window.UpdateLayout();
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("SE MUESTRA COMO");
        vm.TransformsPanel!.Selected!.UsageText.ShouldBe("2 tags (casa1200/motor_cinta, casa1200/quemador)");
        Capture(window, "transforms.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Capture(window, "transforms-dark.png");
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        vm.CloseTransformsCommand.Execute(null);
        vm.Page.ShouldBe(AppPage.Tags);
        vm.TransformsPanel.ShouldNotBeNull("cambiar de pestaña no la cierra");

        var tag = vm.Devices[0].Tags.Single(t => t.Name == "motor_cinta");
        var config = new DeviceConfig { Name = "casa1200" };
        var group = new ScanGroupConfig { Name = "rapido", IntervalMs = 250 };
        group.Tags.Add(new TagConfig { Name = "motor_cinta", Address = "Linea.motor_cinta", Transform = "Estado motor", Folder = "Cinta 1" });
        group.Tags.Add(new TagConfig { Name = "temperatura", Address = "Linea.temperatura", Folder = "Horno" });
        config.ScanGroups.Add(group);
        var details = new TagDetailsViewModel(vm, tag, config, transforms: ["Estado motor"]);
        var detailsWindow = new TagDetailsWindow { DataContext = details };
        detailsWindow.Show();
        detailsWindow.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("Mostrar valor como");
        detailsWindow.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("Unidad");
        detailsWindow.GetVisualDescendants().OfType<ScrollViewer>().First().Offset = new Avalonia.Vector(0, 420);
        detailsWindow.UpdateLayout();
        Capture(detailsWindow, "tag-details-transform.png");
    });

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
