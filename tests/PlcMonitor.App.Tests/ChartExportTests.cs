using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using PlcMonitor.App.Charts;
using PlcMonitor.App.Views;

namespace PlcMonitor.App.Tests;

/// <summary>Estadísticas de la ventana visible y exportación de la gráfica a PNG.</summary>
public sealed class ChartExportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);

    private static ChartPoint P(double seconds, double? value) => new(T0.AddSeconds(seconds), value);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void Step_stats_are_weighted_by_time()
    {
        var series = new ChartSeries("presión", [P(0, 10), P(10, 20)]) { Display = new SeriesFormat("bar", 1) };

        var stats = SeriesStats.Compute(series, At(0), At(30));

        stats.Samples.ShouldBe(2);
        // 10 durante 10 s y 20 durante 20 s (el último valor dura hasta el final de la ventana).
        stats.Mean!.Value.ShouldBe(50.0 / 3, 1e-9);
        stats.StdDev!.Value.ShouldBe(Math.Sqrt(200.0 / 9), 1e-9);
        (stats.Min, stats.MinTime, stats.Max, stats.MaxTime).ShouldBe((10, At(0), 20, At(10)));
        (stats.First, stats.Last, stats.Delta).ShouldBe((10, 20, 10));
        stats.MeanText.ShouldBe("16.7 bar");
        stats.DeltaText.ShouldBe("+10.0 bar");
        stats.EdgesText.ShouldBe("—");
    }

    [Fact]
    public void Window_starts_with_the_value_it_carries_and_skips_gaps()
    {
        var series = new ChartSeries("t", [P(0, 10), P(10, 20), P(20, null), P(30, 40)]);

        // Desde 5 s: trae el 10; de 20 a 30 es un hueco que no cuenta.
        var stats = SeriesStats.Compute(series, At(5), At(30));

        stats.Samples.ShouldBe(2); // el de 10 s y el de 30 s
        stats.First.ShouldBe(10);
        stats.MinTime.ShouldBe(At(5));
        stats.Mean!.Value.ShouldBe(((10 * 5) + (20 * 10)) / 15.0, 1e-9);
        stats.Last.ShouldBe(40);
        stats.Max.ShouldBe(40);
    }

    [Fact]
    public void Linear_series_average_the_lines_between_points()
    {
        var series = new ChartSeries("media", [P(0, 0), P(10, 10)], step: false);

        var stats = SeriesStats.Compute(series, At(0), At(10));

        stats.Mean!.Value.ShouldBe(5, 1e-9);
        stats.StdDev!.Value.ShouldBe(Math.Sqrt(100.0 / 12), 1e-9);
        stats.Last.ShouldBe(10);
    }

    [Fact]
    public void Boolean_stats_give_time_true_and_rising_edges()
    {
        var series = new ChartSeries("marcha", [P(0, 0), P(10, 1), P(20, 0), P(30, 1)]) { IsBoolean = true };

        var stats = SeriesStats.Compute(series, At(0), At(40));

        stats.TruePercent!.Value.ShouldBe(50, 1e-9);
        stats.RisingEdges.ShouldBe(2);
        stats.MeanText.ShouldBe("50 % true");
        (stats.FirstText, stats.LastText, stats.StdDevText, stats.DeltaText, stats.EdgesText).ShouldBe(("false", "true", "—", "—", "2"));
    }

    [Fact]
    public void Series_without_values_in_the_window_show_dashes()
    {
        var stats = SeriesStats.Compute(new ChartSeries("vacía", [P(50, 1)]), At(0), At(30));

        stats.Samples.ShouldBe(0);
        (stats.MinText, stats.MaxText, stats.MeanText, stats.FirstText, stats.DeltaText).ShouldBe(("—", "—", "—", "—", "—"));
    }

    [Fact]
    public void Stats_export_to_csv_and_to_a_table()
    {
        var rows = new[]
        {
            SeriesStats.Compute(new ChartSeries("horno, zona 1", [P(0, 10), P(10, 20)]) { Display = new SeriesFormat("°C", null) }, At(0), At(30)),
            SeriesStats.Compute(new ChartSeries("marcha", [P(0, 0), P(10, 1)]) { IsBoolean = true }, At(0), At(20)),
        };

        var csv = SeriesStats.ToCsv(rows, At(0), At(30)).Split('\n');

        csv[0].ShouldBe(SeriesStats.CsvHeader);
        csv[1].ShouldStartWith("\"horno, zona 1\",°C,2026-10-17T10:00:00.000Z,2026-10-17T10:00:30.000Z,2,10,2026-10-17T10:00:00.000Z,20,2026-10-17T10:00:10.000Z,");
        csv[1].ShouldEndWith(",10,20,10,,");
        csv[2].ShouldEndWith(",0,1,,50,1");
        var table = SeriesStats.ToTable(rows).Split('\n');
        table[0].Split('\t').Length.ShouldBe(10);
        table[1].ShouldStartWith("horno, zona 1\t10 °C · ");
    }

    [Fact]
    public void Export_file_name_and_period()
    {
        ChartExport.FileName("Horno: zona 1/2", new DateTime(2026, 10, 22, 9, 5, 0), "png").ShouldBe("Horno_ zona 1_2-20261022-0905.png");
        ChartExport.FileName("  ", new DateTime(2026, 10, 22, 9, 5, 0), "csv").ShouldBe("grafica-20261022-0905.csv");
        var from = new DateTimeOffset(new DateTime(2026, 10, 17, 6, 0, 0, DateTimeKind.Local));
        ChartExport.Period(from, from.AddHours(8)).ShouldBe("17/10/2026 06:00:00 – 14:00:00");
        ChartExport.Period(from, from.AddDays(1)).ShouldBe("17/10/2026 06:00:00 – 18/10/2026 06:00:00");
        ChartExport.Period(null, from).ShouldBe("");
    }

    [Fact]
    public void Chart_computes_stats_of_the_visible_window_only_when_shown()
    {
        var chart = new ChartViewModel();
        chart.SetData([new ChartSeriesData("t", [P(0, 10), P(10, 20)])], At(0), At(30));
        chart.Stats.ShouldBeEmpty();

        chart.ShowStats = true;
        chart.Stats.Single().Mean!.Value.ShouldBe(50.0 / 3, 1e-9);

        // Con zoom, solo lo que se ve.
        chart.Zoom(At(10), At(20));
        chart.Stats.Single().Mean.ShouldBe(20);

        chart.Options.Single().IsSelected = false;
        chart.Stats.ShouldBeEmpty();
        chart.Options.Single().IsSelected = true;

        chart.Kind = ChartKind.XY;
        chart.CanShowStats.ShouldBeFalse();
        chart.Stats.ShouldBeEmpty();
        chart.Kind = ChartKind.States;
        chart.Stats.ShouldHaveSingleItem();
    }

    [Fact]
    public Task Chart_image_has_a_header_and_a_white_background_in_dark_theme() => HeadlessApp.RunAsync(() =>
    {
        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        try
        {
            var chart = new ChartViewModel { Title = "Horno", Source = "Agente planta", ShowStats = true };
            chart.SetData(
            [
                new ChartSeriesData("temperatura", Enumerable.Range(0, 60).Select(i => P(i * 10, 200 + (20 * Math.Sin(i / 6.0)))).ToList()) { Display = new SeriesFormat("°C", 1) },
                new ChartSeriesData("marcha", [P(0, 0), P(100, 1), P(400, 0)], IsBoolean: true),
            ], At(0), At(600));
            var panel = new ChartPanel { DataContext = chart };
            var window = new Window { Width = 1000, Height = 640, Content = new Border { Padding = new Thickness(16), Child = panel } };
            window.Show();
            MainWindowTests.Capture(window, "chart-stats.png");

            using var image = panel.RenderImage();

            image.ShouldNotBeNull();
            image.PixelSize.Width.ShouldBeGreaterThanOrEqualTo((int)(900 * ChartImage.Scale));
            Pixel(image, 2, 2).ShouldBe(0xFFFFFFFFu); // fondo blanco aunque la app esté en oscuro
            Save(image, "chart-export.png");
            // La imagen se monta y se quita: no deja nada en la ventana.
            window.GetVisualDescendants().OfType<ChartImage>().ShouldBeEmpty();
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            texts.ShouldContain("ESTADÍSTICAS DE LA VENTANA VISIBLE");
            texts.ShouldContain(t => t != null && t.EndsWith(" % true", StringComparison.Ordinal));

            // Los demás tipos y los cursores también salen en la imagen.
            chart.ShowCursors = true;
            (chart.CursorA, chart.CursorB) = (At(120), At(300));
            using (var cursors = panel.RenderImage())
                Save(cursors!, "chart-export-cursors.png");
            chart.ShowCursors = false;
            foreach (var kind in new[] { ChartKind.States, ChartKind.Histogram, ChartKind.Bars })
            {
                chart.Kind = kind;
                window.UpdateLayout();
                using var other = panel.RenderImage();
                other!.PixelSize.Height.ShouldBeGreaterThanOrEqualTo((int)(520 * ChartImage.Scale));
                Save(other, $"chart-export-{kind.ToString().ToLowerInvariant()}.png");
            }
            window.Close();
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        }
    });

    private static uint Pixel(Avalonia.Media.Imaging.Bitmap bitmap, int x, int y)
    {
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            bitmap.CopyPixels(new PixelRect(x, y, 1, 1), buffer, 4, 4);
            return (uint)Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void Save(Avalonia.Media.Imaging.Bitmap bitmap, string file)
    {
        if (Environment.GetEnvironmentVariable("PLCMON_UI_CAPTURE_DIR") is not { Length: > 0 } dir)
            return;
        Directory.CreateDirectory(dir);
        bitmap.Save(Path.Combine(dir, file), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
