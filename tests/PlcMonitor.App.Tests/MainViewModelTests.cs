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
    private readonly string _profilesPath = Path.Combine(Path.GetTempPath(), $"plcmon-agents-{Guid.NewGuid():N}.json");
    private readonly LockingDispatcher _ui = new();
    private readonly FakeDialogs _dialogs = new();

    public MainViewModelTests()
    {
        MainViewModel.ResubscribeDelay = TimeSpan.FromMilliseconds(100);
    }

    private static Tag T(string address, string? name = null, bool writable = false) => new() { Address = address, Name = name, Writable = writable };

    private MainViewModel Create(AgentFactory agent, AppSettings? settings = null) =>
        new(new InMemoryConnector(agent), _ui, _dialogs, settings ?? new AppSettings(), _settingsPath, _profilesPath);

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
        AgentProfileStore.Load(_profilesPath).Select(p => p.Url).ShouldContain("http://pc-linea:5310");
    }

    [Fact]
    public async Task Unreachable_agent_reports_an_error()
    {
        var settings = new AppSettings { AgentUrl = "http://127.0.0.1:1" };
        await using var vm = new MainViewModel(new GrpcAgentConnector(), _ui, _dialogs, settings, _settingsPath, _profilesPath);

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
    public async Task Booleans_are_written_with_true_and_false_buttons()
    {
        await using var agent = new AgentFactory(Device("plc1", writesEnabled: true, tags: [T("DB1.DBX0.0", "marcha", writable: true)]));
        var plc = agent.Driver.Plc("plc1");
        plc.Values["DB1.DBX0.0"] = false;
        await using var vm = await ConnectAsync(agent);
        var tag = TagOf(vm, "plc1", "marcha");
        await Wait.UntilAsync(() => _ui.Read(() => tag.IsGood));

        _ui.Read(() => (tag.IsBoolean, tag.ShowBoolWrite, tag.ShowTextWrite, tag.IsFalse, tag.IsTrue)).ShouldBe((true, true, false, true, false));

        await tag.WriteBoolCommand.ExecuteAsync("true");

        _dialogs.Questions.ShouldHaveSingleItem().ShouldContain("«true» en plc1/marcha");
        plc.Writes.ShouldHaveSingleItem().Value.ShouldBe(true);
        await Wait.UntilAsync(() => _ui.Read(() => tag.IsTrue && !tag.IsFalse), "botón true encendido");
    }

    [Fact]
    public async Task System_theme_has_its_own_icon()
    {
        await using var agent = new AgentFactory();
        await using var vm = Create(agent);
        vm.Theme = "system";
        var system = vm.ThemeIcon;
        vm.Theme = "light";
        var light = vm.ThemeIcon;
        vm.Theme = "dark";
        (system, light, vm.ThemeIcon).ShouldBe(("monitor", "sun", "moon"));
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

        // Ya añadido: el símbolo queda marcado y no se vuelve a añadir.
        vm.Browse.Results[0].IsAdded.ShouldBeTrue();
        await vm.Browse.AddCommand.ExecuteAsync(vm.Browse.Results[0]);
        (await ConfigurationFileLoad(agent)).Devices[0].ScanGroups.SelectMany(g => g.Tags).Count(t => t.Address == "DB1.Motor").ShouldBe(1);
    }

    [Fact]
    public async Task Browse_marks_symbols_that_are_already_tags()
    {
        await using var agent = new AgentFactory(Device("plc1", tags: [T("DB1.Motor", "motor")]));
        var plc = agent.Driver.Plc("plc1");
        plc.Values["DB1.Motor"] = 1;
        plc.Values["DB1.Bomba"] = 2;
        await using var vm = await ConnectAsync(agent);
        await Wait.UntilAsync(() => _ui.Read(() => vm.Devices[0].IsConnected));

        await vm.Browse.SearchCommand.ExecuteAsync(null);

        var results = vm.Browse.Results.ToDictionary(r => r.Address);
        results["DB1.Motor"].IsAdded.ShouldBeTrue();
        results["DB1.Bomba"].IsAdded.ShouldBeFalse();

        await vm.Browse.AddCommand.ExecuteAsync(results["DB1.Bomba"]);
        results["DB1.Bomba"].IsAdded.ShouldBeTrue();
        await vm.Browse.AddCommand.ExecuteAsync(results["DB1.Motor"]);
        (await ConfigurationFileLoad(agent)).Devices[0].ScanGroups.SelectMany(g => g.Tags).Count().ShouldBe(2);
    }

    [Fact]
    public async Task Tag_details_show_and_save_the_tag_configuration()
    {
        await using var agent = new AgentFactory(Device("plc1", writesEnabled: true, tags: [T("DB1.DBW0", "velocidad"), T("DB1.DBW2", "otro")]));
        agent.Driver.Plc("plc1").Values["DB1.DBW0"] = 5;
        agent.Driver.Plc("plc1").Values["DB1.DBW2"] = 6;
        await using var vm = await ConnectAsync(agent);
        var tag = TagOf(vm, "plc1", "velocidad");

        await vm.OpenTagDetailsCommand.ExecuteAsync(tag);

        var details = _dialogs.TagDetails.ShouldHaveSingleItem();
        details.Tag.ShouldBeSameAs(tag);
        (details.Name, details.Address, details.GroupName, details.IntervalMs, details.Writable, details.SelectedDataType)
            .ShouldBe(("velocidad", "DB1.DBW0", "g", 50, false, TagDetailsViewModel.AutomaticType));
        details.WritesEnabled.ShouldBeTrue();
        details.Groups.ShouldBe(["g"]);
        details.GroupNote.ShouldBe("El grupo «g» se lee cada 50 ms.");

        details.Name = "otro";
        await details.SaveCommand.ExecuteAsync(null);
        details.Saved.ShouldBeFalse();
        details.Errors.ShouldContain("repetido");

        details.Name = "consigna";
        details.GroupName = "lento";
        details.IntervalMs = 2000;
        details.GroupNote.ShouldBe("Grupo nuevo: se creará con un periodo de 2000 ms.");
        details.Writable = true;
        details.SelectedDataType = "Int16";
        await details.SaveCommand.ExecuteAsync(null);

        details.Saved.ShouldBeTrue(details.Errors);
        var saved = (await ConfigurationFileLoad(agent)).Devices[0];
        var moved = saved.ScanGroups.Single(g => g.Name == "lento");
        moved.IntervalMs.ShouldBe(2000);
        var config = moved.Tags.ShouldHaveSingleItem();
        (config.Name, config.Address, config.Writable, config.DataType).ShouldBe(("consigna", "DB1.DBW0", true, (PlcMonitor.Abstractions.TagDataType?)PlcMonitor.Abstractions.TagDataType.Int16));
        saved.ScanGroups.Single(g => g.Name == "g").Tags.Select(t => t.Name).ShouldBe(["otro"]);
        await Wait.UntilAsync(() => _ui.Read(() => vm.Devices[0].Tags.Any(t => t.Name == "consigna" && t.Writable)), "tag renombrado en la lista");
    }

    [Fact]
    public async Task History_trigger_is_configured_in_the_tag_window()
    {
        await using var agent = new AgentFactory(Device("plc1", tags: [T("DB1.DBW0", "velocidad"), T("DB1.DBX2.0", "marcha"), T("DB1.DBW4", "modo")]));
        var plc = agent.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 5;
        plc.Values["DB1.DBX2.0"] = true;
        plc.Values["DB1.DBW4"] = 2;
        await using var vm = await ConnectAsync(agent);
        await Wait.UntilAsync(() => _ui.Read(() => TagOf(vm, "plc1", "marcha").IsBoolean), "tipo del tag disparo");

        await vm.OpenTagDetailsCommand.ExecuteAsync(TagOf(vm, "plc1", "velocidad"));
        var details = _dialogs.TagDetails.Single();
        details.TriggerTags.ShouldBe(["marcha", "modo"]);
        details.HistoryEnabled = true;
        details.TriggerEnabled = true;
        details.TriggerTag = "marcha";
        details.IsBooleanTrigger.ShouldBeTrue();
        details.TriggerBool = TagDetailsViewModel.BoolChoices[1];
        details.TriggerHoldMs = 3000;
        await details.SaveCommand.ExecuteAsync(null);
        details.Saved.ShouldBeTrue(details.Errors);

        var trigger = (await ConfigurationFileLoad(agent)).Devices[0].ScanGroups.SelectMany(g => g.Tags).Single(t => t.Name == "velocidad").History!.Trigger!;
        (trigger.Tag, trigger.Condition, trigger.Value, trigger.HoldMs).ShouldBe(("marcha", PlcMonitor.Engine.Configuration.TriggerCondition.Equal, "false", 3000));

        // Al volver a abrir se ve lo guardado; un tag numérico pide condición y valor.
        await Wait.UntilAsync(() => _ui.Read(() => TagOf(vm, "plc1", "velocidad").Historized), "reconfigurado");
        await vm.OpenTagDetailsCommand.ExecuteAsync(TagOf(vm, "plc1", "velocidad"));
        var again = _dialogs.TagDetails.Last();
        (again.TriggerEnabled, again.TriggerTag, again.TriggerOnTrue, again.TriggerHoldMs).ShouldBe((true, "marcha", false, 3000));
        again.TriggerTag = "modo";
        again.IsBooleanTrigger.ShouldBeFalse();
        again.TriggerCondition = TagDetailsViewModel.Conditions.Single(c => c.Text == "≥");
        again.TriggerValue = "x";
        await again.SaveCommand.ExecuteAsync(null);
        again.Saved.ShouldBeFalse();
        again.Errors.ShouldContain("debe ser un número");
        again.TriggerValue = "3";
        await again.SaveCommand.ExecuteAsync(null);
        again.Saved.ShouldBeTrue(again.Errors);
        trigger = (await ConfigurationFileLoad(agent)).Devices[0].ScanGroups.SelectMany(g => g.Tags).Single(t => t.Name == "velocidad").History!.Trigger!;
        (trigger.Tag, trigger.Condition, trigger.Value).ShouldBe(("modo", PlcMonitor.Engine.Configuration.TriggerCondition.GreaterOrEqual, "3"));
    }

    [Fact]
    public async Task History_is_configured_in_the_tag_window_and_queried_in_the_history_tab()
    {
        await using var agent = new AgentFactory(Device("plc1", tags: [T("DB1.DBW0", "velocidad"), T("DB1.DBW2", "otro")]));
        var plc = agent.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 5;
        plc.Values["DB1.DBW2"] = 1;
        await using var vm = await ConnectAsync(agent);
        vm.HistorianEnabled.ShouldBeTrue();
        _ui.Read(() => vm.History.HasHistorizedTags).ShouldBeFalse();

        // Activar el histórico desde la ventana del tag.
        await vm.OpenTagDetailsCommand.ExecuteAsync(TagOf(vm, "plc1", "velocidad"));
        var details = _dialogs.TagDetails.Single();
        details.HistorianEnabled.ShouldBeTrue();
        details.HistoryEnabled.ShouldBeFalse();
        details.HistoryEnabled = true;
        details.HistoryMaxIntervalMs = 60_000;
        details.HistoryDeadband = 2;
        details.HistoryDeadbandPercent = true;
        await details.SaveCommand.ExecuteAsync(null);
        details.Saved.ShouldBeTrue(details.Errors);
        var history = (await ConfigurationFileLoad(agent)).Devices[0].ScanGroups.SelectMany(g => g.Tags).Single(t => t.Name == "velocidad").History!;
        (history.Enabled, history.MaxIntervalMs, history.Deadband, history.DeadbandMode).ShouldBe((true, 60_000, 2.0, PlcMonitor.Engine.Configuration.DeadbandMode.Percent));

        await Wait.UntilAsync(() => _ui.Read(() => vm.History.Tags.Select(t => t.Name).SequenceEqual(["velocidad"])), "tag historizado en la pestaña");
        _ui.Read(() => TagOf(vm, "plc1", "velocidad").Historized).ShouldBeTrue();
        await Wait.UntilAsync(() => _ui.Read(() => TagOf(vm, "plc1", "velocidad").IsGood), "valor tras reconfigurar");
        plc.Values["DB1.DBW0"] = 50;
        await Wait.UntilAsync(() => _ui.Read(() => TagOf(vm, "plc1", "velocidad").ValueText) == "50", "cambio");

        // Consulta en bruto, agregada y exportación.
        await vm.History.QueryCommand.ExecuteAsync(null);
        vm.History.Rows.Where(r => r.IsGood).Select(r => r.Value).ShouldBe(["50", "5"]);
        vm.History.Summary.ShouldContain("puntos de 1 tags");

        vm.History.Aggregate = HistoryViewModel.Aggregates.Single(a => a.Text == "Máximo");
        vm.History.Interval = HistoryViewModel.Intervals.Single(i => i.Text == "1 día");
        vm.History.IsAggregated.ShouldBeTrue();
        await vm.History.QueryCommand.ExecuteAsync(null);
        vm.History.Rows.ShouldAllBe(r => r.Value == "50");

        // Barras por periodo de una vista de tendencia: máximo por hora del tag historizado; el otro no tiene histórico.
        var barsView = new PlcMonitor.App.Services.TrendView
        {
            Kind = PlcMonitor.App.Charts.ChartKind.Bars, BarMetric = PlcMonitor.App.Charts.BarMetric.Max, BarPeriod = PlcMonitor.App.Charts.BarPeriod.Hour,
            Tags = [new("plc1", "velocidad"), new("plc1", "otro")],
        };
        var bars = new PeriodBarsViewModel(vm, barsView, t => vm.FindTag(t.Device, t.Tag));
        bars.Description.ShouldBe("Máximo por hora · hoy y ayer");
        await bars.RefreshCommand.ExecuteAsync(null);
        bars.Missing.ShouldBe("Sin histórico en el agente: plc1 / otro");
        var barSeries = bars.Chart.Options.ShouldHaveSingleItem();
        barSeries.Name.ShouldBe("plc1 / velocidad");
        bars.Chart.Series.Single().Points[^1].Value.ShouldBe(50);
        bars.Summary.ShouldStartWith("1 periodo");

        // Vista con datos del histórico: histograma de la última hora; el tag sin histórico se avisa.
        using var historyTrends = new TrendsViewModel(vm, Path.Combine(agent.Directory, "trends.json"));
        historyTrends.NewCommand.Execute(null);
        historyTrends.Tags.Add(new("plc1", "velocidad"));
        historyTrends.Tags.Add(new("plc1", "otro"));
        historyTrends.Kind = ChartKindChoice.For(PlcMonitor.App.Charts.ChartKind.Histogram);
        historyTrends.DataSource = TrendsViewModel.DataSources[1];
        historyTrends.HistoryRange = TrendHistory.Ranges.Single(r => r.Value == 60);
        await historyTrends.OpenCommand.ExecuteAsync(null);
        historyTrends.KindError.ShouldBeEmpty();
        var fromHistory = historyTrends.Live.ShouldNotBeNull();
        (fromHistory.IsLive, fromHistory.UsesWindow).ShouldBe((false, false));
        fromHistory.HistoryText.ShouldStartWith("Histórico última hora · ");
        fromHistory.Missing.ShouldBe("Sin histórico en el agente: plc1 / otro");
        fromHistory.Chart.Series.Single().Points.Where(p => p.Value is not null).Select(p => p.Value).ShouldBe([5, 50]);
        (fromHistory.Chart.To - fromHistory.Chart.From).ShouldBe(TimeSpan.FromHours(1));

        // Desde la pestaña Histórico: «Abrir en Tendencias» crea la vista con los tags marcados y el periodo.
        vm.History.Range = HistoryViewModel.Ranges.Single(r => r.Text == "Última hora");
        vm.History.OpenInTrendsCommand.Execute(null);
        await Wait.UntilAsync(() => vm.Trends?.Live is not null, "vista abierta en Tendencias");
        var opened = vm.Trends!;
        opened.Selected!.Name.ShouldBe("plc1 · última hora");
        opened.Selected.View.Tags.ShouldBe([new("plc1", "velocidad")]);
        (opened.Selected.View.DataSource, opened.Selected.View.HistoryMinutes).ShouldBe((PlcMonitor.App.Charts.TrendDataSource.History, 60));
        opened.Live!.Chart.Series.Single().Points.Where(p => p.Value is not null).Select(p => p.Value).ShouldBe([5, 50]);
        vm.CloseTrendsCommand.Execute(null);

        var csv = Path.Combine(agent.Directory, "export.csv");
        _dialogs.SavePath = csv;
        await vm.History.ExportCommand.ExecuteAsync(null);
        File.ReadAllLines(csv)[0].ShouldBe("device,tag,timestamp,value,quality");
        File.ReadAllLines(csv).Skip(1).ShouldAllBe(l => l.StartsWith("plc1,velocidad,", StringComparison.Ordinal));

        vm.History.Tags.Single().IsSelected = false;
        await vm.History.QueryCommand.ExecuteAsync(null);
        vm.History.Summary.ShouldBe("Marca al menos un tag.");
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
    public async Task Agent_version_is_read_again_after_resubscribing()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false));
        await using var vm = await ConnectAsync(agent);
        var description = _ui.Read(() => vm.AgentDescription);
        description.ShouldContain(Environment.MachineName);

        // Tras actualizar el agente (se reinicia), la app se vuelve a suscribir y recibe una foto nueva.
        _ui.Read(() => vm.AgentDescription = "versión anterior");
        _ui.Read(() =>
        {
            vm.Apply(new SubscribeEvent { Snapshot = new Snapshot() });
            return true;
        });

        await Wait.UntilAsync(() => _ui.Read(() => vm.AgentDescription) == description, "versión leída de nuevo");
    }

    [Fact]
    public async Task Renaming_a_historized_tag_or_device_offers_to_take_its_history()
    {
        var historized = new Tag { Address = "DB1.DBW0", Name = "velocidad", History = new Engine.Configuration.TagHistorySettings { Enabled = true } };
        await using var agent = new AgentFactory(Device("plc1", tags: [historized, T("DB1.DBW2", "otro")]));
        var plc = agent.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 10;
        plc.Values["DB1.DBW2"] = 1;
        await using var vm = await ConnectAsync(agent);
        await Wait.UntilAsync(() => _ui.Read(() => TagOf(vm, "plc1", "velocidad").IsGood), "valor inicial");
        var client = agent.CreateGrpcClient();
        async Task<double[]> Good(string device, string tag) =>
            [.. (await client.QueryHistoryAsync(new HistoryRequest { Device = device, Tags = { tag } })).Series.Single().Points
                .Where(p => p.Quality == Quality.Good).Select(p => p.Number)];

        // Tag no historizado: no se pregunta.
        await vm.OpenTagDetailsCommand.ExecuteAsync(TagOf(vm, "plc1", "otro"));
        var details = _dialogs.TagDetails.Last();
        details.Name = "otro2";
        await details.SaveCommand.ExecuteAsync(null);
        details.Saved.ShouldBeTrue(details.Errors);
        _dialogs.Questions.ShouldBeEmpty();

        await Wait.UntilAsync(() => _ui.Read(() => vm.Devices[0].Tags.Any(t => t.Name == "otro2")), "tag renombrado");
        await vm.OpenTagDetailsCommand.ExecuteAsync(TagOf(vm, "plc1", "velocidad"));
        details = _dialogs.TagDetails.Last();
        details.Name = "rapidez";
        await details.SaveCommand.ExecuteAsync(null);

        details.Saved.ShouldBeTrue(details.Errors);
        _dialogs.Questions.ShouldHaveSingleItem().ShouldContain("¿Llevarlo a «rapidez»?");
        (await Good("plc1", "rapidez")).ShouldBe([10.0]);
        (await Good("plc1", "velocidad")).ShouldBeEmpty();

        // Renombrar el dispositivo, sin llevarse el histórico.
        await Wait.UntilAsync(() => _ui.Read(() => vm.Devices[0].Tags.Any(t => t.Name == "rapidez")), "tag renombrado");
        _ui.Read(() => vm.SelectedDevice = vm.Devices[0]);
        await vm.EditDeviceCommand.ExecuteAsync(null);
        var editor = _ui.Read(() => vm.Editor!);
        editor.Name = "prensa";
        _dialogs.Answer = false;
        await editor.SaveCommand.ExecuteAsync(null);

        _dialogs.Questions.Count.ShouldBe(2);
        _dialogs.Questions[1].ShouldContain("los tags de «plc1»");
        (await Good("plc1", "rapidez")).ShouldBe([10.0]);
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

    public void Dispose()
    {
        File.Delete(_settingsPath);
        File.Delete(_profilesPath);
    }
}
