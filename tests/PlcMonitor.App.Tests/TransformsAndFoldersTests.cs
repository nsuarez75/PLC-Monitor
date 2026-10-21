using PlcMonitor.Agent.Contracts;
using PlcMonitor.Agent.Tests;
using PlcMonitor.App.Services;
using PlcMonitor.App.ViewModels;
using PlcMonitor.Engine.Configuration;
using static PlcMonitor.Agent.Tests.AgentFactory;
using Tag = PlcMonitor.Engine.Configuration.TagConfiguration;

namespace PlcMonitor.App.Tests;

/// <summary>Transformaciones de valores y grupos plegables de la tabla de tags, contra el agente en memoria.</summary>
public sealed class TransformsAndFoldersTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"plcmon-app-{Guid.NewGuid():N}.json");
    private readonly string _profilesPath = Path.Combine(Path.GetTempPath(), $"plcmon-agents-{Guid.NewGuid():N}.json");
    private readonly LockingDispatcher _ui = new();
    private readonly FakeDialogs _dialogs = new();

    public TransformsAndFoldersTests()
    {
        MainViewModel.ResubscribeDelay = TimeSpan.FromMilliseconds(100);
    }

    private static Tag T(string address, string name, string? transform = null, string? folder = null) =>
        new() { Address = address, Name = name, Transform = transform, Folder = folder };

    private static Engine.Configuration.ValueTransform Estados() => new()
    {
        Name = "Estados",
        Entries = [new Engine.Configuration.TransformEntry { Value = 0, Text = "Parado" }, new Engine.Configuration.TransformEntry { Value = 1, Text = "Marcha" }],
    };

    private static AgentFactory Agent(params Tag[] tags) => new(new AgentConfiguration
    {
        Transforms = [Estados()],
        Devices = [Device("plc1", tags: tags)],
    });

    private async Task<MainViewModel> ConnectAsync(AgentFactory agent, AppSettings? settings = null)
    {
        var vm = new MainViewModel(new InMemoryConnector(agent), _ui, _dialogs, settings ?? new AppSettings(), _settingsPath, _profilesPath);
        await vm.ConnectCommand.ExecuteAsync(null);
        vm.IsConnected.ShouldBeTrue(vm.Message);
        await Wait.UntilAsync(() => _ui.Read(() => vm.IsLive), "suscripción en vivo");
        return vm;
    }

    /// <summary>Ejecuta en el «hilo de la UI» (con el cerrojo del despachador de prueba).</summary>
    private void Do(Action action) => _ui.Read(() =>
    {
        action();
        return true;
    });

    private TagViewModel TagOf(MainViewModel vm, string tag) => _ui.Read(() => vm.Devices[0].Tags.Single(t => t.Name == tag));

    [Fact]
    public async Task Transformed_values_show_text_and_number_and_follow_changes_of_the_transform()
    {
        await using var agent = Agent(T("DB1.DBW0", "motor", transform: "Estados"), T("DB1.DBW2", "otro"));
        var plc = agent.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 1;
        plc.Values["DB1.DBW2"] = 1;
        await using var vm = await ConnectAsync(agent);

        await Wait.UntilAsync(() => _ui.Read(() => TagOf(vm, "motor").DisplayText) == "Marcha (1)", "valor transformado");
        _ui.Read(() => TagOf(vm, "otro").DisplayText).ShouldBe("1");
        plc.Values["DB1.DBW0"] = 7;
        await Wait.UntilAsync(() => _ui.Read(() => TagOf(vm, "motor").DisplayText) == "7", "valor sin texto: tal cual");

        var request = new PutTransformsRequest();
        var transform = new Agent.Contracts.ValueTransform { Name = "Estados" };
        transform.Entries.Add(new Agent.Contracts.TransformEntry { Value = 7, Text = "Fallo" });
        request.Transforms.Add(transform);
        await agent.CreateGrpcClient().PutTransformsAsync(request);

        await Wait.UntilAsync(() => _ui.Read(() => TagOf(vm, "motor").DisplayText) == "Fallo (7)", "transformación cambiada en el agente");
    }

    [Fact]
    public async Task Unit_and_decimals_show_in_the_table_and_are_edited_in_the_tag_details()
    {
        await using var agent = Agent(
            new Tag { Address = "REAL0", Name = "presion", Unit = "bar", Decimals = 2 },
            new Tag { Address = "DB1.DBW2", Name = "contador", Unit = "piezas", Decimals = 2 });
        var plc = agent.Driver.Plc("plc1");
        plc.Values["REAL0"] = 23.4500007629395;
        plc.Values["DB1.DBW2"] = 5;
        await using var vm = await ConnectAsync(agent);

        await Wait.UntilAsync(() => _ui.Read(() => TagOf(vm, "presion").DisplayText) == "23.45 bar", "real con unidad y decimales");
        _ui.Read(() => TagOf(vm, "contador").DisplayText).ShouldBe("5 piezas"); // los enteros no llevan decimales
        _ui.Read(() => TagOf(vm, "contador").SeriesFormat).ShouldBe(new PlcMonitor.App.Charts.SeriesFormat("piezas", null));
        _ui.Read(() => TagOf(vm, "presion").Label).ShouldBe("presion [bar]");

        await vm.OpenTagDetailsCommand.ExecuteAsync(TagOf(vm, "presion"));
        var details = _dialogs.TagDetails.Single();
        (details.Unit, details.SelectedDecimals).ShouldBe(("bar", "2"));
        details.DisplayPreview.ShouldStartWith("Se verá como 23.45 bar.");
        details.Unit = " °C ";
        details.SelectedDecimals = "1";
        details.DisplayPreview.ShouldStartWith("Se verá como 23.5 °C.");
        await details.SaveCommand.ExecuteAsync(null);
        details.Errors.ShouldBeEmpty();

        await Wait.UntilAsync(() => _ui.Read(() => TagOf(vm, "presion").DisplayText) == "23.5 °C", "unidad y decimales nuevos");
        _ui.Read(() => TagOf(vm, "presion").SeriesFormat).ShouldBe(new PlcMonitor.App.Charts.SeriesFormat("°C", 1));
        var saved = (await agent.CreateGrpcClient().GetDeviceConfigAsync(new DeviceRequest { Device = "plc1" })).ScanGroups[0].Tags.Single(t => t.Name == "presion");
        (saved.Unit, saved.Decimals).ShouldBe(("°C", 1));

        _dialogs.TagDetails.Clear();
        await vm.OpenTagDetailsCommand.ExecuteAsync(TagOf(vm, "presion"));
        details = _dialogs.TagDetails.Single();
        details.SelectedDecimals = TagDetailsViewModel.AllDecimals;
        await details.SaveCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => _ui.Read(() => TagOf(vm, "presion").DisplayText) == "23.4500007629395 °C", "valor completo sin decimales fijos");
    }

    [Fact]
    public async Task Tags_are_grouped_in_folders_that_collapse_and_remember_it()
    {
        await using var agent = Agent(T("DB1.DBW0", "a", folder: "Horno"), T("DB1.DBW2", "b"), T("DB1.DBW4", "c", folder: "Cinta 1"), T("DB1.DBW6", "d", folder: "horno"));
        var settings = new AppSettings();
        await using var vm = await ConnectAsync(agent, settings);
        var device = _ui.Read(() => vm.Devices[0]);

        string Describe() => _ui.Read(() => string.Join(" ", device.Rows.Select(r => r is TagFolderViewModel f ? $"[{f.Title}]" : ((TagViewModel)r).Name)));
        Describe().ShouldBe("[Cinta 1] c [Horno] a d [Sin grupo] b");
        device.Folders.ShouldBe(["Cinta 1", "Horno"]);

        var horno = _ui.Read(() => device.Rows.OfType<TagFolderViewModel>().Single(f => f.Name == "Horno"));
        Do(() => horno.ToggleCommand.Execute(null));
        Describe().ShouldBe("[Cinta 1] c [Horno] [Sin grupo] b");
        settings.CollapsedFolders.ShouldHaveSingleItem().ShouldEndWith("/plc1/Horno");

        // Se sigue plegado al rehacer la tabla (p.ej. al cambiar la configuración del dispositivo).
        Do(device.RebuildRows);
        Describe().ShouldBe("[Cinta 1] c [Horno] [Sin grupo] b");
        Do(() => device.SetAllFoldersCommand.Execute("true"));
        Describe().ShouldBe("[Cinta 1] c [Horno] a d [Sin grupo] b");
        settings.CollapsedFolders.ShouldBeEmpty();
        Do(() => device.SetAllFoldersCommand.Execute("false"));
        Describe().ShouldBe("[Cinta 1] [Horno] [Sin grupo]");
    }

    [Fact]
    public async Task Without_folders_the_table_is_the_plain_list_and_tags_can_be_moved_to_a_folder()
    {
        await using var agent = Agent(T("DB1.DBW0", "a"), T("DB1.DBW2", "b"), T("DB1.DBW4", "c"));
        await using var vm = await ConnectAsync(agent);
        var device = _ui.Read(() => vm.Devices[0]);
        _ui.Read(() => device.Rows.Cast<TagViewModel>().Select(t => t.Name).ToArray()).ShouldBe(["a", "b", "c"]);

        _dialogs.PromptAnswer = " Bombas ";
        await device.MoveToNewFolderAsync([TagOf(vm, "a"), TagOf(vm, "c")]);

        await Wait.UntilAsync(() => _ui.Read(() => vm.Devices[0].Rows.OfType<TagFolderViewModel>().Any()), "grupo creado");
        var rows = _ui.Read(() => vm.Devices[0].Rows.Select(r => r is TagFolderViewModel f ? $"[{f.Title}]" : ((TagViewModel)r).Name).ToArray());
        rows.ShouldBe(["[Bombas]", "a", "c", "[Sin grupo]", "b"]);
        var saved = (await ConfigurationFile.LoadAsync(agent.ConfigPath)).Devices[0].ScanGroups[0].Tags;
        saved.Select(t => t.Folder).ShouldBe(["Bombas", null, "Bombas"]);

        await vm.Devices[0].MoveToFolderAsync([TagOf(vm, "a"), TagOf(vm, "c")], "");
        await Wait.UntilAsync(() => _ui.Read(() => !vm.Devices[0].Rows.OfType<TagFolderViewModel>().Any()), "sin grupos de nuevo");
    }

    [Fact]
    public async Task Tag_details_assign_transform_and_folder_and_keep_them_when_saving_other_changes()
    {
        await using var agent = Agent(T("DB1.DBW0", "motor"), T("DB1.DBW2", "otro", folder: "Horno"));
        agent.Driver.Plc("plc1").Values["DB1.DBW0"] = 0;
        await using var vm = await ConnectAsync(agent);

        await vm.OpenTagDetailsCommand.ExecuteAsync(TagOf(vm, "motor"));
        var details = _dialogs.TagDetails.Last();
        details.Transforms.ShouldBe([TagDetailsViewModel.NoTransform, "Estados"]);
        details.SelectedTransform.ShouldBe(TagDetailsViewModel.NoTransform);
        details.Folders.ShouldBe(["Horno"]);
        details.SelectedTransform = "Estados";
        details.TransformPreview.ShouldBe("0 = Parado · 1 = Marcha");
        details.Folder = "Horno";
        await details.SaveCommand.ExecuteAsync(null);
        details.Saved.ShouldBeTrue(details.Errors);
        await Wait.UntilAsync(() => _ui.Read(() => vm.Devices[0].Tags.Any(t => t.Name == "motor" && t.DisplayText == "Parado (0)")), "transformación aplicada");

        await vm.OpenTagDetailsCommand.ExecuteAsync(TagOf(vm, "motor"));
        details = _dialogs.TagDetails.Last();
        (details.SelectedTransform, details.Folder).ShouldBe(("Estados", "Horno"));
        details.Writable = true;
        await details.SaveCommand.ExecuteAsync(null);
        details.Saved.ShouldBeTrue(details.Errors);
        var saved = (await ConfigurationFile.LoadAsync(agent.ConfigPath)).Devices[0].ScanGroups[0].Tags.Single(t => t.Name == "motor");
        (saved.Transform, saved.Folder, saved.Writable).ShouldBe(("Estados", "Horno", true));
    }

    [Fact]
    public async Task Transforms_panel_creates_validates_renames_and_protects_transforms_in_use()
    {
        await using var agent = Agent(T("DB1.DBW0", "motor", transform: "Estados"));
        agent.Driver.Plc("plc1").Values["DB1.DBW0"] = 1;
        await using var vm = await ConnectAsync(agent);
        Do(() => vm.OpenTransformsCommand.Execute(null));
        var panel = vm.TransformsPanel!;
        var estados = panel.Items.ShouldHaveSingleItem();
        estados.UsageText.ShouldBe("1 tag (plc1/motor)");
        panel.IsDirty.ShouldBeFalse();

        // Una en uso no se borra.
        await panel.RemoveCommand.ExecuteAsync(null);
        panel.Errors.ShouldContain("la usan 1 tag (plc1/motor)");
        panel.Items.Count.ShouldBe(1);

        // Nueva, con errores que se ven antes de enviar.
        panel.AddCommand.Execute(null);
        var nueva = panel.Selected!;
        nueva.Name.ShouldBe("Nueva transformación");
        nueva.Entries.Select(e => e.Value).ShouldBe(["0", "1"]);
        nueva.Entries[0].Text = "Cerrada";
        nueva.Entries[1].Value = "x";
        nueva.Entries[1].Text = "Abierta";
        await panel.SaveCommand.ExecuteAsync(null);
        panel.Errors.ShouldContain("«x» no es un número entero");
        nueva.Entries[1].Value = "1";
        nueva.AddEntryCommand.Execute(null);
        nueva.Entries[2].Value.ShouldBe("2"); // sin texto: no se guarda
        nueva.Name = "Válvula";

        // Cambio de nombre de la que está en uso: el tag pasa al nombre nuevo.
        estados.Name = "Estado motor";
        await panel.SaveCommand.ExecuteAsync(null);

        panel.Errors.ShouldBeEmpty();
        panel.IsDirty.ShouldBeFalse();
        var file = await ConfigurationFile.LoadAsync(agent.ConfigPath);
        file.Transforms.Select(t => t.Name).Order().ShouldBe(["Estado motor", "Válvula"]);
        file.Transforms.Single(t => t.Name == "Válvula").Entries.Select(e => (e.Value, e.Text)).ShouldBe([(0L, "Cerrada"), (1L, "Abierta")]);
        file.Devices[0].ScanGroups[0].Tags[0].Transform.ShouldBe("Estado motor");
        await Wait.UntilAsync(() => _ui.Read(() => vm.Devices[0].Tags.Single().Transform == "Estado motor" && vm.Devices[0].Tags.Single().DisplayText == "Marcha (1)"),
            "tag con la transformación renombrada");
        await Wait.UntilAsync(() => _ui.Read(() => panel.Items.Count == 2), "panel recargado");
    }

    public void Dispose()
    {
        File.Delete(_settingsPath);
        File.Delete(_profilesPath);
    }
}
