using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Charts;
using PlcMonitor.App.Localization;

namespace PlcMonitor.App.ViewModels;

/// <summary>Tag historizado que se puede incluir en la consulta.</summary>
public sealed partial class HistoryTagOption(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>Fila del resultado (formato largo: una fila por punto y tag).</summary>
/// <param name="Value">Valor completo, tal como lo guarda el agente (es el que se exporta).</param>
public sealed record HistoryRowViewModel(DateTimeOffset Timestamp, string Tag, string Value, Quality Quality)
{
    private readonly string? _displayValue;

    /// <summary>Valor como se ve en la tabla: con los decimales y la unidad del tag.</summary>
    public string DisplayValue
    {
        get => _displayValue ?? Value;
        init => _displayValue = value;
    }

    /// <summary>Unidad del tag; vacío = ninguna.</summary>
    public string Unit { get; init; } = "";

    public string Time => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public bool IsGood => Quality == Quality.Good;

    public bool IsBad => Quality is not (Quality.Good or Quality.Uncertain);
}

/// <summary>Opción de un desplegable con su texto, que sigue el idioma elegido (con <paramref name="args"/>, rellena sus {0}…).</summary>
public sealed class Choice<T>(string text, T value, params object?[] args) : LocalizedObservableObject
{
    public string Text => args.Length == 0 ? Loc.T(text) : Loc.F(text, args);

    public T Value { get; } = value;

    public override string ToString() => Text;
}

/// <summary>Consulta del histórico del dispositivo seleccionado: tags, periodo, agregado, tabla y exportación a CSV.</summary>
public sealed partial class HistoryViewModel(IAgentContext context, Func<DeviceViewModel?> device) : LocalizedObservableObject
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
    public ChartViewModel Chart { get; } = new() { EmptyText = Loc.N("Consulta el histórico para ver la gráfica") };

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
    [NotifyCanExecuteChangedFor(nameof(QueryCommand), nameof(ExportCommand), nameof(DeleteCommand))]
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

    /// <summary>Deja marcado solo <paramref name="tag"/> (si es un tag historizado del dispositivo).</summary>
    public bool SelectOnly(string tag)
    {
        if (!Tags.Any(t => t.Name == tag))
            return false;
        foreach (var option in Tags)
            option.IsSelected = option.Name == tag;
        return true;
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
            Summary = Loc.T("Marca al menos un tag.");
            return;
        }
        IsBusy = true;
        Summary = Loc.T("Consultando…");
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
            var known = target.Tags.ToDictionary(t => t.Name, StringComparer.Ordinal);
            Rows.Clear();
            foreach (var row in reply.Series
                         .SelectMany(s => s.Points.Select(p => Row(p, s.Tag, known.GetValueOrDefault(s.Tag))))
                         .OrderByDescending(r => r.Timestamp))
                Rows.Add(row);
            Chart.Title = Loc.F("Histórico {0}", target.Name);
            Chart.Source = context.AgentTitle;
            Chart.SetData(
                reply.Series.Select(s => new ChartSeriesData(
                    s.Tag,
                    s.Points.Select(p => new ChartPoint(p.Timestamp.ToDateTimeOffset(),
                        p.HasNumber && p.Quality is Quality.Good or Quality.Uncertain ? p.Number : null)).ToArray(),
                    Step: Aggregate.Value is null,
                    IsBoolean: known.GetValueOrDefault(s.Tag)?.IsBoolean == true)
                {
                    Display = known.GetValueOrDefault(s.Tag)?.SeriesFormat ?? SeriesFormat.None,
                }).ToArray(),
                request.From.ToDateTimeOffset(), request.To.ToDateTimeOffset());
            var truncated = reply.Series.Any(s => s.Truncated);
            Summary = Loc.F("{0} puntos de {1} tags · {2}", Rows.Count, tags.Length, Range.Text.ToLowerInvariant())
                + (Aggregate.Value is null ? "" : " · " + Loc.F("{0} cada {1}", Aggregate.Text.ToLowerInvariant(), Interval.Text))
                + (truncated ? " · " + Loc.T("hay más puntos: usa un agregado o un periodo más corto") : "");
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

    /// <summary>Se ve el panel para borrar datos del histórico.</summary>
    [ObservableProperty]
    public partial bool ShowDelete { get; set; }

    [ObservableProperty]
    public partial DateTime? DeleteFromDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan? DeleteFromTime { get; set; }

    [ObservableProperty]
    public partial DateTime? DeleteToDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan? DeleteToTime { get; set; }

    /// <summary>Abre o cierra el panel de borrar; al abrirlo propone el periodo elegido para consultar.</summary>
    [RelayCommand]
    private void ToggleDelete()
    {
        ShowDelete = !ShowDelete;
        if (!ShowDelete)
            return;
        var to = DateTime.Now;
        var from = to - Range.Value;
        (DeleteFromDate, DeleteFromTime) = (from.Date, new TimeSpan(from.Hour, from.Minute, 0));
        (DeleteToDate, DeleteToTime) = (to.Date, new TimeSpan(to.Hour, to.Minute, 0) + TimeSpan.FromMinutes(1));
    }

    /// <summary>Inicio y final (hora local) del intervalo que se borra; null si falta algo.</summary>
    private (DateTimeOffset From, DateTimeOffset To)? DeleteRange() =>
        DeleteFromDate is { } fromDate && DeleteToDate is { } toDate
            ? (new DateTimeOffset(fromDate.Date + (DeleteFromTime ?? TimeSpan.Zero)), new DateTimeOffset(toDate.Date + (DeleteToTime ?? TimeSpan.Zero)))
            : null;

    /// <summary>Borra del agente lo guardado de los tags marcados en el intervalo elegido (tras confirmarlo).</summary>
    [RelayCommand(CanExecute = nameof(CanQuery))]
    private async Task DeleteAsync()
    {
        if (context.Client is not { } client || device() is not { } target)
            return;
        var tags = Tags.Where(t => t.IsSelected).Select(t => t.Name).ToArray();
        if (tags.Length == 0)
        {
            Summary = Loc.T("Marca al menos un tag.");
            return;
        }
        if (DeleteRange() is not { } range)
        {
            Summary = Loc.T("Indica el día de inicio y el de final.");
            return;
        }
        if (range.To <= range.From)
        {
            Summary = Loc.T("El final del intervalo debe ser posterior al inicio.");
            return;
        }
        const string format = "dd/MM/yyyy HH:mm";
        var what = tags.Length == 1 ? Loc.F("del tag {0}", tags[0]) : Loc.F("de {0} tags ({1})", tags.Length, string.Join(", ", tags));
        if (!await context.Dialogs.ConfirmAsync(Loc.T("Borrar histórico"),
                Loc.F("¿Borrar los datos guardados {0} de {1} entre el {2} y el {3}?\nNo se puede deshacer.",
                    what, target.Name, range.From.ToString(format, CultureInfo.InvariantCulture), range.To.ToString(format, CultureInfo.InvariantCulture))))
            return;
        IsBusy = true;
        try
        {
            var request = new DeleteHistoryRequest
            {
                Device = target.Name,
                From = Timestamp.FromDateTimeOffset(range.From),
                To = Timestamp.FromDateTimeOffset(range.To),
            };
            request.Tags.AddRange(tags);
            var reply = await client.DeleteHistoryAsync(request);
            context.Report(reply.Deleted == 0 ? Loc.T("No había datos en ese intervalo") : Loc.F("{0} datos borrados del histórico {1}", reply.Deleted, what));
            ShowDelete = false;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            context.Report(Loc.T("Este agente no sabe borrar histórico: actualízalo desde «Agentes» (Instalar / actualizar)."), isError: true);
            return;
        }
        catch (RpcException ex)
        {
            context.Report(context.Describe(ex), isError: true);
            return;
        }
        finally
        {
            IsBusy = false;
        }
        // Lo que se estaba viendo se vuelve a consultar para que no siga mostrando lo borrado.
        if (Rows.Count > 0 || Chart.HasOptions)
            await QueryAsync();
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
            Summary = Loc.T("Marca al menos un tag.");
            return;
        }
        OpenInTrendsRequested?.Invoke(this, new PlcMonitor.App.Services.TrendView
        {
            Name = Loc.F("{0} · {1}", target.Name, Range.Text.ToLowerInvariant()),
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
            context.Report(Loc.T("No hay nada que exportar: haz antes una consulta."));
            return;
        }
        var path = await context.Dialogs.SaveFileAsync(Loc.T("Exportar histórico"), $"{target.Name}-{DateTime.Now:yyyyMMdd-HHmm}.csv", "csv");
        if (path is null)
            return;
        var csv = new StringBuilder(HistoryCsv.Header).Append('\n');
        foreach (var row in Rows.OrderBy(r => r.Timestamp))
        {
            csv.Append(Escape(target.Name)).Append(',').Append(Escape(row.Tag)).Append(',')
               .Append(row.Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append(',')
               .Append(Escape(row.Value)).Append(',').Append(row.Quality).Append(',').Append(Escape(row.Unit)).Append('\n');
        }
        await File.WriteAllTextAsync(path, csv.ToString(), new UTF8Encoding(false));
        context.Report(Loc.F("Histórico guardado en {0} ({1} filas)", path, Rows.Count));
    }

    /// <summary>Fila de un punto: en la tabla, los números con la unidad y los decimales del tag (si se conoce y no es booleano).</summary>
    private static HistoryRowViewModel Row(HistoryPoint p, string tagName, TagViewModel? tag)
    {
        var value = p.HasNumber ? p.Number.ToString("G15", CultureInfo.InvariantCulture) : p.Text;
        return new HistoryRowViewModel(p.Timestamp.ToDateTimeOffset(), tagName, value, p.Quality)
        {
            DisplayValue = p.HasNumber && tag is { IsBoolean: false } ? tag.SeriesFormat.Format(p.Number) : value,
            Unit = tag?.Unit ?? "",
        };
    }

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}
