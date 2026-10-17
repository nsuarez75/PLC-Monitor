using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcMonitor.App.Charts;
using PlcMonitor.App.Services;

namespace PlcMonitor.App.ViewModels;

/// <summary>
/// Puntos de un tag para una gráfica: la lista en vivo del tag (la misma, que va creciendo) o lo leído del histórico.
/// </summary>
/// <param name="Step">Escalón (valores por cambio, en bruto) o rectas entre puntos (agregados).</param>
public sealed record TrendSource(List<ChartPoint> Points, bool IsBoolean, bool Step = true)
{
    public static TrendSource? Of(TagViewModel? tag) => tag is null ? null : new TrendSource(tag.TrendPoints, tag.IsBoolean);
}

/// <summary>
/// Gráfica de una vista de tendencia. En vivo, con los valores que llegan del agente, en una ventana que avanza con el
/// reloj (se pausa al hacer zoom o con el botón; «Seguir» quita el zoom y continúa). Del histórico, con una ventana
/// fija (<see cref="FixedWindow"/>) y los puntos leídos del agente.
/// </summary>
public sealed partial class LiveTrendViewModel : ObservableObject, IDisposable
{
    private readonly Func<IReadOnlyList<(string Name, TrendSource? Tag)>> _tags;
    private readonly TrendTrigger? _trigger;
    private readonly Func<TrendSource?>? _triggerTag;
    private readonly List<(TrendSource Source, List<ChartPoint> Shown)> _filtered = [];
    private readonly ChartKind _kind;
    private TrendSource? _triggerSource;
    private TrendSource? _xSource;
    private string _xName = "";
    private bool _drawn;

    /// <summary>Ciclos superpuestos que se dibujan como mucho (los más recientes).</summary>
    public const int MaxCycles = 20;

    /// <param name="tags">Tags a dibujar con su nombre en la leyenda; null si el tag ya no existe en el agente.</param>
    /// <param name="trigger">Tendencia de disparo: solo se ven los tramos en que se cumple (más el tiempo extra).</param>
    /// <param name="triggerTag">Los puntos del tag disparo (null si no existe).</param>
    /// <param name="kind">Tipo de gráfica; en X-Y el primer tag es el eje X.</param>
    /// <param name="chart">Gráfica a alimentar (la de un panel que ya existe); null = una nueva.</param>
    public LiveTrendViewModel(Func<IReadOnlyList<(string Name, TrendSource? Tag)>> tags, TrendTrigger? trigger = null, Func<TrendSource?>? triggerTag = null,
        ChartKind kind = ChartKind.Trend, ChartViewModel? chart = null)
    {
        _tags = tags;
        _trigger = trigger;
        _triggerTag = triggerTag;
        _kind = kind;
        Chart = chart ?? new ChartViewModel { EmptyText = "Aún no hay valores de estos tags" };
        Chart.Kind = kind;
        Chart.Curves = [];
        Chart.Caption = "";
        Chart.XLabel = "";
        Chart.ZoomChanged += OnZoomChanged;
        Chart.PropertyChanged += OnChartChanged;
    }

    private void OnZoomChanged(object? sender, EventArgs e) => Paused = Chart.IsZoomed;

    // Marcar o desmarcar tags cambia las curvas.
    private void OnChartChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChartViewModel.Series) && UsesCurves)
            BuildCurves(FixedWindow?.To ?? DateTimeOffset.UtcNow);
    }

    /// <summary>Deja de seguir la gráfica (cuando otra la sustituye en el mismo panel).</summary>
    public void Dispose()
    {
        Chart.ZoomChanged -= OnZoomChanged;
        Chart.PropertyChanged -= OnChartChanged;
    }

    /// <summary>Ciclos y X-Y dibujan curvas calculadas, no las series en el tiempo.</summary>
    private bool UsesCurves => _kind is ChartKind.Cycles or ChartKind.XY;

    /// <summary>Histórico: ventana fija (null = en vivo, la ventana avanza con el reloj).</summary>
    public (DateTimeOffset From, DateTimeOffset To)? FixedWindow { get; init; }

    public bool IsLive => FixedWindow is null;

    /// <summary>Texto del aviso de tags que faltan; null = el de en vivo o el del histórico.</summary>
    public string? MissingText { get; init; }

    /// <summary>Qué periodo del histórico se ve y cuándo se consultó (vacío en vivo).</summary>
    public string HistoryText { get; init; } = "";

    /// <summary>Se elige la ventana en vivo (los ciclos usan todo lo recibido, la última hora; el histórico, su periodo).</summary>
    public bool UsesWindow => _kind != ChartKind.Cycles && IsLive;

    /// <summary>Estado del disparo ahora mismo: solo en vivo.</summary>
    public bool ShowTriggerState => IsTriggered && IsLive;

    /// <summary>Es una tendencia de disparo.</summary>
    public bool IsTriggered => _trigger is not null;

    public string TriggerText => _trigger is null ? "" : $"Disparo: {_trigger}";

    /// <summary>El disparo se cumple ahora (o sigue en su tiempo extra).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TriggerState))]
    public partial bool TriggerActive { get; private set; }

    public string TriggerState => TriggerActive ? "Activo" : "Esperando";

    public static IReadOnlyList<Choice<TimeSpan>> Windows { get; } =
    [
        new("1 min", TimeSpan.FromMinutes(1)),
        new("5 min", TimeSpan.FromMinutes(5)),
        new("15 min", TimeSpan.FromMinutes(15)),
        new("1 h", TimeSpan.FromHours(1)),
    ];

    public ChartViewModel Chart { get; }

    [ObservableProperty]
    public partial Choice<TimeSpan> Window { get; set; } = Windows[1];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseText))]
    public partial bool Paused { get; set; }

    public string PauseText => Paused ? "Seguir" : "Pausa";

    /// <summary>Tags de la vista que el agente ya no tiene (se han borrado o renombrado).</summary>
    [ObservableProperty]
    public partial string Missing { get; private set; } = "";

    /// <summary>Vuelve a enlazar las series con los tags (al abrir y cuando el agente reconfigura o se reconecta).</summary>
    /// <param name="selectFirst">Sin nada marcado aún, cuántos tags se marcan.</param>
    public void RefreshTags(int selectFirst = int.MaxValue)
    {
        var tags = _tags();
        var now = FixedWindow?.To ?? DateTimeOffset.UtcNow;
        var from = FixedWindow?.From ?? now - Window.Value;
        _filtered.Clear();
        _drawn = false;
        _triggerSource = _triggerTag?.Invoke();
        // X-Y: el primer tag es el eje X; en la gráfica se marcan los demás.
        var plotted = tags;
        if (_kind == ChartKind.XY && tags.Count > 0)
        {
            (_xName, _xSource) = tags[0];
            plotted = tags.Skip(1).ToArray();
            Chart.XLabel = $"X: {_xName}";
        }
        else if (_kind == ChartKind.Cycles)
        {
            Chart.XLabel = "s desde el disparo";
        }
        Chart.SetData(
            plotted.Where(t => t.Tag is not null).Select(t => new ChartSeriesData(t.Name, Shown(t.Tag!), t.Tag!.Step, t.Tag!.IsBoolean)),
            from, now, selectFirst);
        var missing = tags.Where(t => t.Tag is null).Select(t => t.Name).ToList();
        if (_trigger is not null && _triggerSource is null)
            missing.Add($"{_trigger.Ref} (disparo)");
        Missing = missing.Count == 0 ? ""
            : $"{MissingText ?? (IsLive ? "No disponibles en el agente" : "Sin histórico en el agente")}: {string.Join(", ", missing)}";
        Refresh(now);
    }

    /// <summary>
    /// Avanza la ventana hasta <paramref name="now"/> y redibuja (lo llama un temporizador de la vista). Con ventana
    /// fija, solo la primera vez: los datos del histórico no cambian.
    /// </summary>
    public void Refresh(DateTimeOffset now)
    {
        if (FixedWindow is { } window)
        {
            if (_drawn)
                return;
            now = window.To;
        }
        _drawn = true;
        Refilter(now);
        if (!Paused)
        {
            if (IsLive)
                Chart.SetWindow(now - Window.Value, now);
            if (UsesCurves)
                BuildCurves(now);
        }
        Chart.Touch();
    }

    /// <summary>
    /// Puntos que dibuja la gráfica: los del tag o, con disparo, una copia con solo los tramos activos (ciclos y X-Y
    /// recortan ellos mismos por activación).
    /// </summary>
    private List<ChartPoint> Shown(TrendSource tag)
    {
        if (_trigger is null || UsesCurves)
            return tag.Points;
        var shown = new List<ChartPoint>();
        _filtered.Add((tag, shown));
        return shown;
    }

    private void Refilter(DateTimeOffset now)
    {
        if (_trigger is null)
            return;
        var spans = _triggerSource is { } source && TriggerFilter.TryParseTarget(_trigger.Value, out var target)
            ? TriggerFilter.Spans(source.Points, v => TriggerFilter.IsMet(v, _trigger.Condition, target),
                TimeSpan.FromSeconds(Math.Max(0, _trigger.HoldSeconds)), TimeSpan.FromSeconds(Math.Max(0, _trigger.BeforeSeconds)))
            : [];
        TriggerActive = spans.Count > 0 && (spans[^1].To is not { } end || end > now);
        foreach (var (tag, shown) in _filtered)
            TriggerFilter.Apply(tag.Points, spans, now, shown);
    }

    /// <summary>Activaciones del disparo en lo recibido (vacío si no hay disparo o no se puede evaluar).</summary>
    private List<TriggerActivation> Activations()
    {
        if (_trigger is null || _triggerSource is not { } source || !TriggerFilter.TryParseTarget(_trigger.Value, out var target))
            return [];
        return XYData.Activations(source.Points, v => TriggerFilter.IsMet(v, _trigger.Condition, target));
    }

    /// <summary>
    /// Curvas de ciclos (cada activación alineada en 0) o de X-Y (con disparo, una por activación; sin él, la ventana).
    /// La más reciente de cada tag va resaltada.
    /// </summary>
    private void BuildCurves(DateTimeOffset now)
    {
        var before = TimeSpan.FromSeconds(Math.Max(0, _trigger?.BeforeSeconds ?? 0));
        var hold = TimeSpan.FromSeconds(Math.Max(0, _trigger?.HoldSeconds ?? 0));
        var activations = Activations();
        if (activations.Count > MaxCycles)
            activations = activations[^MaxCycles..];
        var curves = new List<XYCurve>();
        var options = Chart.SelectedOptions.ToArray();
        if (_kind == ChartKind.Cycles)
        {
            foreach (var option in options)
            {
                var series = new ChartSeries(option.Name, option.Points, option.Step);
                for (var i = 0; i < activations.Count; i++)
                {
                    var (from, to) = XYData.CycleWindow(activations[i], before, hold, now);
                    curves.Add(new XYCurve(option.Name, XYData.Cycle(series, activations[i].On, from, to), option.ColorIndex,
                        Highlight: i == activations.Count - 1, Step: option.Step, IsBoolean: option.IsBoolean));
                }
            }
            Chart.Caption = activations.Count switch { 0 => "", 1 => "1 ciclo", var n => $"{n} ciclos{(n == MaxCycles ? " (los últimos)" : "")}" };
        }
        else if (_xSource is { } xTag)
        {
            var x = new ChartSeries(_xName, xTag.Points);
            var windows = _trigger is null
                ? [FixedWindow ?? (now - Window.Value, now)]
                : activations.Select(a => XYData.CycleWindow(a, before, hold, now)).ToList();
            foreach (var option in options)
            {
                var y = new ChartSeries(option.Name, option.Points, option.Step);
                for (var i = 0; i < windows.Count; i++)
                    curves.Add(new XYCurve(option.Name, XYData.Pairs(x, y, windows[i].Item1, windows[i].Item2), option.ColorIndex, Highlight: i == windows.Count - 1));
            }
            Chart.Caption = _trigger is null ? "" : activations.Count switch { 0 => "", 1 => "1 ciclo", var n => $"{n} ciclos" };
        }
        Chart.Curves = curves;
    }

    partial void OnWindowChanged(Choice<TimeSpan> value)
    {
        if (IsLive)
            Refresh(DateTimeOffset.UtcNow);
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (!Paused)
        {
            Paused = true;
            return;
        }
        // Seguir: fuera el zoom y la ventana vuelve a avanzar con el reloj.
        if (Chart.IsZoomed)
            Chart.ResetZoomCommand.Execute(null);
        Paused = false;
        Refresh(DateTimeOffset.UtcNow);
    }
}
