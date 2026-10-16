using System.Globalization;
using Avalonia;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Charts;
using PlcMonitor.App.Controls;
using PlcMonitor.App.ViewModels;

namespace PlcMonitor.App.Tests;

/// <summary>Gráficas: ejes, tramos, CSV del histórico, estado de la gráfica y búfer de la tendencia en vivo.</summary>
public sealed class ChartTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);

    private static ChartPoint P(double seconds, double? value) => new(T0.AddSeconds(seconds), value);

    [Theory]
    [InlineData(0, 10, 0, 10, 2)]
    [InlineData(3.2, 7.9, 3, 8, 1)]
    [InlineData(-0.37, 0.42, -0.4, 0.6, 0.2)]
    [InlineData(1000, 1000, 900, 1100, 50)]
    public void Y_axis_uses_round_steps(double min, double max, double expectedMin, double expectedMax, double expectedStep)
    {
        var (lo, hi, step) = ChartAxis.NiceRange(min, max, 5);
        lo.ShouldBe(expectedMin, 1e-9);
        hi.ShouldBe(expectedMax, 1e-9);
        step.ShouldBe(expectedStep, 1e-9);
    }

    [Fact]
    public void Time_axis_picks_a_step_and_aligned_ticks()
    {
        ChartAxis.TimeStep(TimeSpan.FromMinutes(5), 6).ShouldBe(TimeSpan.FromMinutes(1));
        ChartAxis.TimeStep(TimeSpan.FromHours(1), 6).ShouldBe(TimeSpan.FromMinutes(10));
        ChartAxis.TimeStep(TimeSpan.FromSeconds(30), 10).ShouldBe(TimeSpan.FromSeconds(5));

        var from = T0.AddSeconds(7);
        var ticks = ChartAxis.TimeTicks(from, from.AddMinutes(3), TimeSpan.FromMinutes(1)).ToArray();
        ticks.Length.ShouldBe(3);
        ticks.ShouldAllBe(t => t.ToLocalTime().Second == 0);
        ChartAxis.FormatTime(ticks[0], TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3)).ShouldBe(ticks[0].ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));
        ChartAxis.FormatValue(12.5).ShouldBe("12.5");
        ChartAxis.FormatValue(0.00001).ShouldBe("1E-5");
        ChartAxis.FormatValue(-0.0).ShouldBe("0");
    }

    [Fact]
    public void Step_series_hold_values_and_break_on_bad_quality()
    {
        var series = new ChartSeries("t", [P(0, 1), P(10, 2), P(20, null), P(30, 3)]);
        series.ValueAt(T0.AddSeconds(5)).ShouldBe(1);
        series.ValueAt(T0.AddSeconds(25)).ShouldBeNull();
        series.ValueAt(T0.AddSeconds(-1)).ShouldBeNull();

        var segments = TrendChart.Segments(series, T0, T0.AddSeconds(40), 400, t => (t - T0).TotalSeconds * 10, v => v);

        segments.Count.ShouldBe(2);
        // Escalón: (0,1) → (100,1) → (100,2) → se mantiene hasta el hueco en x=200.
        segments[0].ShouldBe([new Point(0, 1), new Point(100, 1), new Point(100, 2), new Point(200, 2)]);
        // El último valor llega hasta el final de la ventana.
        segments[1].ShouldBe([new Point(300, 3), new Point(400, 3)]);
    }

    [Fact]
    public void Linear_series_interpolate()
    {
        var series = new ChartSeries("media", [P(0, 0), P(10, 10)], step: false);
        series.ValueAt(T0.AddSeconds(5)).ShouldBe(5);
    }

    [Fact]
    public void Many_points_are_reduced_per_pixel_keeping_the_peaks()
    {
        var points = Enumerable.Range(0, 10_000).Select(i => P(i * 0.01, i == 5_000 ? 100 : 1)).ToArray();
        var series = new ChartSeries("ruido", points, step: false);

        var segment = TrendChart.Segments(series, T0, T0.AddSeconds(100), 100, t => (t - T0).TotalSeconds, v => v).ShouldHaveSingleItem();

        segment.Count.ShouldBeLessThan(500);
        segment.Max(p => p.Y).ShouldBe(100);
    }

    [Fact]
    public void Exported_csv_is_read_back_as_series()
    {
        var csv = """"
            device,tag,timestamp,value,quality
            plc1,velocidad,2026-10-17T10:00:00.000Z,10,Good
            plc1,velocidad,2026-10-17T10:00:01.000Z,12.5,Good
            plc1,velocidad,2026-10-17T10:00:02.000Z,,BadNotConnected
            plc1,marcha,2026-10-17T10:00:00.000Z,true,Good
            plc1,marcha,2026-10-17T10:00:03.000Z,false,Good
            plc1,"texto, raro",2026-10-17T10:00:00.000Z,"hola ""mundo""",Good
            plc1,velocidad,no-es-fecha,1,Good
            """";

        var content = HistoryCsv.Parse(new StringReader(csv));

        content.Rows.ShouldBe(6);
        content.SkippedRows.ShouldBe(1);
        content.Devices.ShouldBe(["plc1"]);
        content.From.ShouldBe(T0);
        content.Series.Select(s => s.Name).ShouldBe(["marcha", "velocidad"]); // el de texto no se dibuja
        var speed = content.Series.Single(s => s.Name == "velocidad");
        speed.Points.Select(p => p.Value).ShouldBe([10, 12.5, null]);
        var run = content.Series.Single(s => s.Name == "marcha");
        run.IsBoolean.ShouldBeTrue();
        run.Points.Select(p => p.Value).ShouldBe([1, 0]);
    }

    [Fact]
    public void Csv_with_several_devices_names_series_by_device()
    {
        var csv = "device,tag,timestamp,value,quality\nA,t,2026-10-17T10:00:00Z,1,Good\nB,t,2026-10-17T10:00:00Z,2,Good\n";
        HistoryCsv.Parse(new StringReader(csv)).Series.Select(s => s.Name).ShouldBe(["A/t", "B/t"]);
    }

    [Fact]
    public void Other_csv_files_are_rejected()
    {
        Should.Throw<FormatException>(() => HistoryCsv.Parse(new StringReader("a,b,c\n1,2,3\n"))).Message.ShouldContain("device,tag,timestamp,value,quality");
    }

    [Fact]
    public void Chart_keeps_selection_and_colors_and_handles_zoom_and_manual_axis()
    {
        var chart = new ChartViewModel();
        chart.SetData([new("a", [P(0, 1), P(10, 5)]), new("b", [P(0, 2)]), new("c", [P(0, 3)])], selectFirst: 2);
        chart.Series.Select(s => s.Name).ShouldBe(["a", "b"]);
        chart.From.ShouldBe(T0);
        chart.To.ShouldBe(T0.AddSeconds(10));

        chart.Options[1].IsSelected = false;
        chart.Options[2].IsSelected = true;
        chart.Series.Select(s => s.Name).ShouldBe(["a", "c"]);
        // Al recargar se conserva lo marcado y cada tag su color.
        chart.SetData([new("c", [P(0, 3)]), new("a", [P(0, 1)])]);
        chart.Series.Select(s => (s.Name, s.ColorIndex)).ShouldBe([("c", 2), ("a", 0)]);

        var zoomed = 0;
        chart.ZoomChanged += (_, _) => zoomed++;
        chart.Zoom(T0.AddSeconds(2), T0.AddSeconds(4));
        (chart.From, chart.To, chart.IsZoomed).ShouldBe((T0.AddSeconds(2), T0.AddSeconds(4), true));
        chart.ResetZoomCommand.Execute(null);
        (chart.From, chart.IsZoomed, zoomed).ShouldBe((T0, false, 2));

        chart.YMin.ShouldBeNull();
        chart.AutoY = false; // propone los límites de lo que se ve
        (chart.YMinInput, chart.YMaxInput).ShouldNotBe((null, null));
        chart.YMinInput = -10;
        chart.YMaxInput = 50;
        (chart.YMin, chart.YMax).ShouldBe((-10, 50));
        chart.AutoY = true;
        chart.YMax.ShouldBeNull();
    }

    [Fact]
    public void Live_values_are_buffered_for_the_trend()
    {
        var tag = new TagViewModel(new NullContext(), new Tag { Device = "plc1", Name = "v", Address = "DB1.DBW0" });
        tag.Apply(Value(T0, 1));
        tag.Apply(new TagValue { Quality = Quality.BadNotConnected, Timestamp = Ts(T0.AddSeconds(1)) });
        tag.Apply(new TagValue { Quality = Quality.Good, BoolValue = true, DataType = DataType.Boolean, Timestamp = Ts(T0.AddSeconds(2)) });

        tag.TrendPoints.Select(p => p.Value).ShouldBe([1, null, 1]);
        tag.IsChartable.ShouldBeTrue();

        // Solo se guarda la última hora (más el valor que había al empezar).
        tag.Apply(Value(T0.AddHours(2), 7));
        tag.TrendPoints.Select(p => p.Value).ShouldBe([1, 7]);
    }

    [Fact]
    public void Live_trend_follows_the_clock_and_pauses_on_zoom()
    {
        var trend = new LiveTrendViewModel(() => []);
        var now = T0.AddMinutes(30);
        trend.Refresh(now);
        (trend.Chart.From, trend.Chart.To).ShouldBe((now - TimeSpan.FromMinutes(5), now));

        trend.Chart.Zoom(now.AddMinutes(-2), now.AddMinutes(-1));
        trend.Paused.ShouldBeTrue();
        trend.Refresh(now.AddMinutes(1));
        trend.Chart.To.ShouldBe(now.AddMinutes(-1));

        trend.TogglePauseCommand.Execute(null);
        trend.Paused.ShouldBeFalse();
        trend.Chart.IsZoomed.ShouldBeFalse();
    }

    private static TagValue Value(DateTimeOffset time, double value) =>
        new() { Quality = Quality.Good, DoubleValue = value, DataType = DataType.Double, Text = value.ToString(CultureInfo.InvariantCulture), Timestamp = Ts(time) };

    private static Google.Protobuf.WellKnownTypes.Timestamp Ts(DateTimeOffset time) => Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(time);

    private sealed class NullContext : IAgentContext
    {
        public AgentService.AgentServiceClient? Client => null;

        public Services.IDialogs Dialogs { get; } = new FakeDialogs();

        public void Report(string message, bool isError = false)
        {
        }

        public string Describe(Exception exception) => exception.Message;
    }
}
