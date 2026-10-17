using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Charts;
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
        ? (Ranges.FirstOrDefault(r => r.Value == view.HistoryMinutes)?.Text ?? $"últimos {view.HistoryMinutes} min").ToLowerInvariant()
        : $"del {Format(view.HistoryFrom.Value)} al {Format(view.HistoryTo.Value)}";

    /// <summary>
    /// Puntos en bruto de los tags en [from, to], una consulta por dispositivo. <paramref name="truncated"/>: algún tag
    /// tenía más de <see cref="MaxPoints"/>.
    /// </summary>
    public static async Task<(Dictionary<TrendTagRef, TrendSource> Sources, bool Truncated)> LoadAsync(AgentService.AgentServiceClient client,
        IEnumerable<(TrendTagRef Ref, bool IsBoolean)> tags, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var sources = new Dictionary<TrendTagRef, TrendSource>();
        var truncated = false;
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
            foreach (var series in reply.Series)
            {
                truncated |= series.Truncated;
                var points = series.Points
                    .Select(p => new ChartPoint(p.Timestamp.ToDateTimeOffset(), p.HasNumber && p.Quality is Quality.Good or Quality.Uncertain ? p.Number : null))
                    .ToList();
                sources[new TrendTagRef(device.Key, series.Tag)] = new TrendSource(points, types.GetValueOrDefault(series.Tag));
            }
        }
        return (sources, truncated);
    }
}
