using PlcMonitor.Agent.Tests;
using PlcMonitor.App.Services;
using PlcMonitor.App.ViewModels;
using PlcMonitor.Engine.Configuration;
using static PlcMonitor.Agent.Tests.AgentFactory;
using Tag = PlcMonitor.Engine.Configuration.TagConfiguration;

namespace PlcMonitor.App.Tests;

/// <summary>Acciones de la tabla de tags (menú, grupos, arrastrar y renombrar), contra el agente en memoria.</summary>
public sealed class TagTableTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"plcmon-app-{Guid.NewGuid():N}.json");
    private readonly string _profilesPath = Path.Combine(Path.GetTempPath(), $"plcmon-agents-{Guid.NewGuid():N}.json");
    private readonly LockingDispatcher _ui = new();
    private readonly FakeDialogs _dialogs = new();

    public TagTableTests()
    {
        MainViewModel.ResubscribeDelay = TimeSpan.FromMilliseconds(100);
    }

    private static Tag T(string address, string name, string? folder = null, string? trigger = null) => new()
    {
        Address = address,
        Name = name,
        Folder = folder,
        History = trigger is null ? null : new TagHistorySettings { Enabled = true, Trigger = new HistoryTrigger { Tag = trigger, Value = "1" } },
    };

    private async Task<MainViewModel> ConnectAsync(AgentFactory agent, AppSettings? settings = null)
    {
        var vm = new MainViewModel(new InMemoryConnector(agent), _ui, _dialogs, settings ?? new AppSettings(), _settingsPath, _profilesPath);
        await vm.ConnectCommand.ExecuteAsync(null);
        vm.IsConnected.ShouldBeTrue(vm.Message);
        await Wait.UntilAsync(() => _ui.Read(() => vm.IsLive), "suscripción en vivo");
        return vm;
    }

    private TagViewModel TagOf(MainViewModel vm, string tag) => _ui.Read(() => vm.Devices[0].Tags.Single(t => t.Name == tag));

    private string Rows(MainViewModel vm) =>
        _ui.Read(() => string.Join(" ", vm.Devices[0].Rows.Select(r => r is TagFolderViewModel f ? $"[{f.Title}]" : (((TagViewModel)r).IsInFolder ? "  " : "") + ((TagViewModel)r).Name)));

    [Fact]
    public async Task Tags_in_folders_are_indented_and_plain_lists_are_not()
    {
        await using var agent = new AgentFactory(Device("plc1", tags: [T("DB1.DBW0", "a", folder: "Horno"), T("DB1.DBW2", "b")]));
        await using var vm = await ConnectAsync(agent);
        Rows(vm).ShouldBe("[Horno]   a [Sin grupo]   b");

        await _ui.Read(() => vm.Devices[0]).MoveToFolderAsync([TagOf(vm, "a")], "");
        await Wait.UntilAsync(() => Rows(vm) == "a b", "lista sin grupos ni sangrado");
    }

    [Fact]
    public async Task Several_tags_are_removed_at_once_and_triggers_that_depend_on_them_are_dropped()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false,
            tags: [T("DB1.DBW0", "marcha"), T("DB1.DBW2", "b"), T("DB1.DBW4", "temp", trigger: "marcha"), T("DB1.DBW6", "c")]));
        await using var vm = await ConnectAsync(agent);
        var device = _ui.Read(() => vm.Devices[0]);

        _dialogs.Answer = false;
        await device.RemoveTagsAsync([TagOf(vm, "marcha"), TagOf(vm, "b")]);
        _dialogs.Questions.ShouldHaveSingleItem().ShouldContain("2 tags (marcha, b)");
        _dialogs.Questions[0].ShouldContain("disparo del histórico de temp");
        (await ConfigurationFile.LoadAsync(agent.ConfigPath)).Devices[0].ScanGroups[0].Tags.Count.ShouldBe(4);

        _dialogs.Answer = true;
        await device.RemoveTagsAsync([TagOf(vm, "marcha"), TagOf(vm, "b")]);

        await Wait.UntilAsync(() => _ui.Read(() => vm.Devices[0].Tags.Count) == 2, "tags quitados");
        var saved = (await ConfigurationFile.LoadAsync(agent.ConfigPath)).Devices[0].ScanGroups[0].Tags;
        saved.Select(t => t.Name).ShouldBe(["temp", "c"]);
        saved[0].History!.Enabled.ShouldBeTrue();
        saved[0].History!.Trigger.ShouldBeNull();
        vm.Message.ShouldBe("2 tags quitados de plc1");
    }

    [Fact]
    public async Task Renaming_a_folder_moves_its_tags_and_keeps_it_collapsed()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false,
            tags: [T("DB1.DBW0", "a", folder: "Horno"), T("DB1.DBW2", "b", folder: "horno"), T("DB1.DBW4", "c")]));
        var settings = new AppSettings();
        await using var vm = await ConnectAsync(agent, settings);
        var device = _ui.Read(() => vm.Devices[0]);
        var horno = _ui.Read(() => device.Rows.OfType<TagFolderViewModel>().First());
        _ui.Read(() =>
        {
            horno.ToggleCommand.Execute(null);
            return true;
        });

        _dialogs.PromptAnswer = "  Horno 2 ";
        await device.RenameFolderAsync(horno);

        await Wait.UntilAsync(() => Rows(vm) == "[Horno 2] [Sin grupo]   c", "grupo renombrado y aún plegado");
        settings.CollapsedFolders.ShouldHaveSingleItem().ShouldEndWith("/plc1/Horno 2");
        (await ConfigurationFile.LoadAsync(agent.ConfigPath)).Devices[0].ScanGroups[0].Tags.Select(t => t.Folder).ShouldBe(["Horno 2", "Horno 2", null]);

        // Cancelar o dejar el mismo nombre no cambia nada.
        _dialogs.PromptAnswer = null;
        await device.RenameFolderAsync(_ui.Read(() => vm.Devices[0].Rows.OfType<TagFolderViewModel>().First()));
        Rows(vm).ShouldBe("[Horno 2] [Sin grupo]   c");
    }

    [Fact]
    public async Task Dropping_tags_on_a_row_picks_the_folder_of_that_row()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false,
            tags: [T("DB1.DBW0", "a", folder: "Horno"), T("DB1.DBW2", "b", folder: "Cinta"), T("DB1.DBW4", "c")]));
        await using var vm = await ConnectAsync(agent);
        var (a, b, c) = (TagOf(vm, "a"), TagOf(vm, "b"), TagOf(vm, "c"));
        var folders = _ui.Read(() => vm.Devices[0].Rows.OfType<TagFolderViewModel>().ToDictionary(f => f.Name));

        DeviceViewModel.DropFolder(folders["Horno"], [c]).ShouldBe("Horno");
        DeviceViewModel.DropFolder(b, [a, c]).ShouldBe("Cinta");
        DeviceViewModel.DropFolder(folders[""], [a]).ShouldBe("");
        DeviceViewModel.DropFolder(folders["Horno"], [a]).ShouldBeNull("ya está en ese grupo");
        DeviceViewModel.DropFolder(null, [a]).ShouldBeNull();

        await _ui.Read(() => vm.Devices[0]).MoveToFolderAsync([a, c], "Cinta");
        await Wait.UntilAsync(() => Rows(vm) == "[Cinta]   a   b   c", "todos en Cinta");
        var plain = TagOf(vm, "a");
        _ui.Read(() => plain.IsInFolder).ShouldBeTrue();
    }

    [Fact]
    public async Task Renaming_a_tag_keeps_the_triggers_that_use_it()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false,
            tags: [T("DB1.DBW0", "marcha"), T("DB1.DBW2", "temp", trigger: "marcha"), T("DB1.DBW4", "presion", trigger: "marcha"), T("DB1.DBW6", "c", trigger: "temp")]));
        await using var vm = await ConnectAsync(agent);

        await vm.OpenTagDetailsCommand.ExecuteAsync(TagOf(vm, "marcha"));
        var details = _dialogs.TagDetails.Last();
        details.Name = "en_marcha";
        await details.SaveCommand.ExecuteAsync(null);

        details.Saved.ShouldBeTrue(details.Errors);
        var saved = (await ConfigurationFile.LoadAsync(agent.ConfigPath)).Devices[0].ScanGroups.SelectMany(g => g.Tags).ToDictionary(t => t.Name!);
        saved["temp"].History!.Trigger!.Tag.ShouldBe("en_marcha");
        saved["presion"].History!.Trigger!.Tag.ShouldBe("en_marcha");
        saved["c"].History!.Trigger!.Tag.ShouldBe("temp");
        vm.Message.ShouldContain("2 tags siguen disparándose con él");
    }

    [Fact]
    public async Task Opening_the_history_of_a_tag_selects_only_it_and_queries()
    {
        await using var agent = new AgentFactory(Device("plc1",
            tags: [T("DB1.DBW0", "marcha"), T("DB1.DBW2", "temp", trigger: "marcha"), new Tag { Address = "DB1.DBW4", Name = "presion", History = new TagHistorySettings { Enabled = true } }]));
        agent.Driver.Plc("plc1").Values["DB1.DBW0"] = 1;
        await using var vm = await ConnectAsync(agent);

        await vm.OpenTagHistoryCommand.ExecuteAsync(TagOf(vm, "marcha"));
        vm.DeviceTab.ShouldBe(0);
        vm.Message.ShouldContain("no se guarda en el histórico");

        await vm.OpenTagHistoryCommand.ExecuteAsync(TagOf(vm, "presion"));
        vm.DeviceTab.ShouldBe(MainViewModel.HistoryTab);
        vm.History.Tags.Where(t => t.IsSelected).Select(t => t.Name).ShouldBe(["presion"]);
        vm.History.Summary.ShouldContain("de 1 tags");
    }

    [Fact]
    public async Task History_of_the_checked_tags_is_deleted_between_two_dates_after_confirming()
    {
        await using var agent = new AgentFactory(Device("plc1",
            tags: [new Tag { Address = "DB1.DBW0", Name = "presion", History = new TagHistorySettings { Enabled = true } }, new Tag { Address = "DB1.DBW2", Name = "temp", History = new TagHistorySettings { Enabled = true } }]));
        agent.Driver.Plc("plc1").Values["DB1.DBW0"] = 3;
        agent.Driver.Plc("plc1").Values["DB1.DBW2"] = 4;
        await using var vm = await ConnectAsync(agent);
        await Wait.UntilAsync(() => _ui.Read(() => TagOf(vm, "presion").IsGood && TagOf(vm, "temp").IsGood), "valores leídos");
        await vm.OpenTagHistoryCommand.ExecuteAsync(TagOf(vm, "presion"));
        var history = vm.History;
        for (var i = 0; i < 200 && history.Rows.Count == 0; i++)
        {
            await Task.Delay(50);
            await history.QueryCommand.ExecuteAsync(null);
        }
        history.Rows.Count.ShouldBeGreaterThan(0, "histórico guardado");

        history.ToggleDeleteCommand.Execute(null);
        history.ShowDelete.ShouldBeTrue();
        (history.DeleteFromDate, history.DeleteToDate).ShouldBe((DateTime.Now.AddHours(-1).Date, DateTime.Today));
        _dialogs.Answer = false;
        await history.DeleteCommand.ExecuteAsync(null);
        _dialogs.Questions.Last().ShouldContain("del tag presion de plc1");
        history.Rows.Count.ShouldBeGreaterThan(0);

        _dialogs.Answer = true;
        await history.DeleteCommand.ExecuteAsync(null);

        vm.Message.ShouldContain("datos borrados del histórico del tag presion");
        history.ShowDelete.ShouldBeFalse();
        history.Rows.ShouldBeEmpty("se vuelve a consultar sin lo borrado");
        history.SelectOnly("temp").ShouldBeTrue();
        await history.QueryCommand.ExecuteAsync(null);
        history.Rows.Count.ShouldBeGreaterThan(0, "el otro tag conserva su histórico");

        history.ToggleDeleteCommand.Execute(null);
        (history.DeleteFromDate, history.DeleteToDate) = (DateTime.Today, DateTime.Today);
        (history.DeleteFromTime, history.DeleteToTime) = (TimeSpan.FromHours(10), TimeSpan.FromHours(9));
        await history.DeleteCommand.ExecuteAsync(null);
        history.Summary.ShouldBe("El final del intervalo debe ser posterior al inicio.");
    }

    public void Dispose()
    {
        File.Delete(_settingsPath);
        File.Delete(_profilesPath);
    }
}
