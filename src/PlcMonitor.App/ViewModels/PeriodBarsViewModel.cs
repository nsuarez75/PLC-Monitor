using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Charts;
using PlcMonitor.App.Localization;
using PlcMonitor.App.Services;

namespace PlcMonitor.App.ViewModels;

/// <summary>
/// Barras por periodo de una vista: consulta el histórico del agente (una petición por dispositivo) agregado por el
/// periodo elegido y dibuja un grupo de barras por periodo. No es en vivo: se vuelve a consultar con «Actualizar».
/// </summary>
public sealed partial class PeriodBarsViewModel : LocalizedObservableObject
{
    public static IReadOnlyList<Choice<BarPeriod>> Periods { get; } =
    [
        new("Hora", BarPeriod.Hour),
        new("Turno (8 h)", BarPeriod.Shift),
        new("Día", BarPeriod.Day),
    ];

    public static IReadOnlyList<Choice<BarMetric>> Metrics { get; } =
    [
        new("Máximo", BarMetric.Max),
        new("Media", BarMetric.Average),
        new("Mínimo", BarMetric.Min),
        new("Incremento (contador)", BarMetric.Increase),
    ];

    public static IReadOnlyList<Choice<int>> Ranges { get; } =
    [
        new("Hoy y ayer", 1),
        new("Últimos 7 días", 7),
        new("Últimos 30 días", 30),
    ];

    private readonly IAgentContext? _context;
    private readonly TrendView _view;
    private readonly Func<TrendTagRef, TagViewModel?>? _findTag;
    private readonly IReadOnlyDictionary<TrendTagRef, TrendSource>? _local;
    private readonly Choice<int>? _rangeChoice;
    private readonly string _file = "";

    public PeriodBarsViewModel(IAgentContext context, TrendView view, Func<TrendTagRef, TagViewModel?> findTag)
    {
        _context = context;
        _view = view;
        _findTag = findTag;
        Chart.BarPeriod = view.BarPeriod;
        Chart.ShiftStartHour = view.ShiftStartHour;
        var range = Ranges.FirstOrDefault(r => r.Value == view.BarDays) ?? Ranges[0];
        _rangeChoice = range;
    }

    /// <summary>Barras de los datos de un CSV, calculadas aquí (todo el fichero, sin agente).</summary>
    public PeriodBarsViewModel(TrendView view, IReadOnlyDictionary<TrendTagRef, TrendSource> local, string file)
    {
        _view = view;
        _local = local;
        _file = file;
        Chart.BarPeriod = view.BarPeriod;
        Chart.ShiftStartHour = view.ShiftStartHour;
    }

    public ChartViewModel Chart { get; } = new() { Kind = ChartKind.Bars, EmptyText = Loc.N("Consultando el histórico…") };

    /// <summary>Qué se ve: cálculo, periodo y rango.</summary>
    public string Description => _local is null
        ? Loc.F("{0} por {1} · {2}", Text(Metrics, _view.BarMetric), Text(Periods, _view.BarPeriod).ToLowerInvariant(), _rangeChoice!.Text.ToLowerInvariant())
        : Loc.F("{0} por {1} · CSV {2}", Text(Metrics, _view.BarMetric), Text(Periods, _view.BarPeriod).ToLowerInvariant(), _file);

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    /// <summary>Tags de la vista que no se historizan (o que el agente no tiene).</summary>
    [ObservableProperty]
    public partial string Missing { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsBusy { get; private set; }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (_local is not null)
        {
            ComputeLocal(_local);
            return;
        }
        if (_context!.Client is not { } client)
        {
            Chart.EmptyText = Loc.N("Conéctate al agente para consultar su histórico");
            Chart.SetData([]);
            return;
        }
        var historized = _view.Tags.Where(t => _findTag!(t)?.Historized == true).ToList();
        var missing = _view.Tags.Except(historized).Select(t => t.ToString()).ToList();
        Missing = missing.Count == 0 ? "" : Loc.F("Sin histórico en el agente: {0}", string.Join(", ", missing));
        if (historized.Count == 0)
        {
            Chart.EmptyText = Loc.N("Ningún tag de la vista se historiza: actívalo en la configuración del dispositivo");
            Chart.SetData([]);
            return;
        }

        IsBusy = true;
        Summary = Loc.T("Consultando…");
        try
        {
            var period = _view.BarPeriod;
            var length = PeriodBars.Length(period);
            var now = DateTimeOffset.UtcNow;
            var from = PeriodBars.Start(now - TimeSpan.FromDays(Math.Max(1, _view.BarDays)), period, _view.ShiftStartHour);
            // El incremento necesita el último valor del periodo anterior al primero.
            if (_view.BarMetric == BarMetric.Increase)
                from -= length;
            var data = new List<ChartSeriesData>();
            var truncated = false;
            foreach (var device in historized.GroupBy(t => t.Device))
            {
                var request = new HistoryRequest
                {
                    Device = device.Key,
                    From = Timestamp.FromDateTimeOffset(from),
                    To = Timestamp.FromDateTimeOffset(now),
                    IntervalMs = (int)length.TotalMilliseconds,
                    Aggregate = PeriodBars.Aggregate(_view.BarMetric),
                    MaxPoints = 10_000,
                };
                request.Tags.AddRange(device.Select(t => t.Tag));
                var reply = await client.QueryHistoryAsync(request);
                foreach (var series in reply.Series)
                {
                    truncated |= series.Truncated;
                    IReadOnlyList<ChartPoint> points = series.Points
                        .Select(p => new ChartPoint(p.Timestamp.ToDateTimeOffset(), p.HasNumber && p.Quality is Quality.Good or Quality.Uncertain ? p.Number : null))
                        .ToList();
                    if (_view.BarMetric == BarMetric.Increase)
                        points = PeriodBars.Increase(points);
                    var key = new TrendTagRef(device.Key, series.Tag);
                    data.Add(new ChartSeriesData(key.ToString(), points, Step: false) { Display = Format(key) });
                }
            }
            Chart.EmptyText = Loc.N("No hay histórico de estos tags en ese periodo");
            Chart.SetData(data, from, now);
            var periods = data.SelectMany(d => d.Points).Select(p => p.Time).Distinct().Count();
            Summary = Loc.F(periods == 1 ? "{0} periodo" : "{0} periodos", periods)
                + " · " + Loc.F("consultado a las {0}", DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
                + (truncated ? " · " + Loc.T("resultado recortado") : "");
        }
        catch (RpcException ex)
        {
            Summary = "";
            Chart.EmptyText = Loc.N("No se ha podido consultar el histórico");
            _context.Report(_context.Describe(ex), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ComputeLocal(IReadOnlyDictionary<TrendTagRef, TrendSource> local)
    {
        var present = _view.Tags.Where(local.ContainsKey).ToList();
        var missing = _view.Tags.Except(present).Select(t => t.ToString()).ToList();
        Missing = missing.Count == 0 ? "" : Loc.F("No están en el CSV: {0}", string.Join(", ", missing));
        var data = present.Select(t => new ChartSeriesData(t.ToString(),
            PeriodBars.Compute(local[t].Points, _view.BarPeriod, _view.BarMetric, _view.ShiftStartHour), Step: false) { Display = Format(t) }).ToList();
        Chart.EmptyText = Loc.N("El CSV no tiene valores de estos tags");
        Chart.SetData(data);
        var periods = data.SelectMany(d => d.Points).Select(p => p.Time).Distinct().Count();
        Summary = Loc.F(periods == 1 ? "{0} periodo" : "{0} periodos", periods);
    }

    private SeriesFormat Format(TrendTagRef tag) => _findTag?.Invoke(tag)?.SeriesFormat ?? SeriesFormat.None;

    private static string Text<T>(IReadOnlyList<Choice<T>> choices, T value) =>
        choices.FirstOrDefault(c => EqualityComparer<T>.Default.Equals(c.Value, value))?.Text ?? "";
}
