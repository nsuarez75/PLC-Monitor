using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Charts;

namespace PlcMonitor.App.ViewModels;

/// <summary>Tag historizado que se puede incluir en la consulta.</summary>
public sealed partial class HistoryTagOption(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>Fila del resultado (formato largo: una fila por punto y tag).</summary>
public sealed record HistoryRowViewModel(DateTimeOffset Timestamp, string Tag, string Value, Quality Quality)
{
    public string Time => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public bool IsGood => Quality == Quality.Good;

    public bool IsBad => Quality is not (Quality.Good or Quality.Uncertain);
}

/// <summary>Opción de un desplegable con su texto.</summary>
public sealed record Choice<T>(string Text, T Value)
{
    public override string ToString() => Text;
}

/// <summary>Consulta del histórico del dispositivo seleccionado: tags, periodo, agregado, tabla y exportación a CSV.</summary>
public sealed partial class HistoryViewModel(IAgentContext context, Func<DeviceViewModel?> device) : ObservableObject
{
    public static IReadOnlyList<Choice<TimeSpan>> Ranges { get; } =
    [
        new("Últimos 15 min", TimeSpan.FromMinutes(15)),
        new("Última hora", TimeSpan.FromHours(1)),
        new("Últimas 8 h", TimeSpan.FromHours(8)),
        new("Últimas 24 h", TimeSpan.FromDays(1)),
        new("Últimos 7 días", TimeSpan.FromDays(7)),
        new("Últimos 30 días", TimeSpan.FromDays(30)),
    ];

    /// <summary>Agregado; null = puntos en bruto.</summary>
    public static IReadOnlyList<Choice<HistoryAggregate?>> Aggregates { get; } =
    [
        new("En bruto", null),
        new("Media", HistoryAggregate.Average),
        new("Mínimo", HistoryAggregate.Min),
        new("Máximo", HistoryAggregate.Max),
        new("Primero", HistoryAggregate.First),
        new("Último", HistoryAggregate.Last),
    ];

    public static IReadOnlyList<Choice<TimeSpan>> Intervals { get; } =
    [
        new("1 s", TimeSpan.FromSeconds(1)),
        new("10 s", TimeSpan.FromSeconds(10)),
        new("1 min", TimeSpan.FromMinutes(1)),
        new("5 min", TimeSpan.FromMinutes(5)),
        new("15 min", TimeSpan.FromMinutes(15)),
        new("1 h", TimeSpan.FromHours(1)),
        new("1 día", TimeSpan.FromDays(1)),
    ];

    public ObservableCollection<HistoryTagOption> Tags { get; } = [];

    public ObservableCollection<HistoryRowViewModel> Rows { get; } = [];

    /// <summary>Gráfica del resultado (los tags consultados, todos marcados).</summary>
    public ChartViewModel Chart { get; } = new() { EmptyText = "Consulta el histórico para ver la gráfica" };

    /// <summary>Se pide abrir los tags marcados y el periodo en una vista de Tendencias (con todos los tipos de gráfica).</summary>
    public event EventHandler<PlcMonitor.App.Services.TrendView>? OpenInTrendsRequested;

    /// <summary>Muestra la tabla de puntos además de la gráfica.</summary>
    [ObservableProperty]
    public partial bool ShowTable { get; set; }

    [ObservableProperty]
    public partial Choice<TimeSpan> Range { get; set; } = Ranges[1];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAggregated))]
    public partial Choice<HistoryAggregate?> Aggregate { get; set; } = Aggregates[0];

    [ObservableProperty]
    public partial Choice<TimeSpan> Interval { get; set; } = Intervals[2];

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QueryCommand), nameof(ExportCommand))]
    public partial bool IsBusy { get; set; }

    public bool IsAggregated => Aggregate.Value is not null;

    public bool HasHistorizedTags => Tags.Count > 0;

    /// <summary>Nuevo dispositivo seleccionado: lista de tags nueva y resultado vacío.</summary>
    public void Reset()
    {
        Rows.Clear();
        Summary = "";
        Chart.SetData([]);
        RefreshTags();
    }

    /// <summary>Rehace la lista de tags historizados conservando lo marcado.</summary>
    public void RefreshTags()
    {
        var selected = Tags.Where(t => t.IsSelected).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Tags.Clear();
        foreach (var tag in device()?.Tags.Where(t => t.Historized) ?? [])
            Tags.Add(new HistoryTagOption(tag.Name) { IsSelected = selected.Contains(tag.Name) || selected.Count == 0 });
        OnPropertyChanged(nameof(HasHistorizedTags));
    }

    private bool CanQuery() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanQuery))]
    private async Task QueryAsync()
    {
        if (context.Client is not { } client || device() is not { } target)
            return;
        var tags = Tags.Where(t => t.IsSelected).Select(t => t.Name).ToArray();
        if (tags.Length == 0)
        {
            Summary = "Marca al menos un tag.";
            return;
        }
        IsBusy = true;
        Summary = "Consultando…";
        try
        {
            var to = DateTimeOffset.UtcNow;
            var request = new HistoryRequest
            {
                Device = target.Name,
                From = Timestamp.FromDateTimeOffset(to - Range.Value),
                To = Timestamp.FromDateTimeOffset(to),
                IntervalMs = Aggregate.Value is null ? 0 : (int)Interval.Value.TotalMilliseconds,
                Aggregate = Aggregate.Value ?? HistoryAggregate.Average,
                MaxPoints = 20_000,
            };
            request.Tags.AddRange(tags);
            var reply = await client.QueryHistoryAsync(request);
            Rows.Clear();
            foreach (var row in reply.Series
                         .SelectMany(s => s.Points.Select(p => new HistoryRowViewModel(p.Timestamp.ToDateTimeOffset(), s.Tag, Text(p), p.Quality)))
                         .OrderByDescending(r => r.Timestamp))
                Rows.Add(row);
            var types = target.Tags.ToDictionary(t => t.Name, t => t.IsBoolean, StringComparer.Ordinal);
            Chart.SetData(
                reply.Series.Select(s => new ChartSeriesData(
                    s.Tag,
                    s.Points.Select(p => new ChartPoint(p.Timestamp.ToDateTimeOffset(),
                        p.HasNumber && p.Quality is Quality.Good or Quality.Uncertain ? p.Number : null)).ToArray(),
                    Step: Aggregate.Value is null,
                    IsBoolean: types.GetValueOrDefault(s.Tag))).ToArray(),
                request.From.ToDateTimeOffset(), request.To.ToDateTimeOffset());
            var truncated = reply.Series.Any(s => s.Truncated);
            Summary = $"{Rows.Count} puntos de {tags.Length} tags · {Range.Text.ToLowerInvariant()}" +
                      (Aggregate.Value is null ? "" : $" · {Aggregate.Text.ToLowerInvariant()} cada {Interval.Text}") +
                      (truncated ? " · hay más puntos: usa un agregado o un periodo más corto" : "");
        }
        catch (RpcException ex)
        {
            Summary = "";
            context.Report(context.Describe(ex), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Crea una vista de Tendencias con los tags marcados y el periodo, leída del histórico, y la abre.</summary>
    [RelayCommand]
    private void OpenInTrends()
    {
        if (device() is not { } target)
            return;
        var tags = Tags.Where(t => t.IsSelected).Select(t => new PlcMonitor.App.Services.TrendTagRef(target.Name, t.Name)).ToList();
        if (tags.Count == 0)
        {
            Summary = "Marca al menos un tag.";
            return;
        }
        OpenInTrendsRequested?.Invoke(this, new PlcMonitor.App.Services.TrendView
        {
            Name = $"{target.Name} · {Range.Text.ToLowerInvariant()}",
            Tags = tags,
            DataSource = TrendDataSource.History,
            HistoryMinutes = (int)Range.Value.TotalMinutes,
        });
    }

    /// <summary>Guarda el resultado en CSV (dispositivo, tag, hora UTC ISO 8601, valor, calidad).</summary>
    [RelayCommand(CanExecute = nameof(CanQuery))]
    private async Task ExportAsync()
    {
        if (Rows.Count == 0 || device() is not { } target)
        {
            context.Report("No hay nada que exportar: haz antes una consulta.");
            return;
        }
        var path = await context.Dialogs.SaveFileAsync("Exportar histórico", $"{target.Name}-{DateTime.Now:yyyyMMdd-HHmm}.csv", "csv");
        if (path is null)
            return;
        var csv = new StringBuilder("device,tag,timestamp,value,quality\n");
        foreach (var row in Rows.OrderBy(r => r.Timestamp))
        {
            csv.Append(Escape(target.Name)).Append(',').Append(Escape(row.Tag)).Append(',')
               .Append(row.Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append(',')
               .Append(Escape(row.Value)).Append(',').Append(row.Quality).Append('\n');
        }
        await File.WriteAllTextAsync(path, csv.ToString(), new UTF8Encoding(false));
        context.Report($"Histórico guardado en {path} ({Rows.Count} filas)");
    }

    private static string Text(HistoryPoint p) => p.HasNumber ? p.Number.ToString("G15", CultureInfo.InvariantCulture) : p.Text;

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}
