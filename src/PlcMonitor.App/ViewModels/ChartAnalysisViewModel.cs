using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Charts;
using PlcMonitor.App.Services;

namespace PlcMonitor.App.ViewModels;

/// <summary>
/// Análisis de datos ya cargados (un CSV o una consulta del histórico): el tipo de gráfica y sus opciones (eje X,
/// disparo, tolerancias, barras) se eligen aquí y la gráfica se rehace con cada cambio sobre los mismos datos. Usa la
/// misma lógica que las vistas de tendencia con una ventana fija; las barras se calculan aquí por periodo.
/// </summary>
public sealed partial class ChartAnalysisViewModel : ObservableObject, IDisposable
{
    private IReadOnlyList<ChartSeriesData> _data = [];
    private DateTimeOffset? _from;
    private DateTimeOffset? _to;
    private LiveTrendViewModel? _composer;
    private bool _loading;

    public ChartAnalysisViewModel(string emptyText)
    {
        Chart = new ChartViewModel { EmptyText = emptyText };
        EmptyText = emptyText;
    }

    /// <summary>La gráfica (siempre la misma: el panel no cambia, cambia lo que dibuja).</summary>
    public ChartViewModel Chart { get; }

    private string EmptyText { get; }

    /// <summary>Tipos disponibles: todos menos los indicadores, que son el valor actual.</summary>
    public static IReadOnlyList<ChartKindChoice> Kinds { get; } = ChartKindChoice.All.Where(k => k.Value != ChartKind.Gauges).ToArray();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsXY), nameof(IsBars), nameof(IsHistogram), nameof(CanTrigger))]
    public partial ChartKindChoice Kind { get; set; } = Kinds[0];

    public bool IsXY => Kind.Value == ChartKind.XY;

    public bool IsBars => Kind.Value == ChartKind.Bars;

    public bool IsHistogram => Kind.Value == ChartKind.Histogram;

    /// <summary>Las barras no usan disparo.</summary>
    public bool CanTrigger => Kind.Value != ChartKind.Bars;

    /// <summary>Tags de los datos (para el eje X y el disparo).</summary>
    public ObservableCollection<string> Names { get; } = [];

    [ObservableProperty]
    public partial string? XTag { get; set; }

    [ObservableProperty]
    public partial bool TriggerEnabled { get; set; }

    [ObservableProperty]
    public partial string? TriggerTag { get; set; }

    [ObservableProperty]
    public partial Choice<TriggerCondition> TriggerCondition { get; set; } = TagDetailsViewModel.Conditions[0];

    /// <summary>true/false para booleanos; un número para los demás.</summary>
    [ObservableProperty]
    public partial string TriggerValue { get; set; } = "true";

    [ObservableProperty]
    public partial decimal TriggerBeforeSeconds { get; set; }

    [ObservableProperty]
    public partial decimal TriggerHoldSeconds { get; set; }

    [ObservableProperty]
    public partial decimal? LowLimit { get; set; }

    [ObservableProperty]
    public partial decimal? HighLimit { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShiftPeriod))]
    public partial Choice<BarPeriod> BarPeriod { get; set; } = PeriodBarsViewModel.Periods[0];

    public bool IsShiftPeriod => IsBars && BarPeriod.Value == Charts.BarPeriod.Shift;

    [ObservableProperty]
    public partial Choice<BarMetric> BarMetric { get; set; } = PeriodBarsViewModel.Metrics[0];

    [ObservableProperty]
    public partial decimal ShiftStartHour { get; set; } = 6;

    /// <summary>Lo que falta para dibujar el tipo elegido (disparo de los ciclos, eje X…).</summary>
    [ObservableProperty]
    public partial string Note { get; private set; } = "";

    /// <summary>Datos nuevos: conserva las opciones (y lo marcado) si los tags siguen ahí.</summary>
    public void SetData(IReadOnlyList<ChartSeriesData> data, DateTimeOffset? from = null, DateTimeOffset? to = null, int selectFirst = int.MaxValue)
    {
        _data = data;
        var all = data.SelectMany(d => d.Points).Select(p => p.Time).ToArray();
        _from = from ?? (all.Length > 0 ? all.Min() : null);
        _to = to ?? (all.Length > 0 ? all.Max() : null);
        _loading = true;
        Names.Clear();
        foreach (var d in data)
            Names.Add(d.Name);
        if (XTag is null || !Names.Contains(XTag))
            XTag = Names.FirstOrDefault();
        if (TriggerTag is not null && !Names.Contains(TriggerTag))
            TriggerTag = null;
        _loading = false;
        Rebuild(selectFirst);
    }

    partial void OnKindChanged(ChartKindChoice value)
    {
        OnPropertyChanged(nameof(IsShiftPeriod));
        Rebuild();
    }

    partial void OnXTagChanged(string? value) => Rebuild();

    partial void OnTriggerEnabledChanged(bool value) => Rebuild();

    partial void OnTriggerTagChanged(string? value)
    {
        // Un booleano dispara con true por defecto; uno numérico, con lo que se escriba.
        if (value is not null && _data.FirstOrDefault(d => d.Name == value) is { IsBoolean: true } && !TriggerFilter.TryParseTarget(TriggerValue, out _))
            TriggerValue = "true";
        Rebuild();
    }

    partial void OnTriggerConditionChanged(Choice<TriggerCondition> value) => Rebuild();

    partial void OnTriggerValueChanged(string value) => Rebuild();

    partial void OnTriggerBeforeSecondsChanged(decimal value) => Rebuild();

    partial void OnTriggerHoldSecondsChanged(decimal value) => Rebuild();

    partial void OnLowLimitChanged(decimal? value) => Chart.LowLimit = value is { } v ? (double)v : null;

    partial void OnHighLimitChanged(decimal? value) => Chart.HighLimit = value is { } v ? (double)v : null;

    partial void OnBarPeriodChanged(Choice<BarPeriod> value) => Rebuild();

    partial void OnBarMetricChanged(Choice<BarMetric> value) => Rebuild();

    partial void OnShiftStartHourChanged(decimal value) => Rebuild();

    /// <summary>Rehace la gráfica con el tipo y las opciones actuales sobre los datos cargados.</summary>
    private void Rebuild(int selectFirst = int.MaxValue)
    {
        if (_loading)
            return;
        _composer?.Dispose();
        _composer = null;
        Chart.EmptyText = EmptyText;
        Chart.BarPeriod = BarPeriod.Value;
        Chart.ShiftStartHour = (int)ShiftStartHour;
        if (_data.Count == 0 || _from is not { } from || _to is not { } to)
        {
            Note = "";
            Chart.Kind = Kind.Value;
            Chart.SetData([]);
            return;
        }

        if (Kind.Value == ChartKind.Bars)
        {
            Note = "";
            Chart.Kind = ChartKind.Bars;
            Chart.Curves = [];
            Chart.SetData(_data.Select(d => new ChartSeriesData(d.Name, PeriodBars.Compute(d.Points, BarPeriod.Value, BarMetric.Value, (int)ShiftStartHour), Step: false, d.IsBoolean)),
                from, to, selectFirst);
            return;
        }

        var trigger = TriggerEnabled && CanTrigger && TriggerTag is { } tag && TriggerFilter.TryParseTarget(TriggerValue, out _)
            ? new TrendTrigger
            {
                Tag = tag,
                Condition = TriggerCondition.Value,
                Value = TriggerValue.Trim(),
                BeforeSeconds = (double)TriggerBeforeSeconds,
                HoldSeconds = (double)TriggerHoldSeconds,
            }
            : null;
        Note = Kind.Value switch
        {
            _ when TriggerEnabled && trigger is null => "Disparo incompleto: elige el tag y un valor (true/false o un número).",
            ChartKind.Cycles when trigger is null => "Los ciclos superpuestos necesitan un disparo: márcalo y elige el tag y la condición.",
            ChartKind.XY when Names.Count < 2 => "La gráfica X-Y necesita al menos dos tags.",
            _ => "",
        };
        var sources = _data.ToDictionary(d => d.Name, d => new TrendSource(d.Points as List<ChartPoint> ?? d.Points.ToList(), d.IsBoolean, d.Step));
        var names = Kind.Value == ChartKind.XY && XTag is { } x ? [x, .. Names.Where(n => n != x)] : Names.ToArray();
        _composer = new LiveTrendViewModel(
            () => names.Select(n => (n, sources.GetValueOrDefault(n))).ToArray(),
            trigger,
            trigger is null ? null : () => sources.GetValueOrDefault(trigger.Tag),
            Kind.Value,
            Chart)
        {
            FixedWindow = (from, to),
        };
        if (trigger is not null)
            Chart.EmptyText = "El disparo no se cumple en estos datos";
        _composer.RefreshTags(selectFirst);
    }

    public void Dispose() => _composer?.Dispose();
}
