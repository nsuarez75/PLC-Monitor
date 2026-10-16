using Google.Protobuf.WellKnownTypes;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Services;
using PlcMonitor.App.Charts;
using PlcMonitor.App.ViewModels;

namespace PlcMonitor.App.Tests;

/// <summary>Vistas de tendencia: se guardan en trends.json, se configuran con tags del agente y se abren en vivo.</summary>
public sealed class TrendsViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("plcmon-trends-").FullName;

    private string SettingsPath => Path.Combine(_dir, "app.json");

    private string TrendsPath => Path.Combine(_dir, "trends.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Store_round_trips_and_ignores_a_broken_file()
    {
        TrendViewStore.PathNextTo(SettingsPath).ShouldBe(TrendsPath);
        TrendViewStore.Load(TrendsPath).ShouldBeEmpty();

        TrendViewStore.Save(TrendsPath, [new TrendView { Name = "A", Agent = "x", Tags = [new("plc1", "v")], WindowMinutes = 15, AutoY = false, YMin = -1, YMax = 10 }]);
        var view = TrendViewStore.Load(TrendsPath).ShouldHaveSingleItem();
        (view.Name, view.Agent, view.WindowMinutes, view.AutoY, view.YMin, view.YMax).ShouldBe(("A", "x", 15, false, -1, 10));
        view.Tags.ShouldBe([new TrendTagRef("plc1", "v")]);

        File.WriteAllText(TrendsPath, "{ no es json");
        TrendViewStore.Load(TrendsPath).ShouldBeEmpty();
    }

    [Fact]
    public void A_new_view_gets_tags_from_any_device_and_is_saved()
    {
        var main = MainWindowTests.ViewModelWithData(SettingsPath);
        using var trends = new TrendsViewModel(main, TrendsPath);
        trends.HasViews.ShouldBeFalse();

        trends.NewCommand.Execute(null);
        trends.Selected.ShouldNotBeNull().Name.ShouldBe("Tendencia 1");
        trends.IsEditing.ShouldBeTrue();
        trends.Available.Select(c => c.Text).ShouldBe(["casa1200 / clock", "casa1200 / peso", "casa1200 / malo"]);

        trends.Search = "pes";
        trends.Available.ShouldHaveSingleItem().Tag.ShouldBe("peso");
        trends.AddTagCommand.Execute(trends.Available[0]);
        trends.Available.ShouldBeEmpty();
        trends.Search = "";
        trends.Available.Select(c => c.Tag).ShouldBe(["clock", "malo"]);
        trends.AddTagCommand.Execute(trends.Available[0]);
        trends.RemoveTagCommand.Execute(new TrendTagRef("casa1200", "clock"));
        trends.Tags.ShouldBe([new TrendTagRef("casa1200", "peso")]);

        trends.Name = "Báscula";
        trends.Window = LiveTrendViewModel.Windows[2];
        trends.AutoY = false;
        trends.YMin = 0;
        trends.YMax = 20;
        trends.SaveCommand.Execute(null);

        trends.Selected.Name.ShouldBe("Báscula");
        trends.Selected.Description.ShouldBe("1 tag · 15 min");
        var saved = TrendViewStore.Load(TrendsPath).ShouldHaveSingleItem();
        (saved.Name, saved.Agent, saved.WindowMinutes, saved.AutoY, saved.YMin, saved.YMax).ShouldBe(("Báscula", main.AgentKey, 15, false, 0, 20));
        saved.Tags.ShouldBe([new TrendTagRef("casa1200", "peso")]);
    }

    [Fact]
    public void Empty_name_cannot_be_saved()
    {
        var main = MainWindowTests.ViewModelWithData(SettingsPath);
        using var trends = new TrendsViewModel(main, TrendsPath);
        trends.SaveCommand.CanExecute(null).ShouldBeFalse(); // sin vista
        trends.NewCommand.Execute(null);
        trends.Name = "  ";
        trends.SaveCommand.CanExecute(null).ShouldBeFalse();
        trends.OpenCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void Only_the_views_of_the_current_agent_are_listed()
    {
        var main = MainWindowTests.ViewModelWithData(SettingsPath);
        TrendViewStore.Save(TrendsPath,
        [
            new TrendView { Name = "Mía", Agent = main.AgentKey },
            new TrendView { Name = "De otro", Agent = "otro-agente" },
        ]);
        using var trends = new TrendsViewModel(main, TrendsPath);

        trends.Views.ShouldHaveSingleItem().Name.ShouldBe("Mía");
        trends.NewCommand.Execute(null);
        // La del otro agente se conserva al guardar.
        TrendViewStore.Load(TrendsPath).Select(v => v.Name).ShouldBe(["Mía", "De otro", "Tendencia 2"]);
    }

    [Fact]
    public void Opening_draws_the_tags_and_reports_the_missing_ones()
    {
        var main = MainWindowTests.ViewModelWithData(SettingsPath);
        TrendViewStore.Save(TrendsPath,
        [
            new TrendView { Name = "Mezcla", Agent = main.AgentKey, WindowMinutes = 1, AutoY = false, YMin = 5, YMax = 15, Tags = [new("casa1200", "peso"), new("casa1200", "borrado")] },
        ]);
        using var trends = new TrendsViewModel(main, TrendsPath);
        trends.Tags.Count.ShouldBe(2);

        trends.OpenCommand.Execute(null);

        var live = trends.Live.ShouldNotBeNull();
        trends.IsViewing.ShouldBeTrue();
        trends.IsEditing.ShouldBeFalse();
        live.Window.Value.ShouldBe(TimeSpan.FromMinutes(1));
        live.Chart.Options.Select(o => o.Name).ShouldBe(["casa1200 / peso"]);
        live.Chart.Options[0].IsSelected.ShouldBeTrue();
        (live.Chart.YMin, live.Chart.YMax).ShouldBe((5, 15));
        live.Missing.ShouldBe("No disponibles en el agente: casa1200 / borrado");

        // El agente añade el tag: la gráfica abierta lo recoge.
        var configured = new DeviceConfigured { Device = new Device { Name = "casa1200", Driver = "s7-symbolic", Enabled = true } };
        configured.Tags.Add(new Tag { Device = "casa1200", Name = "peso", Address = "Viruta_Datos.Viruta.Peso" });
        configured.Tags.Add(new Tag { Device = "casa1200", Name = "borrado", Address = "DB1.x", Value = new TagValue { DataType = DataType.Float, Timestamp = Timestamp.FromDateTime(DateTime.UtcNow) } });
        main.Apply(new SubscribeEvent { DeviceConfigured = configured });

        live.Missing.ShouldBeEmpty();
        live.Chart.Options.Count.ShouldBe(2);

        trends.EditCommand.Execute(null);
        trends.Live.ShouldBeNull();
        trends.IsEditing.ShouldBeTrue();
    }

    [Fact]
    public void A_trigger_view_is_saved_and_shows_only_the_triggered_spans()
    {
        var main = MainWindowTests.ViewModelWithData(SettingsPath);
        using var trends = new TrendsViewModel(main, TrendsPath);
        trends.NewCommand.Execute(null);
        trends.AddTagCommand.Execute(trends.Available.Single(c => c.Tag == "peso"));
        trends.TriggerEnabled = true;
        trends.SaveCommand.Execute(null);
        trends.TriggerError.ShouldBe("Elige el tag de disparo.");

        trends.TriggerTag = trends.TriggerChoices.Single(c => c.Tag == "clock");
        trends.IsBooleanTrigger.ShouldBeTrue();
        trends.TriggerBeforeSeconds = 2;
        trends.TriggerHoldSeconds = 3;
        trends.SaveCommand.Execute(null);
        trends.TriggerError.ShouldBeEmpty();
        var saved = TrendViewStore.Load(TrendsPath).Single().Trigger.ShouldNotBeNull();
        (saved.Device, saved.Tag, saved.Condition, saved.Value, saved.BeforeSeconds, saved.HoldSeconds)
            .ShouldBe(("casa1200", "clock", TriggerCondition.Equal, "true", 2, 3));
        File.ReadAllText(TrendsPath).ShouldContain("\"Equal\"");
        trends.Selected!.Description.ShouldEndWith("· disparo");

        // Un tag numérico pide un número.
        trends.TriggerTag = trends.TriggerChoices.Single(c => c.Tag == "peso");
        trends.IsValueTrigger.ShouldBeTrue();
        trends.TriggerValue = "mucho";
        trends.OpenCommand.Execute(null);
        trends.Live.ShouldBeNull();
        trends.TriggerError.ShouldContain("número");
        trends.TriggerTag = trends.TriggerChoices.Single(c => c.Tag == "clock");

        // clock: true de 0 a 10 s y de nuevo desde 40 s; peso cambia cada 5 s.
        var start = DateTimeOffset.UtcNow.AddSeconds(5);
        var updates = new TagUpdates();
        foreach (var (at, on) in new[] { (0, true), (10, false), (40, true) })
            updates.Updates.Add(new TagUpdate { Device = "casa1200", Tag = "clock", Value = new TagValue { Quality = Quality.Good, DataType = DataType.Boolean, BoolValue = on, Timestamp = Timestamp.FromDateTimeOffset(start.AddSeconds(at)) } });
        for (var at = 0; at <= 50; at += 5)
            updates.Updates.Add(new TagUpdate { Device = "casa1200", Tag = "peso", Value = new TagValue { Quality = Quality.Good, DataType = DataType.Float, DoubleValue = at, Timestamp = Timestamp.FromDateTimeOffset(start.AddSeconds(at)) } });
        main.Apply(new SubscribeEvent { TagUpdates = updates });

        trends.OpenCommand.Execute(null);
        var live = trends.Live.ShouldNotBeNull();
        live.IsTriggered.ShouldBeTrue();
        live.TriggerText.ShouldBe("Disparo: casa1200 / clock = true (−2 s +3 s)");
        live.Refresh(start.AddSeconds(50));
        live.TriggerActive.ShouldBeTrue();
        var shown = live.Chart.Options.Single().Points;
        // Tramo 1: de −2 s a 13 s (hueco al final); tramo 2: desde 38 s, abierto.
        shown[0].Time.ShouldBe(start.AddSeconds(-2));
        // (El valor de la foto inicial de la prueba no trae número: el tramo empieza con hueco hasta el primer valor.)
        shown.Where(p => p.Value is null).Select(p => p.Time).ShouldBe([start.AddSeconds(-2), start.AddSeconds(13)]);
        shown.Where(p => p.Value is not null).Select(p => p.Value).ShouldBe([0, 5, 10, 35, 40, 45, 50]);
    }

    [Fact]
    public async Task Deleting_asks_first()
    {
        var main = MainWindowTests.ViewModelWithData(SettingsPath);
        var dialogs = (FakeDialogs)main.Dialogs;
        using var trends = new TrendsViewModel(main, TrendsPath);
        trends.NewCommand.Execute(null);

        dialogs.Answer = false;
        await trends.DeleteCommand.ExecuteAsync(null);
        trends.Views.Count.ShouldBe(1);

        dialogs.Answer = true;
        await trends.DeleteCommand.ExecuteAsync(null);
        trends.Views.ShouldBeEmpty();
        trends.Selected.ShouldBeNull();
        TrendViewStore.Load(TrendsPath).ShouldBeEmpty();
    }
}
