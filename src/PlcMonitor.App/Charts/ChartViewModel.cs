using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PlcMonitor.App.Charts;

/// <summary>Tag que se puede mostrar en la gráfica (casilla con su color).</summary>
public sealed partial class ChartSeriesOption(string name, int colorIndex) : ObservableObject
{
    public string Name { get; } = name;

    public int ColorIndex { get; } = colorIndex;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    internal IReadOnlyList<ChartPoint> Points { get; set; } = [];

    internal bool Step { get; set; } = true;

    internal bool IsBoolean { get; set; }

    internal SeriesFormat Display { get; set; } = SeriesFormat.None;

    /// <summary>Serie para dibujar con los datos y el formato de esta opción.</summary>
    internal ChartSeries ToSeries() => new(Name, Points, Step) { IsBoolean = IsBoolean, ColorIndex = ColorIndex, Display = Display };
}

/// <summary>Datos de una serie para cargar en la gráfica.</summary>
public sealed record ChartSeriesData(string Name, IReadOnlyList<ChartPoint> Points, bool Step = true, bool IsBoolean = false)
{
    public SeriesFormat Display { get; init; } = SeriesFormat.None;
}

/// <summary>
/// Estado de una gráfica (la misma para la tendencia en vivo, el histórico y el visor de CSV): qué tags se ven, la
/// ventana de tiempo (con zoom) y los límites del eje Y (automáticos o fijados a mano).
/// </summary>
public sealed partial class ChartViewModel : ObservableObject
{
    // Cada nombre conserva su color aunque cambie la lista.
    private readonly Dictionary<string, int> _colors = new(StringComparer.Ordinal);

    public ObservableCollection<ChartSeriesOption> Options { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ChartSeries> Series { get; private set; } = [];

    /// <summary>Ventana visible.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? From { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? To { get; set; }

    /// <summary>Ventana completa de los datos (a la que vuelve «Ajustar»).</summary>
    public DateTimeOffset? DataFrom { get; private set; }

    public DateTimeOffset? DataTo { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YMin), nameof(YMax), nameof(IsManualY))]
    public partial bool AutoY { get; set; } = true;

    public bool IsManualY => !AutoY;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YMin))]
    public partial decimal? YMinInput { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YMax))]
    public partial decimal? YMaxInput { get; set; }

    public double? YMin => AutoY ? null : (double?)YMinInput;

    public double? YMax => AutoY ? null : (double?)YMaxInput;

    /// <summary>Cambia al añadir puntos a las series para que la gráfica se redibuje.</summary>
    [ObservableProperty]
    public partial int Revision { get; private set; }

    [ObservableProperty]
    public partial bool IsZoomed { get; private set; }

    [ObservableProperty]
    public partial string EmptyText { get; set; } = "Marca uno o más tags";

    public bool HasOptions => Options.Count > 0;

    /// <summary>Tipo de gráfica: la tendencia y el cronograma usan <see cref="Series"/>; ciclos y X-Y, <see cref="Curves"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrend), nameof(IsStates), nameof(IsXY), nameof(IsCycles), nameof(IsHistogram), nameof(IsGauges), nameof(IsBars), nameof(HasYAxis), nameof(Hint), nameof(CanSeparateAxes), nameof(AxisText), nameof(CanShowStats), nameof(StatsVisible))]
    public partial ChartKind Kind { get; set; } = ChartKind.Trend;

    public bool IsTrend => Kind == ChartKind.Trend;

    public bool IsStates => Kind == ChartKind.States;

    public bool IsCycles => Kind == ChartKind.Cycles;

    public bool IsHistogram => Kind == ChartKind.Histogram;

    public bool IsGauges => Kind == ChartKind.Gauges;

    public bool IsBars => Kind == ChartKind.Bars;

    /// <summary>Texto de la casilla de los límites automáticos según qué eje son.</summary>
    public string AxisText => Kind switch
    {
        ChartKind.Histogram => "Eje de valores automático",
        ChartKind.Gauges => "Escala automática",
        _ => "Eje Y automático",
    };

    /// <summary>Cursores de medida A y B en la tendencia (al quitarlos se borran).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    public partial bool ShowCursors { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? CursorA { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? CursorB { get; set; }

    partial void OnShowCursorsChanged(bool value)
    {
        if (!value)
            (CursorA, CursorB) = (null, null);
    }

    /// <summary>Tendencia con un eje Y por tag.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasYAxis))]
    public partial bool SeparateAxes { get; set; }

    /// <summary>Se ofrece separar los ejes: tendencia con más de un tag.</summary>
    public bool CanSeparateAxes => Kind == ChartKind.Trend && Options.Count > 1;

    /// <summary>Tolerancias del histograma y límites de los indicadores (null = sin límite).</summary>
    [ObservableProperty]
    public partial double? LowLimit { get; set; }

    [ObservableProperty]
    public partial double? HighLimit { get; set; }

    [ObservableProperty]
    public partial GaugeStyle GaugeStyle { get; set; }

    /// <summary>Barras: periodo de cada barra (para las etiquetas) y hora de inicio del primer turno.</summary>
    [ObservableProperty]
    public partial BarPeriod BarPeriod { get; set; }

    [ObservableProperty]
    public partial int ShiftStartHour { get; set; } = 6;

    /// <summary>Título de la imagen exportada (el nombre de la vista o de lo que se consulta).</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = "";

    /// <summary>De dónde salen los datos, para la cabecera de la imagen (el agente o el CSV).</summary>
    [ObservableProperty]
    public partial string Source { get; set; } = "";

    /// <summary>Tabla de estadísticas bajo la gráfica (solo en las de tiempo: tendencia y cronograma).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsVisible))]
    public partial bool ShowStats { get; set; }

    public bool CanShowStats => Kind is ChartKind.Trend or ChartKind.States;

    public bool StatsVisible => ShowStats && CanShowStats;

    /// <summary>Estadísticas de los tags marcados en la ventana visible (vacía si no se muestran).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SeriesStatsRow> Stats { get; private set; } = [];

    partial void OnShowStatsChanged(bool value) => RefreshStats();

    partial void OnFromChanged(DateTimeOffset? value) => RefreshStats();

    partial void OnToChanged(DateTimeOffset? value) => RefreshStats();

    partial void OnRevisionChanged(int value) => RefreshStats();

    partial void OnKindChanged(ChartKind value) => RefreshStats();

    /// <summary>Recalcula <see cref="Stats"/> si se ven (con la ventana visible, zoom incluido).</summary>
    public void RefreshStats()
    {
        if (!StatsVisible || From is not { } from || To is not { } to)
        {
            if (Stats.Count > 0)
                Stats = [];
            return;
        }
        Stats = Series.Select(s => SeriesStats.Compute(s, from, to)).ToArray();
    }

    /// <summary>Gráfica con eje X numérico (ciclos superpuestos o X-Y).</summary>
    public bool IsXY => Kind is ChartKind.Cycles or ChartKind.XY;

    /// <summary>Límites del eje Y a mano: no en el cronograma ni con un eje por tag. En el histograma son los del eje X; en los indicadores, la escala.</summary>
    public bool HasYAxis => Kind != ChartKind.States && !(Kind == ChartKind.Trend && SeparateAxes && Options.Count > 1);

    /// <summary>Curvas de los ciclos superpuestos o de la X-Y (las calcula quien alimenta la gráfica).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<XYCurve> Curves { get; set; } = [];

    /// <summary>Texto del eje X de las gráficas X-Y y de ciclos.</summary>
    [ObservableProperty]
    public partial string XLabel { get; set; } = "";

    /// <summary>Resumen junto a la ayuda (p. ej. cuántos ciclos se ven).</summary>
    [ObservableProperty]
    public partial string Caption { get; set; } = "";

    /// <summary>El resumen avisa de algo (el último ciclo se sale de la referencia): en rojo.</summary>
    [ObservableProperty]
    public partial bool CaptionAlarm { get; set; }

    /// <summary>Bandas de tolerancia de los ciclos de referencia.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ReferenceBand> Bands { get; set; } = [];

    public string Hint => Kind switch
    {
        ChartKind.Cycles => "Cada ciclo empieza en 0 (el disparo); el último va resaltado y los anteriores, tenues.",
        ChartKind.XY => "Pasa el ratón para ver las coordenadas; el punto marca el valor más reciente.",
        ChartKind.States => "Arrastra para ampliar un tramo; pasa el ratón por una franja para ver el estado y cuánto duró.",
        ChartKind.Histogram => "Valores de la ventana. Líneas: media (negra), ±3σ (discontinuas) y tolerancias (rojas). Pasa el ratón por una barra.",
        ChartKind.Gauges => "Valor actual de cada tag; en rojo si está fuera de los límites.",
        ChartKind.Bars => "Un grupo de barras por periodo. Pasa el ratón para ver los valores.",
        _ when ShowCursors => "Clic para poner los cursores A y B; arrástralos para moverlos. Quita «Cursores» para volver al zoom.",
        _ => "Arrastra sobre la gráfica para ampliar un tramo; pasa el ratón para ver los valores.",
    };

    /// <summary>Tags marcados, con sus puntos.</summary>
    internal IEnumerable<ChartSeriesOption> SelectedOptions => Options.Where(o => o.IsSelected);

    /// <summary>El usuario ha hecho zoom o lo ha quitado (la tendencia en vivo se pausa con el zoom).</summary>
    public event EventHandler? ZoomChanged;

    /// <summary>
    /// Carga las series. Conserva lo marcado por nombre; si no había nada, marca las <paramref name="selectFirst"/>
    /// primeras. La ventana vuelve a la de los datos.
    /// </summary>
    public void SetData(IEnumerable<ChartSeriesData> data, DateTimeOffset? from = null, DateTimeOffset? to = null, int selectFirst = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(data);
        var selected = Options.Where(o => o.IsSelected).Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        var keepSelection = selected.Count > 0;
        foreach (var option in Options)
            option.PropertyChanged -= OnOptionChanged;
        Options.Clear();
        foreach (var d in data)
        {
            if (!_colors.TryGetValue(d.Name, out var color))
                _colors[d.Name] = color = _colors.Count;
            var option = new ChartSeriesOption(d.Name, color)
            {
                Points = d.Points,
                Step = d.Step,
                IsBoolean = d.IsBoolean,
                Display = d.Display,
                IsSelected = keepSelection ? selected.Contains(d.Name) : Options.Count < selectFirst,
            };
            option.PropertyChanged += OnOptionChanged;
            Options.Add(option);
        }
        OnPropertyChanged(nameof(HasOptions));
        OnPropertyChanged(nameof(CanSeparateAxes));
        OnPropertyChanged(nameof(HasYAxis));
        var all = Options.SelectMany(o => o.Points).Select(p => p.Time).ToArray();
        DataFrom = from ?? (all.Length > 0 ? all.Min() : null);
        DataTo = to ?? (all.Length > 0 ? all.Max() : null);
        From = DataFrom;
        To = DataTo;
        IsZoomed = false;
        Rebuild();
    }

    /// <summary>Mueve la ventana sin zoom (la tendencia en vivo la desplaza con el reloj).</summary>
    public void SetWindow(DateTimeOffset from, DateTimeOffset to)
    {
        DataFrom = from;
        DataTo = to;
        if (IsZoomed)
            return;
        From = from;
        To = to;
    }

    /// <summary>Zoom a un tramo (al arrastrar sobre la gráfica).</summary>
    public void Zoom(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
            return;
        From = from;
        To = to;
        IsZoomed = true;
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Vuelve a la ventana completa de los datos.</summary>
    [RelayCommand]
    private void ResetZoom()
    {
        From = DataFrom;
        To = DataTo;
        IsZoomed = false;
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Pone en el eje Y los límites de lo que se ve ahora (para ajustarlos a partir de ahí).</summary>
    partial void OnAutoYChanged(bool value)
    {
        if (value || (YMinInput is not null && YMaxInput is not null))
            return;
        var values = Series.SelectMany(s => s.Points).Where(p => p.Value is not null && (From is null || p.Time >= From) && (To is null || p.Time <= To))
            .Select(p => p.Value!.Value).ToArray();
        if (values.Length == 0)
            return;
        var (min, max, _) = ChartAxis.NiceRange(values.Min(), values.Max());
        YMinInput ??= (decimal)min;
        YMaxInput ??= (decimal)max;
    }

    /// <summary>Hay puntos nuevos en las series (mismas listas): redibujar.</summary>
    public void Touch() => Revision++;

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChartSeriesOption.IsSelected))
            Rebuild();
    }

    private void Rebuild()
    {
        Series = Options.Where(o => o.IsSelected)
            .Select(o => o.ToSeries())
            .ToArray();
        Revision++;
    }
}
