using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Google.Protobuf.WellKnownTypes;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Charts;
using PlcMonitor.App.Services;
using PlcMonitor.App.ViewModels;
using PlcMonitor.App.Views;

namespace PlcMonitor.App.Tests;

/// <summary>Paneles: varias vistas de tendencia en una rejilla, guardados en panels.json.</summary>
public sealed class TrendPanelsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("plcmon-panels-").FullName;

    private string SettingsPath => Path.Combine(_dir, "app.json");

    private string TrendsPath => Path.Combine(_dir, "trends.json");

    private string PanelsPath => Path.Combine(_dir, "panels.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Store_round_trips_clamps_columns_and_ignores_a_broken_file()
    {
        TrendPanelStore.PathNextTo(TrendsPath).ShouldBe(PanelsPath);
        TrendPanelStore.Load(PanelsPath).ShouldBeEmpty();

        TrendPanelStore.Save(PanelsPath, [new TrendPanel { Name = "Línea 3", Agent = "x", Views = ["a", "b"], Columns = 9, SyncZoom = false }]);
        var panel = TrendPanelStore.Load(PanelsPath).ShouldHaveSingleItem();
        (panel.Name, panel.Agent, panel.Columns, panel.SyncZoom).ShouldBe(("Línea 3", "x", TrendPanel.MaxColumns, false));
        panel.Views.ShouldBe(["a", "b"]);

        File.WriteAllText(PanelsPath, "[ roto");
        TrendPanelStore.Load(PanelsPath).ShouldBeEmpty();
    }

    [Fact]
    public void A_new_panel_takes_saved_views_in_order_and_is_saved()
    {
        var (main, trends) = WithViews();
        using var _ = trends;
        trends.HasPanels.ShouldBeFalse();

        trends.NewPanelCommand.Execute(null);

        var item = trends.SelectedPanel.ShouldNotBeNull();
        trends.Selected.ShouldBeNull();
        trends.IsEditingPanel.ShouldBeTrue();
        trends.IsEditing.ShouldBeFalse();
        trends.HeaderTitle.ShouldBe("Panel 1");
        trends.PanelAvailable.Select(v => v.Name).ShouldBe(["Peso", "Reloj", "Estados"]);

        trends.AddPanelViewCommand.Execute(trends.PanelAvailable[0]);
        trends.AddPanelViewCommand.Execute(trends.PanelAvailable[0]);
        trends.MovePanelViewUpCommand.Execute(trends.PanelViews[1]);
        trends.PanelViews.Select(v => v.Name).ShouldBe(["Reloj", "Peso"]);
        trends.PanelAvailable.Select(v => v.Name).ShouldBe(["Estados"]);
        trends.RemovePanelViewCommand.Execute(trends.PanelViews[1]);
        trends.PanelAvailable.Select(v => v.Name).ShouldBe(["Peso", "Estados"]);
        trends.AddPanelViewCommand.Execute(trends.PanelAvailable[0]);

        trends.PanelName = "Báscula y reloj";
        trends.PanelColumns = TrendsViewModel.ColumnChoices[0];
        trends.PanelSyncZoom = false;
        trends.SavePanelCommand.Execute(null);

        item.Name.ShouldBe("Báscula y reloj");
        item.Description.ShouldBe("2 vistas · 1 columna");
        var saved = TrendPanelStore.Load(PanelsPath).ShouldHaveSingleItem();
        (saved.Name, saved.Agent, saved.Columns, saved.SyncZoom).ShouldBe(("Báscula y reloj", main.AgentKey, 1, false));
        saved.Views.ShouldBe(trends.PanelViews.Select(v => v.View.Id).ToList());

        // Elegir una vista deja el panel, y volver al panel recupera su formulario.
        trends.Selected = trends.Views[0];
        trends.SelectedPanel.ShouldBeNull();
        trends.SelectedPanel = trends.Panels[0];
        trends.PanelViews.Select(v => v.Name).ShouldBe(["Reloj", "Peso"]);
        trends.PanelColumns.Value.ShouldBe(1);
    }

    [Fact]
    public void Panel_without_views_cannot_open_and_an_empty_name_cannot_be_saved()
    {
        var (_, trends) = WithViews();
        using var _ = trends;
        trends.NewPanelCommand.Execute(null);

        trends.OpenPanelCommand.Execute(null);
        trends.OpenPanel.ShouldBeNull();
        trends.PanelError.ShouldBe("Añade al menos una vista al panel.");

        trends.PanelName = " ";
        trends.SavePanelCommand.CanExecute(null).ShouldBeFalse();
        trends.OpenPanelCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Deleting_a_view_or_a_panel_updates_panels_json()
    {
        var (_, trends) = WithViews();
        using var _ = trends;
        trends.NewPanelCommand.Execute(null);
        trends.AddPanelViewCommand.Execute(trends.PanelAvailable[0]);
        trends.AddPanelViewCommand.Execute(trends.PanelAvailable[0]);
        trends.SavePanelCommand.Execute(null);

        trends.Selected = trends.Views.Single(v => v.Name == "Peso");
        await trends.DeleteCommand.ExecuteAsync(null);

        TrendPanelStore.Load(PanelsPath).Single().Views.Count.ShouldBe(1);
        trends.Panels[0].Description.ShouldStartWith("1 vista ·");

        trends.SelectedPanel = trends.Panels[0];
        await trends.DeletePanelCommand.ExecuteAsync(null);
        trends.HasPanels.ShouldBeFalse();
        TrendPanelStore.Load(PanelsPath).ShouldBeEmpty();
        TrendViewStore.Load(TrendsPath).Count.ShouldBe(2); // las vistas se quedan
    }

    [Fact]
    public void Opening_a_panel_draws_each_view_and_syncs_the_zoom()
    {
        var (main, trends) = WithViews();
        using var _ = trends;
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var updates = new TagUpdates();
        for (var at = 0; at <= 50; at += 5)
        {
            updates.Updates.Add(new TagUpdate { Device = "casa1200", Tag = "peso", Value = new TagValue { Quality = Quality.Good, DataType = DataType.Float, DoubleValue = at, Timestamp = Timestamp.FromDateTimeOffset(start.AddSeconds(at)) } });
            updates.Updates.Add(new TagUpdate { Device = "casa1200", Tag = "clock", Value = new TagValue { Quality = Quality.Good, DataType = DataType.Boolean, BoolValue = at % 10 == 0, Timestamp = Timestamp.FromDateTimeOffset(start.AddSeconds(at)) } });
        }
        main.Apply(new SubscribeEvent { TagUpdates = updates });
        trends.NewPanelCommand.Execute(null);
        foreach (var available in trends.PanelAvailable.ToArray())
            trends.AddPanelViewCommand.Execute(available);

        trends.OpenPanelCommand.Execute(null);

        var panel = trends.OpenPanel.ShouldNotBeNull();
        trends.IsViewingPanel.ShouldBeTrue();
        trends.IsEditingPanel.ShouldBeFalse();
        panel.Columns.ShouldBe(2);
        panel.Cells.Select(c => c.Title).ShouldBe(["Peso", "Reloj", "Estados"]);
        panel.Cells.ShouldAllBe(c => c.HasChart && !c.HasError);
        panel.Cells[2].Chart!.Kind.ShouldBe(ChartKind.States);
        panel.Cells[0].Chart!.Title.ShouldBe("Peso");
        panel.Refresh(DateTimeOffset.UtcNow);

        // Zoom en una: las otras de tiempo lo siguen; «Ajustar» las devuelve a todas.
        var (from, to) = (start.AddSeconds(10), start.AddSeconds(30));
        panel.Cells[0].Chart!.Zoom(from, to);
        panel.Cells.ShouldAllBe(c => c.Chart!.IsZoomed && c.Chart.From == from && c.Chart.To == to);
        panel.Cells[1].Chart!.ResetZoomCommand.Execute(null);
        panel.Cells.ShouldAllBe(c => !c.Chart!.IsZoomed);

        panel.SyncZoom = false;
        panel.Cells[0].Chart!.Zoom(from, to);
        panel.Cells[1].Chart!.IsZoomed.ShouldBeFalse();

        trends.EditPanelCommand.Execute(null);
        trends.OpenPanel.ShouldBeNull();
        trends.IsEditingPanel.ShouldBeTrue();
    }

    [Fact]
    public void A_missing_csv_shows_its_error_in_its_cell()
    {
        var (main, trends) = WithViews(new TrendView { Name = "Fichero", Agent = "otro", DataSource = TrendDataSource.Csv, CsvPath = Path.Combine(_dir, "no-existe.csv"), Tags = [new("plc", "t")] });
        using var _ = trends;
        trends.NewPanelCommand.Execute(null);
        trends.AddPanelViewCommand.Execute(trends.PanelAvailable.Single(v => v.Name == "Fichero"));

        trends.OpenPanelCommand.Execute(null);

        var cell = trends.OpenPanel.ShouldNotBeNull().Cells.Single();
        cell.HasChart.ShouldBeFalse();
        cell.Error.ShouldStartWith("No se puede leer el CSV");
    }

    [Fact]
    public Task Open_panel_is_shown_in_a_grid_and_exported_to_png() => HeadlessApp.RunAsync(() =>
    {
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var (main, trends) = WithViews();
        var start = DateTimeOffset.UtcNow.AddMinutes(-4);
        var updates = new TagUpdates();
        for (var at = 0; at <= 240; at += 4)
        {
            updates.Updates.Add(new TagUpdate { Device = "casa1200", Tag = "peso", Value = new TagValue { Quality = Quality.Good, DataType = DataType.Float, DoubleValue = 10 + (5 * Math.Sin(at / 20.0)), Timestamp = Timestamp.FromDateTimeOffset(start.AddSeconds(at)) } });
            updates.Updates.Add(new TagUpdate { Device = "casa1200", Tag = "clock", Value = new TagValue { Quality = Quality.Good, DataType = DataType.Boolean, BoolValue = at % 40 < 20, Timestamp = Timestamp.FromDateTimeOffset(start.AddSeconds(at)) } });
        }
        main.Apply(new SubscribeEvent { TagUpdates = updates });
        trends.NewPanelCommand.Execute(null);
        trends.PanelName = "Línea 3";
        foreach (var available in trends.PanelAvailable.ToArray())
            trends.AddPanelViewCommand.Execute(available);
        trends.OpenPanelCommand.Execute(null);
        trends.Refresh(DateTimeOffset.UtcNow);

        var view = new TrendsView { DataContext = trends };
        var window = new Window { Width = 1500, Height = 1000, DataContext = main, Content = view };
        window.Show();
        MainWindowTests.Capture(window, "trends-panel.png");

        window.GetVisualDescendants().OfType<ChartPanel>().Count(p => p.IsEffectivelyVisible).ShouldBe(3);
        window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.UniformGrid>().Single(g => g.IsEffectivelyVisible).Columns.ShouldBe(2);

        using var image = view.RenderPanelImage();
        image.ShouldNotBeNull();
        // Dos columnas y dos filas de gráficas, a doble escala.
        image.PixelSize.Width.ShouldBeGreaterThan((int)(2 * 720 * ChartImage.Scale));
        image.PixelSize.Height.ShouldBeGreaterThan((int)(2 * TrendsView.CellHeight * ChartImage.Scale));
        if (Environment.GetEnvironmentVariable("PLCMON_UI_CAPTURE_DIR") is { Length: > 0 } dir)
            image.Save(Path.Combine(dir, "panel-export.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);

        trends.EditPanelCommand.Execute(null);
        window.UpdateLayout();
        MainWindowTests.Capture(window, "trends-panel-editor.png");
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ShouldContain("VISTAS DEL PANEL");
        window.Close();
        trends.Dispose();
    });

    /// <summary>Datos de prueba con tres vistas en vivo (dos tendencias y un cronograma) y las que se pasen.</summary>
    private (MainViewModel Main, TrendsViewModel Trends) WithViews(params TrendView[] more)
    {
        var main = MainWindowTests.ViewModelWithData(SettingsPath);
        TrendViewStore.Save(TrendsPath,
        [
            new TrendView { Name = "Peso", Agent = main.AgentKey, WindowMinutes = 5, Tags = [new("casa1200", "peso")] },
            new TrendView { Name = "Reloj", Agent = main.AgentKey, WindowMinutes = 5, Tags = [new("casa1200", "clock")] },
            new TrendView { Name = "Estados", Agent = main.AgentKey, Kind = ChartKind.States, WindowMinutes = 5, Tags = [new("casa1200", "clock"), new("casa1200", "peso")] },
            .. more.Select(v => { v.Agent = v.DataSource == TrendDataSource.Csv ? v.Agent : main.AgentKey; return v; }),
        ]);
        return (main, new TrendsViewModel(main, TrendsPath));
    }
}
