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
        ChartKindChoice.All.Select(k => k.Value).ShouldBe([ChartKind.Trend, ChartKind.Cycles, ChartKind.XY, ChartKind.States, ChartKind.Histogram, ChartKind.Gauges, ChartKind.Bars]);
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
        var live = new LiveTrendViewModel(() => [("peso", TrendSource.Of(main.FindTag("casa1200", "peso")))], trigger, () => TrendSource.Of(main.FindTag("casa1200", "clock")), ChartKind.Cycles);
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
    public void Histogram_stats_and_capability()
    {
        double[] values = [9, 10, 10, 11, 10, 9, 11, 10];
        var stats = Histogram.Stats(values, 7, 13).ShouldNotBeNull();
        (stats.Count, stats.Mean, stats.Min, stats.Max).ShouldBe((8, 10, 9, 11));
        stats.StdDev.ShouldBe(Math.Sqrt(4.0 / 7), 1e-12);
        stats.Cp!.Value.ShouldBe(6 / (6 * stats.StdDev), 1e-12);
        stats.Cpk!.Value.ShouldBe(3 / (3 * stats.StdDev), 1e-12);

        // Media descentrada: Cpk es el lado más cercano; con una sola tolerancia, solo Cpk.
        Histogram.Stats(values, 8, 16)!.Cpk!.Value.ShouldBe(2 / (3 * stats.StdDev), 1e-12);
        var oneSided = Histogram.Stats(values, null, 13)!;
        oneSided.Cp.ShouldBeNull();
        oneSided.Cpk.ShouldNotBeNull();
        Histogram.Stats([5], 0, 10)!.Cpk.ShouldBeNull(); // sin desviación
        Histogram.Stats([], 0, 10).ShouldBeNull();
    }

    [Fact]
    public void Histogram_bins_cover_all_values_with_round_widths()
    {
        var values = Enumerable.Range(0, 100).Select(i => 10 + (i * 0.05)).ToList(); // 10 … 14.95
        var bins = Histogram.Bins(values);
        bins.Sum(b => b.Count).ShouldBe(100);
        (bins[1].To - bins[1].From).ShouldBe(0.5, 1e-9);
        bins[0].From.ShouldBe(10, 1e-9);
        bins[^1].To.ShouldBeGreaterThan(14.95);

        Histogram.Bins([3, 3, 3]).ShouldHaveSingleItem().Count.ShouldBe(3);

        // Solo los valores dentro de la ventana y con calidad buena.
        Histogram.Values(new ChartSeries("v", [P(0, 1), P(5, null), P(10, 2), P(20, 3)]), At(5), At(15)).ShouldBe([2]);
    }

    [Fact]
    public void Periods_start_on_local_boundaries_and_have_labels()
    {
        var local = new DateTimeOffset(new DateTime(2026, 10, 16, 23, 30, 0, DateTimeKind.Local));
        PeriodBars.Start(local, BarPeriod.Hour, 6).ShouldBe(local.AddMinutes(-30));
        PeriodBars.Start(local, BarPeriod.Day, 6).ShouldBe(local.AddHours(-23).AddMinutes(-30));
        // Turnos desde las 6: T3 empieza a las 22.
        var shift = PeriodBars.Start(local, BarPeriod.Shift, 6);
        shift.ShouldBe(local.AddHours(-1).AddMinutes(-30));
        PeriodBars.Label(shift, BarPeriod.Shift, 6).ShouldBe("16/10 T3");
        // A las 3 de la madrugada sigue el T3 del día anterior.
        var night = PeriodBars.Start(local.AddHours(4), BarPeriod.Shift, 6);
        night.ShouldBe(shift);
        PeriodBars.Label(PeriodBars.Start(local.AddHours(8), BarPeriod.Shift, 6), BarPeriod.Shift, 6).ShouldBe("17/10 T1");
        PeriodBars.Label(local.AddMinutes(-30), BarPeriod.Hour, 6).ShouldBe("16/10 23:00");
        PeriodBars.Label(local.Date, BarPeriod.Day, 6).ShouldBe("vie 16/10");
        PeriodBars.Length(BarPeriod.Shift).ShouldBe(TimeSpan.FromHours(8));
        PeriodBars.Aggregate(BarMetric.Increase).ShouldBe(HistoryAggregate.Last);
    }

    [Fact]
    public void Counter_increase_per_period_handles_resets()
    {
        // Último valor de cada periodo: 100 (referencia), 130, 180, reinicio a 20, 50.
        PeriodBars.Increase([P(0, 100), P(3600, 130), P(7200, 180), P(10800, 20), P(14400, null), P(18000, 50)])
            .ShouldBe([P(3600, 30), P(7200, 50), P(10800, 20), P(18000, 30)]);
    }

    [Fact]
    public void Gauges_flag_values_out_of_limits()
    {
        GaugePanel.OutOfLimits(5, 0, 10).ShouldBeFalse();
        GaugePanel.OutOfLimits(-1, 0, 10).ShouldBeTrue();
        GaugePanel.OutOfLimits(11, null, 10).ShouldBeTrue();
        GaugePanel.OutOfLimits(1e9, null, null).ShouldBeFalse();
    }

    [Fact]
    public void The_options_of_each_kind_are_saved()
    {
        var path = Path.Combine(_dir, "trends.json");
        var main = MainWindowTests.ViewModelWithData(Path.Combine(_dir, "app.json"));
        using var trends = new TrendsViewModel(main, path);
        trends.NewCommand.Execute(null);
        trends.AddTagCommand.Execute(trends.Available.Single(c => c.Tag == "peso"));

        trends.Kind = ChartKindChoice.For(ChartKind.Histogram);
        (trends.HasLimits, trends.LimitsText, trends.CanTrigger, trends.UsesLiveWindow).ShouldBe((true, "Tolerancias", true, true));
        trends.LowLimit = 12;
        trends.HighLimit = 10;
        trends.SaveCommand.Execute(null);
        trends.KindError.ShouldContain("menor que la superior");
        trends.HighLimit = 14;
        trends.SaveCommand.Execute(null);
        trends.KindError.ShouldBeEmpty();

        trends.Kind = ChartKindChoice.For(ChartKind.Gauges);
        (trends.LimitsText, trends.CanTrigger, trends.AxisCaption).ShouldBe(("Límites", false, "Escala"));
        trends.GaugeStyle = TrendsViewModel.GaugeStyles.Single(g => g.Value == GaugeStyle.Level);
        trends.TriggerEnabled = true; // el disparo no aplica a los indicadores: no se guarda
        trends.SaveCommand.Execute(null);
        var saved = TrendViewStore.Load(path).Single();
        (saved.Kind, saved.LowLimit, saved.HighLimit, saved.GaugeStyle, saved.Trigger).ShouldBe((ChartKind.Gauges, 12, 14, GaugeStyle.Level, null));

        trends.OpenCommand.Execute(null);
        var live = trends.Live.ShouldNotBeNull();
        (live.Chart.Kind, live.Chart.LowLimit, live.Chart.HighLimit, live.Chart.GaugeStyle).ShouldBe((ChartKind.Gauges, 12, 14, GaugeStyle.Level));
        trends.EditCommand.Execute(null);

        trends.Kind = ChartKindChoice.For(ChartKind.Bars);
        (trends.UsesLiveWindow, trends.CanTrigger, trends.IsShiftPeriod).ShouldBe((false, false, false));
        trends.BarPeriod = PeriodBarsViewModel.Periods.Single(p => p.Value == BarPeriod.Shift);
        trends.IsShiftPeriod.ShouldBeTrue();
        trends.ShiftStartHour = 7;
        trends.BarMetric = PeriodBarsViewModel.Metrics.Single(m => m.Value == BarMetric.Increase);
        trends.BarRange = PeriodBarsViewModel.Ranges.Single(r => r.Value == 7);
        trends.SaveCommand.Execute(null);
        saved = TrendViewStore.Load(path).Single();
        (saved.BarPeriod, saved.BarMetric, saved.BarDays, saved.ShiftStartHour).ShouldBe((BarPeriod.Shift, BarMetric.Increase, 7, 7));
        trends.Selected!.Description.ShouldBe("Barras · 1 tag · por turno (8 h)");

        // Sin conexión con el agente, las barras lo dicen.
        trends.OpenCommand.Execute(null);
        trends.Live.ShouldBeNull();
        var bars = trends.Bars.ShouldNotBeNull();
        (trends.IsViewing, trends.IsViewingBars, trends.IsEditing).ShouldBe((true, true, false));
        bars.Description.ShouldBe("Incremento (contador) por turno (8 h) · últimos 7 días");
        trends.EditCommand.Execute(null);
        trends.Bars.ShouldBeNull();

        // En la tendencia el disparo sí cuenta: marcado y sin tag, no se guarda.
        trends.Kind = ChartKindChoice.For(ChartKind.Trend);
        trends.SeparateAxes = true;
        trends.SaveCommand.Execute(null);
        trends.TriggerError.ShouldBe("Elige el tag de disparo.");
        trends.TriggerEnabled = false;
        trends.SaveCommand.Execute(null);
        TrendViewStore.Load(path).Single().SeparateAxes.ShouldBeTrue();
    }

    [Fact]
    public void History_periods_are_parsed_and_described()
    {
        TrendHistory.TryParse("17/10/2026 06:00", out var from).ShouldBeTrue();
        from.ShouldBe(new DateTimeOffset(new DateTime(2026, 10, 17, 6, 0, 0, DateTimeKind.Local)));
        TrendHistory.TryParse("7-10-2026 6:30", out var other).ShouldBeTrue();
        other.ShouldBe(new DateTimeOffset(new DateTime(2026, 10, 7, 6, 30, 0, DateTimeKind.Local)));
        TrendHistory.TryParse("17/10/2026", out var day).ShouldBeTrue();
        day.Hour.ShouldBe(0);
        TrendHistory.TryParse("mañana", out _).ShouldBeFalse();
        TrendHistory.TryParse("", out _).ShouldBeFalse();
        TrendHistory.Format(from).ShouldBe("17/10/2026 06:00");

        var now = At(0);
        var view = new TrendView { HistoryMinutes = 480 };
        TrendHistory.Window(view, now).ShouldBe((now.AddHours(-8), now));
        TrendHistory.Describe(view).ShouldBe("últimas 8 h");
        view = new TrendView { HistoryMinutes = 0, HistoryFrom = from, HistoryTo = from.AddHours(8) };
        TrendHistory.Window(view, now).ShouldBe((from, from.AddHours(8)));
        TrendHistory.Describe(view).ShouldBe("del 17/10/2026 06:00 al 17/10/2026 14:00");
    }

    [Fact]
    public async Task The_data_source_is_saved_and_history_needs_the_agent()
    {
        var path = Path.Combine(_dir, "trends.json");
        var main = MainWindowTests.ViewModelWithData(Path.Combine(_dir, "app.json"));
        using var trends = new TrendsViewModel(main, path);
        trends.NewCommand.Execute(null);
        trends.AddTagCommand.Execute(trends.Available.Single(c => c.Tag == "peso"));
        (trends.CanUseHistory, trends.UsesHistory, trends.UsesLiveWindow).ShouldBe((true, false, true));

        trends.DataSource = TrendsViewModel.DataSources.Single(d => d.Value == TrendDataSource.History);
        (trends.UsesHistory, trends.UsesLiveWindow, trends.IsCustomRange).ShouldBe((true, false, false));
        trends.HistoryRange = TrendHistory.Ranges.Single(r => r.Value == 0);
        trends.IsCustomRange.ShouldBeTrue();
        trends.HistoryFromText.ShouldNotBeEmpty(); // propone las últimas 8 h
        trends.HistoryFromText = "17/10/2026 14:00";
        trends.HistoryToText = "17/10/2026 06:00";
        trends.SaveCommand.Execute(null);
        trends.KindError.ShouldStartWith("Periodo:");
        trends.HistoryToText = "17/10/2026 22:00";
        trends.SaveCommand.Execute(null);
        trends.KindError.ShouldBeEmpty();
        var saved = TrendViewStore.Load(path).Single();
        (saved.DataSource, saved.HistoryMinutes).ShouldBe((TrendDataSource.History, 0));
        TrendHistory.Format(saved.HistoryFrom!.Value).ShouldBe("17/10/2026 14:00");
        trends.Selected!.Description.ShouldBe("1 tag · histórico, del 17/10/2026 14:00 al 17/10/2026 22:00");

        // Sin conexión no se puede leer el histórico.
        await trends.OpenCommand.ExecuteAsync(null);
        trends.Live.ShouldBeNull();
        trends.KindError.ShouldBe("Conéctate al agente para leer su histórico.");

        // Los indicadores son el valor actual: siempre en vivo.
        trends.Kind = ChartKindChoice.For(ChartKind.Gauges);
        (trends.CanUseHistory, trends.UsesHistory).ShouldBe((false, false));
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

        var random = new Random(7);
        double Normal() => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
        var histogram = new ChartViewModel { Kind = ChartKind.Histogram, LowLimit = 9.2, HighLimit = 10.8 };
        histogram.SetData(
        [
            new ChartSeriesData("Diámetro", Enumerable.Range(0, 400).Select(i => P(i, 10.1 + (0.2 * Normal()))).ToList()),
        ], At(0), At(400));
        Show(new ChartPanel { DataContext = histogram }, "chart-histogram.png").OfType<HistogramChart>().Single().IsVisible.ShouldBeTrue();

        foreach (var style in new[] { GaugeStyle.Needle, GaugeStyle.Level, GaugeStyle.Value })
        {
            var gauges = new ChartViewModel { Kind = ChartKind.Gauges, GaugeStyle = style, LowLimit = 20, HighLimit = 80 };
            gauges.SetData(
            [
                new ChartSeriesData("Presión", [P(0, 30), P(10, 55)]),
                new ChartSeriesData("Temperatura", [P(0, 70), P(10, 91.5)]),
                new ChartSeriesData("Marcha", [P(0, 1)], IsBoolean: true),
            ], At(0), At(10));
            Show(new ChartPanel { DataContext = gauges }, $"chart-gauges-{style.ToString().ToLowerInvariant()}.png");
        }

        var day = new DateTimeOffset(new DateTime(2026, 10, 16, 6, 0, 0, DateTimeKind.Local));
        var bars = new ChartViewModel { Kind = ChartKind.Bars, BarPeriod = BarPeriod.Shift };
        bars.SetData(
        [
            new ChartSeriesData("Piezas L1", Enumerable.Range(0, 9).Select(i => new ChartPoint(day.AddHours(8 * i), 400 + (i % 3 * 60) + (i * 5))).ToList(), Step: false),
            new ChartSeriesData("Piezas L2", Enumerable.Range(0, 9).Select(i => new ChartPoint(day.AddHours(8 * i), 350 + (i % 3 * 40))).ToList(), Step: false),
        ]);
        Show(new ChartPanel { DataContext = bars }, "chart-bars.png");

        var axes = new ChartViewModel { SeparateAxes = true };
        axes.SetData(
        [
            new ChartSeriesData("Presión bar", Enumerable.Range(0, 60).Select(i => P(i * 3, 4 + Math.Sin(i / 6.0))).ToList()),
            new ChartSeriesData("Caudal l/min", Enumerable.Range(0, 60).Select(i => P(i * 3, 1200 + (300 * Math.Cos(i / 9.0)))).ToList()),
            new ChartSeriesData("Bomba", Enumerable.Range(0, 60).Select(i => P(i * 3, i % 20 < 12 ? 1 : 0)).ToList(), IsBoolean: true),
        ], At(0), At(180));
        axes.CanSeparateAxes.ShouldBeTrue();
        axes.HasYAxis.ShouldBeFalse();
        Show(new ChartPanel { DataContext = axes }, "chart-separate-axes.png");
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
