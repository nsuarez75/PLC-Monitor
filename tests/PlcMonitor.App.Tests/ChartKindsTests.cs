using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Google.Protobuf.WellKnownTypes;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Charts;
using PlcMonitor.App.Controls;
using PlcMonitor.App.Services;
using PlcMonitor.App.ViewModels;
using PlcMonitor.App.Views;

namespace PlcMonitor.App.Tests;

/// <summary>Tipos de gráfica: cronograma de estados, ciclos superpuestos y X-Y.</summary>
public sealed class ChartKindsTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("plcmon-kinds-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ChartPoint P(double seconds, double? value) => new(T0.AddSeconds(seconds), value);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    private static bool On(double? v) => TriggerFilter.IsMet(v, TriggerCondition.Equal, 1);

    [Fact]
    public void State_segments_join_equal_values_and_hold_the_last_one()
    {
        var series = new ChartSeries("marcha", [P(0, 0), P(10, 1), P(15, 1), P(30, null), P(35, 0)]);

        StateTimeline.Segments(series, At(5), At(40)).ShouldBe(
        [
            new(At(5), At(10), 0),
            new(At(10), At(30), 1),
            new(At(30), At(35), null),
            new(At(35), At(40), 0),
        ]);
        // Antes del primer punto no hay estado.
        StateTimeline.Segments(series, At(-10), At(5)).ShouldBe([new(At(0), At(5), 0)]);
        StateTimeline.Segments(series, At(5), At(5)).ShouldBeEmpty();
    }

    [Fact]
    public void State_shares_ignore_gaps()
    {
        var shares = StateTimeline.Shares([new(At(0), At(30), 1), new(At(30), At(40), null), new(At(40), At(50), 0), new(At(50), At(60), 1)]);

        shares.ShouldBe([new(1, TimeSpan.FromSeconds(40), 0.8), new(0, TimeSpan.FromSeconds(10), 0.2)]);
    }

    [Theory]
    [InlineData(0.25, "250 ms")]
    [InlineData(12.34, "12.3 s")]
    [InlineData(245, "4 min 05 s")]
    [InlineData(7620, "2 h 07 min")]
    public void Durations_are_readable(double seconds, string expected) =>
        StateTimeline.FormatDuration(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);

    [Fact]
    public void Activations_are_kept_apart_and_the_last_may_be_open()
    {
        var trigger = new[] { P(0, 0), P(10, 1), P(12, 1), P(20, 0), P(21, 1), P(30, 0), P(60, 1) };

        XYData.Activations(trigger, On).ShouldBe([new(At(10), At(20)), new(At(21), At(30)), new(At(60), null)]);
    }

    [Fact]
    public void A_cycle_is_aligned_on_the_activation()
    {
        var series = new ChartSeries("fuerza", [P(0, 1), P(9, 2), P(12, 5), P(18, 3), P(40, 9)]);
        var activation = new TriggerActivation(At(10), At(20));
        var (from, to) = XYData.CycleWindow(activation, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), At(100));
        (from, to).ShouldBe((At(8), At(23)));

        // Valor al empezar (−2 s), puntos del ciclo y el último mantenido hasta el final (escalón).
        XYData.Cycle(series, activation.On, from, to).ShouldBe([new(-2, 1), new(-1, 2), new(2, 5), new(8, 3), new(13, 3)]);

        // Ciclo abierto: hasta ahora.
        XYData.CycleWindow(new TriggerActivation(At(90), null), TimeSpan.Zero, TimeSpan.FromSeconds(5), At(95)).ShouldBe((At(90), At(95)));
    }

    [Fact]
    public void Pairs_take_both_values_at_each_change()
    {
        var x = new ChartSeries("posicion", [P(0, 0), P(10, 1), P(20, 2)]);
        var y = new ChartSeries("fuerza", [P(5, 100), P(15, null), P(18, 300)]);

        XYData.Pairs(x, y, At(0), At(20)).ShouldBe(
        [
            new(0, 100),   // 5 s
            new(1, 100),   // 10 s
            new(0, null),  // 15 s: hueco
            new(1, 300),   // 18 s
            new(2, 300),   // 20 s
        ]);
    }

    [Fact]
    public void The_kind_is_saved_and_old_views_are_trends()
    {
        var settings = Path.Combine(_dir, "app.json");
        var path = Path.Combine(_dir, "trends.json");
        File.WriteAllText(path, """[{ "Name": "Antigua", "Agent": "x", "Tags": [] }]""");
        TrendViewStore.Load(path).Single().Kind.ShouldBe(ChartKind.Trend);

        var main = MainWindowTests.ViewModelWithData(settings);
        using var trends = new TrendsViewModel(main, path);
        trends.NewCommand.Execute(null);
        trends.Kind.Value.ShouldBe(ChartKind.Trend);
        ChartKindChoice.All.Select(k => k.Value).ShouldBe([ChartKind.Trend, ChartKind.Cycles, ChartKind.XY, ChartKind.States]);
        ChartKindChoice.All.ShouldAllBe(k => k.Description.Length > 40);

        // Ciclos sin disparo: no se abre.
        trends.Kind = ChartKindChoice.For(ChartKind.Cycles);
        trends.AddTagCommand.Execute(trends.Available.Single(c => c.Tag == "peso"));
        trends.OpenCommand.Execute(null);
        trends.Live.ShouldBeNull();
        trends.KindError.ShouldContain("disparo");

        // X-Y con un tag: falta el otro eje.
        trends.Kind = ChartKindChoice.For(ChartKind.XY);
        trends.IsXYKind.ShouldBeTrue();
        trends.XAxisText.ShouldBe("Eje X: casa1200 / peso");
        trends.OpenCommand.Execute(null);
        trends.KindError.ShouldContain("dos tags");
        trends.AddTagCommand.Execute(trends.Available.Single(c => c.Tag == "clock"));
        trends.MakeXAxisCommand.Execute(new TrendTagRef("casa1200", "clock"));
        trends.XAxisText.ShouldBe("Eje X: casa1200 / clock");
        trends.SaveCommand.Execute(null);
        trends.KindError.ShouldBeEmpty();

        var saved = TrendViewStore.Load(path).Last();
        saved.Kind.ShouldBe(ChartKind.XY);
        saved.Tags.Select(t => t.Tag).ShouldBe(["clock", "peso"]);
        File.ReadAllText(path).ShouldContain("\"XY\"");
        trends.Selected!.Description.ShouldStartWith("X-Y · 2 tags");

        trends.OpenCommand.Execute(null);
        var live = trends.Live.ShouldNotBeNull();
        live.Chart.Kind.ShouldBe(ChartKind.XY);
        live.Chart.XLabel.ShouldBe("X: casa1200 / clock");
        live.Chart.Options.Select(o => o.Name).ShouldBe(["casa1200 / peso"]);
    }

    [Fact]
    public void Live_cycles_overlay_the_activations_and_highlight_the_last()
    {
        var main = MainWindowTests.ViewModelWithData(Path.Combine(_dir, "app.json"));
        var start = DateTimeOffset.UtcNow.AddSeconds(5); // después de la foto inicial (lo anterior se adelanta a ella)
        var updates = new TagUpdates();
        // clock: tres activaciones de 10 s; peso sube durante cada una.
        foreach (var (at, on) in new[] { (0, true), (10, false), (60, true), (70, false), (120, true), (130, false) })
            updates.Updates.Add(Update("clock", start.AddSeconds(at), DataType.Boolean, on ? 1 : 0));
        foreach (var cycle in new[] { 0, 60, 120 })
        {
            for (var s = 0; s <= 10; s += 2)
                updates.Updates.Add(Update("peso", start.AddSeconds(cycle + s), DataType.Float, s * (1 + (cycle / 60.0))));
        }
        main.Apply(new SubscribeEvent { TagUpdates = updates });

        var trigger = new TrendTrigger { Device = "casa1200", Tag = "clock", Value = "true", BeforeSeconds = 2 };
        var live = new LiveTrendViewModel(() => [("peso", main.FindTag("casa1200", "peso"))], trigger, () => main.FindTag("casa1200", "clock"), ChartKind.Cycles);
        live.RefreshTags();
        live.Refresh(start.AddSeconds(200));

        live.UsesWindow.ShouldBeFalse();
        live.Chart.XLabel.ShouldBe("s desde el disparo");
        live.Chart.Caption.ShouldBe("3 ciclos");
        var curves = live.Chart.Curves;
        curves.Count.ShouldBe(3);
        curves.Select(c => c.Highlight).ShouldBe([false, false, true]);
        // Todas empiezan 2 s antes del disparo; el tercer ciclo sube hasta 10 × 3.
        curves.ShouldAllBe(c => c.Points[0].X == -2);
        curves[^1].Points.Max(p => p.Y ?? 0).ShouldBe(30);

        // Desmarcar el tag deja la gráfica vacía.
        live.Chart.Options.Single().IsSelected = false;
        live.Chart.Curves.ShouldBeEmpty();
    }

    [Fact]
    public Task The_new_charts_are_drawn() => HeadlessApp.RunAsync(() =>
    {
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var states = new ChartViewModel { Kind = ChartKind.States };
        states.SetData(
        [
            new ChartSeriesData("Marcha", [P(0, 0), P(20, 1), P(140, 0), P(170, 1)], IsBoolean: true),
            new ChartSeriesData("Paso secuencia", [P(0, 1), P(30, 2), P(60, 3), P(90, 4), P(130, 1), P(160, 2)]),
            new ChartSeriesData("Alarma", [P(0, 0), P(100, 1), P(115, 0)], IsBoolean: true),
        ], At(0), At(200));
        Show(new ChartPanel { DataContext = states }, "chart-states.png").OfType<StateChart>().Single().IsVisible.ShouldBeTrue();

        var cycles = new ChartViewModel { Kind = ChartKind.Cycles, XLabel = "s desde el disparo", Caption = "8 ciclos" };
        cycles.SetData([new ChartSeriesData("fuerza", []), new ChartSeriesData("posición", [])]);
        cycles.Curves = Enumerable.Range(0, 8).SelectMany(i => new[]
        {
            new XYCurve("fuerza", Enumerable.Range(-20, 120).Select(k => new XYPoint(k / 10.0, k < 0 ? 0 : Math.Sin(k / 30.0) * (50 + (i * 3)))).ToList(), 0, Highlight: i == 7),
            new XYCurve("posición", Enumerable.Range(-20, 120).Select(k => new XYPoint(k / 10.0, k < 0 ? 0 : k * (0.3 + (i * 0.01)))).ToList(), 1, Highlight: i == 7),
        }).ToList();
        var drawn = Show(new ChartPanel { DataContext = cycles }, "chart-cycles.png");
        drawn.OfType<XYChart>().Single().IsVisible.ShouldBeTrue();
        drawn.OfType<TrendChart>().Single().IsVisible.ShouldBeFalse();

        var xy = new ChartViewModel { Kind = ChartKind.XY, XLabel = "X: posición" };
        xy.SetData([new ChartSeriesData("fuerza", [])]);
        xy.Curves = [new XYCurve("fuerza", Enumerable.Range(0, 100).Select(k => new XYPoint(k * 0.5, (k * k * 0.01) - (k * 0.2))).ToList(), 0)];
        Show(new ChartPanel { DataContext = xy }, "chart-xy.png");
    });

    private static IEnumerable<Avalonia.Visual> Show(ChartPanel panel, string file)
    {
        var window = new Window { Width = 900, Height = 420, Content = new Border { Padding = new Avalonia.Thickness(16), Child = panel } };
        window.Show();
        MainWindowTests.Capture(window, file);
        return window.GetVisualDescendants();
    }

    private static TagUpdate Update(string tag, DateTimeOffset at, DataType type, double value) => new()
    {
        Device = "casa1200",
        Tag = tag,
        Value = type == DataType.Boolean
            ? new TagValue { Quality = Quality.Good, DataType = type, BoolValue = value >= 0.5, Timestamp = Timestamp.FromDateTimeOffset(at) }
            : new TagValue { Quality = Quality.Good, DataType = type, DoubleValue = value, Timestamp = Timestamp.FromDateTimeOffset(at) },
    };
}
