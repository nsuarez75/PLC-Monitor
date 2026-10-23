using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Charts;
using PlcMonitor.App.Localization;
using PlcMonitor.App.Services;

namespace PlcMonitor.App.ViewModels;

/// <summary>Lectura del histórico del agente para las vistas de tendencia con datos del histórico.</summary>
public static class TrendHistory
{
    /// <summary>Puntos como mucho por tag en una consulta.</summary>
    public const int MaxPoints = 100_000;

    private const string DateFormat = "dd/MM/yyyy HH:mm";

    /// <summary>Periodos en minutos hacia atrás; 0 = fechas fijas.</summary>
    public static IReadOnlyList<Choice<int>> Ranges { get; } =
    [
        new("Últimos 15 min", 15),
        new("Última hora", 60),
        new("Últimas 8 h", 480),
        new("Últimas 24 h", 1440),
        new("Últimos 7 días", 10080),
        new("Últimos 30 días", 43200),
        new("Fechas concretas", 0),
    ];

    /// <summary>Fecha y hora local como «17/10/2026 06:00».</summary>
    public static string Format(DateTimeOffset time) => time.ToLocalTime().ToString(DateFormat, CultureInfo.InvariantCulture);

    /// <summary>Lee «17/10/2026 06:00» (también con «-» o «.» y sin ceros a la izquierda) en hora local.</summary>
    public static bool TryParse(string? text, out DateTimeOffset time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var normalized = text.Trim().Replace('-', '/').Replace('.', '/');
        if (!DateTime.TryParseExact(normalized, ["d/M/yyyy H:mm", "d/M/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
            return false;
        time = new DateTimeOffset(local);
        return true;
    }

    /// <summary>Periodo que lee una vista: los últimos minutos hasta <paramref name="now"/> o sus fechas fijas.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) Window(TrendView view, DateTimeOffset now) =>
        view.HistoryMinutes > 0 || view.HistoryFrom is null || view.HistoryTo is null
            ? (now - TimeSpan.FromMinutes(Math.Max(1, view.HistoryMinutes)), now)
            : (view.HistoryFrom.Value, view.HistoryTo.Value);

    /// <summary>Texto del periodo: «últimas 8 h» o «del 17/10/2026 06:00 al 17/10/2026 14:00».</summary>
    public static string Describe(TrendView view) => view.HistoryMinutes > 0 || view.HistoryFrom is null || view.HistoryTo is null
        ? (Ranges.FirstOrDefault(r => r.Value == view.HistoryMinutes)?.Text ?? Loc.F("últimos {0} min", view.HistoryMinutes)).ToLowerInvariant()
        : Loc.F("del {0} al {1}", Format(view.HistoryFrom.Value), Format(view.HistoryTo.Value));

    /// <summary>Intervalos en que se agrega un tag con demasiados puntos (mínimo y máximo de cada uno).</summary>
    public const int AggregatedBuckets = 10_000;

    /// <summary>
    /// Puntos en bruto de los tags en [from, to], una consulta por dispositivo. Un tag con más de <see cref="MaxPoints"/>
    /// se vuelve a pedir agregado (el agente recorta por el final: se perdería lo más reciente): mínimo y máximo de cada
    /// uno de <see cref="AggregatedBuckets"/> intervalos, que cubren todo el periodo y conservan los picos.
    /// <c>AggregatedEvery</c>: el intervalo si algún tag se ha agregado.
    /// </summary>
    public static async Task<(Dictionary<TrendTagRef, TrendSource> Sources, TimeSpan? AggregatedEvery)> LoadAsync(AgentService.AgentServiceClient client,
        IEnumerable<(TrendTagRef Ref, bool IsBoolean)> tags, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var sources = new Dictionary<TrendTagRef, TrendSource>();
        TimeSpan? aggregated = null;
        foreach (var device in tags.DistinctBy(t => t.Ref).GroupBy(t => t.Ref.Device))
        {
            var types = device.ToDictionary(t => t.Ref.Tag, t => t.IsBoolean, StringComparer.Ordinal);
            var request = new HistoryRequest
            {
                Device = device.Key,
                From = Timestamp.FromDateTimeOffset(from),
                To = Timestamp.FromDateTimeOffset(to),
                MaxPoints = MaxPoints,
            };
            request.Tags.AddRange(types.Keys);
            var reply = await client.QueryHistoryAsync(request, cancellationToken: cancellationToken);
            var truncated = new List<string>();
            foreach (var series in reply.Series)
            {
                if (series.Truncated)
                    truncated.Add(series.Tag);
                sources[new TrendTagRef(device.Key, series.Tag)] = new TrendSource(Points(series), types.GetValueOrDefault(series.Tag));
            }
            if (truncated.Count == 0)
                continue;

            var every = TimeSpan.FromMilliseconds(Math.Max(1000, Math.Ceiling((to - from).TotalMilliseconds / AggregatedBuckets)));
            aggregated = every;
            var lows = await QueryAggregateAsync(client, device.Key, truncated, from, to, every, HistoryAggregate.Min, cancellationToken);
            var highs = await QueryAggregateAsync(client, device.Key, truncated, from, to, every, HistoryAggregate.Max, cancellationToken);
            foreach (var tag in truncated)
            {
                sources[new TrendTagRef(device.Key, tag)] =
                    new TrendSource(MinMax(lows.GetValueOrDefault(tag, []), highs.GetValueOrDefault(tag, []), every), types.GetValueOrDefault(tag), Step: false);
            }
        }
        return (sources, aggregated);
    }

    /// <summary>
    /// Une el mínimo y el máximo de cada intervalo en una serie: el mínimo al principio del intervalo y el máximo a la
    /// mitad, para que la línea recorra los dos.
    /// </summary>
    public static List<ChartPoint> MinMax(IReadOnlyList<ChartPoint> lows, IReadOnlyList<ChartPoint> highs, TimeSpan every)
    {
        var maxima = highs.Where(p => p.Value is not null).ToDictionary(p => p.Time, p => p.Value);
        var points = new List<ChartPoint>(lows.Count * 2);
        foreach (var low in lows)
        {
            points.Add(low);
            if (maxima.TryGetValue(low.Time, out var high) && high != low.Value)
                points.Add(new ChartPoint(low.Time + (every / 2), high));
        }
        return points;
    }

    private static async Task<Dictionary<string, List<ChartPoint>>> QueryAggregateAsync(AgentService.AgentServiceClient client, string device,
        IReadOnlyList<string> tags, DateTimeOffset from, DateTimeOffset to, TimeSpan every, HistoryAggregate aggregate, CancellationToken cancellationToken)
    {
        var request = new HistoryRequest
        {
            Device = device,
            From = Timestamp.FromDateTimeOffset(from),
            To = Timestamp.FromDateTimeOffset(to),
            IntervalMs = (int)every.TotalMilliseconds,
            Aggregate = aggregate,
            MaxPoints = AggregatedBuckets + 1,
        };
        request.Tags.AddRange(tags);
        var reply = await client.QueryHistoryAsync(request, cancellationToken: cancellationToken);
        return reply.Series.ToDictionary(s => s.Tag, Points, StringComparer.Ordinal);
    }

    private static List<ChartPoint> Points(HistorySeries series) => series.Points
        .Select(p => new ChartPoint(p.Timestamp.ToDateTimeOffset(), p.HasNumber && p.Quality is Quality.Good or Quality.Uncertain ? p.Number : null))
        .ToList();
}
