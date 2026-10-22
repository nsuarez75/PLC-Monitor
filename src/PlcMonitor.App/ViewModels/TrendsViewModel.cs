using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcMonitor.App.Charts;
using PlcMonitor.App.Services;

namespace PlcMonitor.App.ViewModels;

/// <summary>Vista de tendencia en la lista del panel.</summary>
public sealed partial class TrendViewItem(TrendView view) : ObservableObject
{
    public TrendView View { get; } = view;

    [ObservableProperty]
    public partial string Name { get; set; } = view.Name;

    [ObservableProperty]
    public partial string Description { get; set; } = Describe(view);

    public void Refresh()
    {
        Name = View.Name;
        Description = Describe(View);
    }

    private static string Describe(TrendView view)
    {
        var kind = view.Kind == ChartKind.Trend ? "" : $"{ChartKindChoice.For(view.Kind).Short} · ";
        var tags = view.Tags.Count switch { 0 => "sin tags", 1 => "1 tag", var n => $"{n} tags" };
        if (view.Tags.Count == 0)
            return kind + tags;
        var csv = view.DataSource == TrendDataSource.Csv && view.Kind != ChartKind.Gauges ? $" · CSV {Path.GetFileName(view.CsvPath)}" : "";
        if (view.Kind == ChartKind.Bars)
            return $"{kind}{tags} · por {PeriodBarsViewModel.Periods.First(p => p.Value == view.BarPeriod).Text.ToLowerInvariant()}{csv}";
        if (csv.Length > 0)
            return $"{kind}{tags}{csv}{(view.Trigger is null ? "" : " · disparo")}";
        if (view.DataSource == TrendDataSource.History && TrendsViewModel.CanReadHistory(view.Kind))
            return $"{kind}{tags} · histórico, {TrendHistory.Describe(view)}{(view.Trigger is null ? "" : " · disparo")}";
        return $"{kind}{tags} · {view.WindowMinutes} min{(view.Trigger is null ? "" : " · disparo")}";
    }
}

/// <summary>Tipo de gráfica que se puede elegir para una vista, con su explicación.</summary>
public sealed record ChartKindChoice(ChartKind Value, string Text, string Short, string Description)
{
    public static IReadOnlyList<ChartKindChoice> All { get; } =
    [
        new(ChartKind.Trend, "Tendencia (valor / tiempo)", "Tendencia",
            "La gráfica de siempre: el valor de cada tag a lo largo del tiempo, con zoom arrastrando y los valores al pasar el ratón."),
        new(ChartKind.Cycles, "Ciclos superpuestos", "Ciclos",
            "Cada vez que salta el disparo se dibuja un ciclo que empieza en 0, encima de los anteriores (como la persistencia de un osciloscopio): el último resaltado y los demás tenues. Sirve para comparar ciclos de máquina y ver el que se sale. Necesita un disparo."),
        new(ChartKind.XY, "X-Y (un tag contra otro)", "X-Y",
            "Un tag frente a otro en vez de frente al tiempo, p. ej. fuerza contra posición en una prensa o presión contra caudal. El primer tag de la vista es el eje X y los demás, el Y. Con disparo, una curva por ciclo."),
        new(ChartKind.States, "Cronograma de estados", "Estados",
            "Una franja por tag con un color por estado (marcha/paro/alarma, paso de secuencia…), la duración de cada tramo al pasar el ratón y el porcentaje de tiempo en cada estado. Pensado para booleanos y enteros de estado."),
        new(ChartKind.Histogram, "Histograma con Cp/Cpk", "Histograma",
            "Cómo se reparten los valores de la ventana (en vivo o del histórico): barras por clase, media, desviación típica, mínimo y máximo y, con tolerancias, la capacidad del proceso (Cp y Cpk). Con disparo, solo los valores de cada ciclo."),
        new(ChartKind.Gauges, "Indicadores de panel", "Indicadores",
            "El valor actual de cada tag en grande, como en un panel de operador: con aguja, barra de nivel o solo el número, y en rojo si sale de los límites. Los booleanos se ven como una luz."),
        new(ChartKind.Bars, "Barras por periodo (histórico)", "Barras",
            "Un grupo de barras por hora, turno o día con el máximo, la media, el mínimo o lo que ha subido un contador, sacado del histórico del agente (los tags tienen que historizarse). No es en vivo: se actualiza con un botón."),
    ];

    public static ChartKindChoice For(ChartKind kind) => All.FirstOrDefault(k => k.Value == kind) ?? All[0];

    public override string ToString() => Text;
}

/// <summary>Gráfica de una vista abierta: la de tiempo/ciclos/X-Y (<see cref="Live"/>) o las barras; o por qué no se ha podido abrir.</summary>
public sealed record OpenedView(LiveTrendViewModel? Live, PeriodBarsViewModel? Bars, string Error = "");

/// <summary>Tag del agente que se puede añadir a una vista.</summary>
public sealed record TrendTagChoice(string Device, string Tag, string DataType)
{
    public TrendTagRef Ref => new(Device, Tag);

    public string Text => $"{Device} / {Tag}";
}

/// <summary>
/// Panel de tendencias: vistas guardadas (nombre, tags de cualquier dispositivo del agente, ventana y eje Y) que se
/// configuran antes y se abren cuando se quiere ver su gráfica en vivo.
/// </summary>
public sealed partial class TrendsViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly string _path;
    private readonly List<TrendView> _all;

    public TrendsViewModel(MainViewModel main, string path)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _path = path;
        _all = TrendViewStore.Load(path);
        _panelsPath = TrendPanelStore.PathNextTo(path);
        _allPanels = TrendPanelStore.Load(_panelsPath);
        Tags.CollectionChanged += (_, _) => OnPropertyChanged(nameof(XAxisText));
        // Las vistas de CSV no dependen del agente: se ven siempre.
        foreach (var view in _all.Where(v => v.Agent == main.AgentKey || v.DataSource == TrendDataSource.Csv))
            Views.Add(new TrendViewItem(view));
        foreach (var panel in _allPanels.Where(p => p.Agent == main.AgentKey))
            Panels.Add(new TrendPanelItem(panel));
        _main.TagsChanged += OnTagsChanged;
        Selected = Views.FirstOrDefault();
    }

    public ObservableCollection<TrendViewItem> Views { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderTitle))]
    public partial TrendViewItem? Selected { get; set; }

    /// <summary>Título de la página: el panel o la vista elegidos.</summary>
    public string HeaderTitle => SelectedPanel?.Name ?? Selected?.Name ?? "Tendencias";

    public string HeaderHint => SelectedPanel is null
        ? "Tags del agente o de un CSV en una gráfica: en vivo, del histórico o del fichero."
        : "Varias vistas guardadas en una rejilla, cada una con sus datos y su tipo de gráfica.";

    /// <summary>Gráfica abierta (null = configurando la vista).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(IsViewing), nameof(IsViewingLive))]
    public partial LiveTrendViewModel? Live { get; private set; }

    public bool IsViewing => Live is not null || Bars is not null;

    public bool IsEditing => !IsViewing && Selected is not null;

    public bool HasViews => Views.Count > 0;

    // ---- Formulario de la vista seleccionada (se aplica con Guardar o al abrir) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsXYKind), nameof(IsTrendKind), nameof(IsGaugesKind), nameof(IsBarsKind), nameof(HasLimits), nameof(LimitsText),
        nameof(LimitsHint), nameof(UsesLiveWindow), nameof(CanTrigger), nameof(AxisCaption), nameof(HasAxisLimits), nameof(CanUseHistory), nameof(UsesHistory), nameof(UsesCsv), nameof(ShowHistoryRange), nameof(IsCustomRange),
        nameof(AvailableCaption))]
    public partial ChartKindChoice Kind { get; set; } = ChartKindChoice.All[0];

    /// <summary>X-Y: el primer tag es el eje X.</summary>
    public bool IsXYKind => Kind.Value == ChartKind.XY;

    public bool IsTrendKind => Kind.Value == ChartKind.Trend;

    /// <summary>El cronograma no tiene eje de valores.</summary>
    public bool HasAxisLimits => Kind.Value != ChartKind.States;

    /// <summary>Rótulo de los límites mín/máx: el eje Y, el de valores del histograma o la escala de los indicadores.</summary>
    public string AxisCaption => Kind.Value switch
    {
        ChartKind.Histogram => "Eje de valores",
        ChartKind.Gauges => "Escala",
        _ => "Eje Y",
    };

    public bool IsGaugesKind => Kind.Value == ChartKind.Gauges;

    public bool IsBarsKind => Kind.Value == ChartKind.Bars;

    /// <summary>Tolerancias (histograma) o límites (indicadores).</summary>
    public bool HasLimits => Kind.Value is ChartKind.Histogram or ChartKind.Gauges;

    public string LimitsText => Kind.Value == ChartKind.Histogram ? "Tolerancias" : "Límites";

    public string LimitsHint => Kind.Value == ChartKind.Histogram
        ? "Inferior y superior de especificación (LIE/LSE) para calcular Cp y Cpk; con una sola, solo Cpk."
        : "Fuera de ellos el indicador se pone en rojo.";

    /// <summary>La ventana en vivo cuenta (no en ciclos, en barras ni leyendo del histórico o de un CSV).</summary>
    public bool UsesLiveWindow => Kind.Value is not (ChartKind.Cycles or ChartKind.Bars) && !UsesHistory && !UsesCsv;

    /// <summary>Tipos que pueden leer datos grabados (histórico o CSV): todos menos los indicadores, que son el valor actual.</summary>
    public static bool CanReadHistory(ChartKind kind) => kind != ChartKind.Gauges;

    public bool CanUseHistory => CanReadHistory(Kind.Value);

    public static IReadOnlyList<Choice<TrendDataSource>> DataSources { get; } =
    [
        new("En vivo (lo que llega, última hora)", TrendDataSource.Live),
        new("Histórico del agente", TrendDataSource.History),
        new("Fichero CSV exportado", TrendDataSource.Csv),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesHistory), nameof(UsesCsv), nameof(UsesLiveWindow), nameof(IsCustomRange), nameof(ShowHistoryRange), nameof(AvailableCaption))]
    public partial Choice<TrendDataSource> DataSource { get; set; } = DataSources[0];

    /// <summary>La vista lee del histórico del agente (si el tipo lo admite).</summary>
    public bool UsesHistory => CanUseHistory && DataSource.Value == TrendDataSource.History;

    /// <summary>La vista lee de un CSV exportado (sus tags son los del fichero).</summary>
    public bool UsesCsv => CanUseHistory && DataSource.Value == TrendDataSource.Csv;

    /// <summary>Se elige el periodo del histórico (las barras tienen el suyo).</summary>
    public bool ShowHistoryRange => UsesHistory && Kind.Value != ChartKind.Bars;

    public string AvailableCaption => UsesCsv ? "TAGS DEL CSV" : "TAGS DEL AGENTE";

    /// <summary>Fichero CSV de la vista.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CsvText))]
    public partial string? CsvPath { get; set; }

    public string CsvText => CsvPath is { Length: > 0 } path ? Path.GetFileName(path) : "Ningún fichero elegido";

    /// <summary>Por qué no se ha podido leer el CSV (vacío si se ha leído).</summary>
    [ObservableProperty]
    public partial string CsvError { get; private set; } = "";

    private HistoryCsvContent? _csv;

    partial void OnCsvPathChanged(string? value)
    {
        _csv = null;
        CsvError = "";
        KindError = "";
        if (value is { Length: > 0 } path)
        {
            try
            {
                _csv = HistoryCsv.Load(path);
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                CsvError = ex.Message;
            }
        }
        RefreshAvailable();
    }

    partial void OnDataSourceChanged(Choice<TrendDataSource> value) => RefreshAvailable();

    // Los indicadores solo van en vivo; las barras, del histórico o de un CSV.
    partial void OnKindChanged(ChartKindChoice value)
    {
        if (value.Value == ChartKind.Gauges && DataSource.Value != TrendDataSource.Live)
            DataSource = DataSources[0];
        else if (value.Value == ChartKind.Bars && DataSource.Value == TrendDataSource.Live)
            DataSource = DataSources[1];
    }

    [RelayCommand]
    private async Task ChooseCsvAsync()
    {
        if (await _main.Dialogs.OpenFileAsync("Abrir histórico (CSV)", "csv") is { } path)
            CsvPath = path;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomRange))]
    public partial Choice<int> HistoryRange { get; set; } = TrendHistory.Ranges.First(r => r.Value == 480);

    /// <summary>Periodo con fechas concretas (desde/hasta).</summary>
    public bool IsCustomRange => ShowHistoryRange && HistoryRange.Value == 0;

    [ObservableProperty]
    public partial string HistoryFromText { get; set; } = "";

    [ObservableProperty]
    public partial string HistoryToText { get; set; } = "";

    /// <summary>Consultando el histórico al abrir.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    partial void OnHistoryRangeChanged(Choice<int> value)
    {
        // Al pasar a fechas concretas, propone el último turno de 8 h que acaba ahora.
        if (value.Value != 0 || HistoryFromText.Length > 0)
            return;
        var now = DateTimeOffset.Now;
        var to = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);
        HistoryFromText = TrendHistory.Format(to.AddHours(-8));
        HistoryToText = TrendHistory.Format(to);
    }

    /// <summary>El disparo tiene sentido (no en barras del histórico ni en indicadores del valor actual).</summary>
    public bool CanTrigger => Kind.Value is not (ChartKind.Bars or ChartKind.Gauges);

    [ObservableProperty]
    public partial bool SeparateAxes { get; set; }

    [ObservableProperty]
    public partial decimal? LowLimit { get; set; }

    [ObservableProperty]
    public partial decimal? HighLimit { get; set; }

    public static IReadOnlyList<Choice<Charts.GaugeStyle>> GaugeStyles { get; } =
    [
        new("Aguja", Charts.GaugeStyle.Needle),
        new("Barra de nivel", Charts.GaugeStyle.Level),
        new("Solo el valor", Charts.GaugeStyle.Value),
    ];

    [ObservableProperty]
    public partial Choice<Charts.GaugeStyle> GaugeStyle { get; set; } = GaugeStyles[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShiftPeriod))]
    public partial Choice<Charts.BarPeriod> BarPeriod { get; set; } = PeriodBarsViewModel.Periods[0];

    public bool IsShiftPeriod => BarPeriod.Value == Charts.BarPeriod.Shift;

    [ObservableProperty]
    public partial Choice<Charts.BarMetric> BarMetric { get; set; } = PeriodBarsViewModel.Metrics[0];

    [ObservableProperty]
    public partial Choice<int> BarRange { get; set; } = PeriodBarsViewModel.Ranges[0];

    /// <summary>Hora a la que empieza el primer turno (los otros dos, cada 8 h).</summary>
    [ObservableProperty]
    public partial decimal ShiftStartHour { get; set; } = 6;

    /// <summary>Barras del histórico abiertas (null = no).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(IsViewing), nameof(IsViewingBars), nameof(IsViewingLive))]
    public partial PeriodBarsViewModel? Bars { get; private set; }

    public bool IsViewingBars => Bars is not null;

    public bool IsViewingLive => Live is not null;

    public string XAxisText => Tags.Count > 0 ? $"Eje X: {Tags[0]}" : "Eje X: el primer tag que añadas";

    /// <summary>Lo que le falta a la vista para el tipo elegido (disparo, tags).</summary>
    [ObservableProperty]
    public partial string KindError { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(OpenCommand))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial Choice<TimeSpan> Window { get; set; } = LiveTrendViewModel.Windows[1];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualY))]
    public partial bool AutoY { get; set; } = true;

    public bool IsManualY => !AutoY;

    [ObservableProperty]
    public partial decimal? YMin { get; set; }

    [ObservableProperty]
    public partial decimal? YMax { get; set; }

    /// <summary>Tags de la vista.</summary>
    public ObservableCollection<TrendTagRef> Tags { get; } = [];

    /// <summary>Tags del agente que aún no están en la vista (filtrados por <see cref="Search"/>).</summary>
    public ObservableCollection<TrendTagChoice> Available { get; } = [];

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial string Note { get; private set; } = "";

    // ---- Disparo: la vista solo muestra los valores mientras se cumple (más un tiempo después) ----

    [ObservableProperty]
    public partial bool TriggerEnabled { get; set; }

    /// <summary>Tags del agente que pueden disparar (los dibujables, estén o no en la vista).</summary>
    public ObservableCollection<TrendTagChoice> TriggerChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBooleanTrigger), nameof(IsValueTrigger))]
    public partial TrendTagChoice? TriggerTag { get; set; }

    /// <summary>El disparo es booleano: se elige true o false; si no, condición y valor.</summary>
    public bool IsBooleanTrigger => TriggerTag is { } t && (t.DataType == "Boolean" || _main.FindTag(t.Device, t.Tag)?.IsBoolean == true);

    public bool IsValueTrigger => !IsBooleanTrigger;

    [ObservableProperty]
    public partial Choice<PlcMonitor.Agent.Contracts.TriggerCondition> TriggerCondition { get; set; } = TagDetailsViewModel.Conditions[0];

    [ObservableProperty]
    public partial string TriggerValue { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TriggerBool))]
    public partial bool TriggerOnTrue { get; set; } = true;

    public Choice<bool> TriggerBool
    {
        get => TagDetailsViewModel.BoolChoices[TriggerOnTrue ? 0 : 1];
        set
        {
            if (value is not null)
                TriggerOnTrue = value.Value;
        }
    }

    /// <summary>Segundos que se siguen mostrando después de que la condición deje de cumplirse.</summary>
    [ObservableProperty]
    public partial decimal TriggerHoldSeconds { get; set; }

    /// <summary>Segundos de antes de que se cumpla que también se muestran (pre-disparo).</summary>
    [ObservableProperty]
    public partial decimal TriggerBeforeSeconds { get; set; }

    partial void OnSelectedChanged(TrendViewItem? value)
    {
        if (value is not null)
            SelectedPanel = null;
        Live = null;
        Bars = null;
        LoadForm(value?.View);
        OnPropertyChanged(nameof(IsEditing));
    }

    partial void OnSearchChanged(string value) => RefreshAvailable();

    private void LoadForm(TrendView? view)
    {
        Name = view?.Name ?? "";
        Kind = ChartKindChoice.For(view?.Kind ?? ChartKind.Trend);
        KindError = "";
        SeparateAxes = view?.SeparateAxes ?? false;
        CsvPath = view?.CsvPath;
        DataSource = DataSources.FirstOrDefault(d => d.Value == view?.DataSource) ?? DataSources[0];
        HistoryFromText = view?.HistoryFrom is { } historyFrom ? TrendHistory.Format(historyFrom) : "";
        HistoryToText = view?.HistoryTo is { } historyTo ? TrendHistory.Format(historyTo) : "";
        HistoryRange = TrendHistory.Ranges.FirstOrDefault(r => r.Value == view?.HistoryMinutes) ?? TrendHistory.Ranges.First(r => r.Value == 480);
        LowLimit = view?.LowLimit is { } low ? (decimal)low : null;
        HighLimit = view?.HighLimit is { } high ? (decimal)high : null;
        GaugeStyle = GaugeStyles.FirstOrDefault(g => g.Value == view?.GaugeStyle) ?? GaugeStyles[0];
        BarPeriod = PeriodBarsViewModel.Periods.FirstOrDefault(p => p.Value == view?.BarPeriod) ?? PeriodBarsViewModel.Periods[0];
        BarMetric = PeriodBarsViewModel.Metrics.FirstOrDefault(m => m.Value == view?.BarMetric) ?? PeriodBarsViewModel.Metrics[0];
        BarRange = PeriodBarsViewModel.Ranges.FirstOrDefault(r => r.Value == view?.BarDays) ?? PeriodBarsViewModel.Ranges[0];
        ShiftStartHour = view?.ShiftStartHour ?? 6;
        Window = LiveTrendViewModel.Windows.FirstOrDefault(w => (int)w.Value.TotalMinutes == view?.WindowMinutes) ?? LiveTrendViewModel.Windows[1];
        AutoY = view?.AutoY ?? true;
        YMin = view?.YMin is { } min ? (decimal)min : null;
        YMax = view?.YMax is { } max ? (decimal)max : null;
        Tags.Clear();
        foreach (var tag in view?.Tags ?? [])
            Tags.Add(tag);
        Search = "";
        RefreshAvailable();
        var trigger = view?.Trigger;
        TriggerEnabled = trigger is not null;
        TriggerTag = trigger is null ? null : TriggerChoices.FirstOrDefault(c => c.Ref == trigger.Ref) ?? AddMissingTrigger(trigger.Ref);
        TriggerCondition = TagDetailsViewModel.Conditions.FirstOrDefault(c => c.Value == trigger?.Condition) ?? TagDetailsViewModel.Conditions[0];
        TriggerValue = trigger?.Value ?? "";
        TriggerOnTrue = !string.Equals(trigger?.Value?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        TriggerHoldSeconds = (decimal)(trigger?.HoldSeconds ?? 0);
        TriggerBeforeSeconds = (decimal)(trigger?.BeforeSeconds ?? 0);
    }

    // El tag disparo guardado aunque el agente no lo tenga (desconectado o borrado): se sigue viendo en el desplegable.
    private TrendTagChoice AddMissingTrigger(TrendTagRef tag)
    {
        var choice = new TrendTagChoice(tag.Device, tag.Tag, "");
        TriggerChoices.Add(choice);
        return choice;
    }

    private TrendTrigger? BuildTrigger() => TriggerEnabled && CanTrigger && TriggerTag is { } tag
        ? new TrendTrigger
        {
            Device = tag.Device,
            Tag = tag.Tag,
            Condition = IsBooleanTrigger ? PlcMonitor.Agent.Contracts.TriggerCondition.Equal : TriggerCondition.Value,
            Value = IsBooleanTrigger ? (TriggerOnTrue ? "true" : "false") : TriggerValue.Trim(),
            HoldSeconds = (double)TriggerHoldSeconds,
            BeforeSeconds = (double)TriggerBeforeSeconds,
        }
        : null;

    /// <summary>Tags que se pueden añadir: los del CSV o los del agente conectado (dibujables) que no estén ya.</summary>
    private void RefreshAvailable()
    {
        var all = UsesCsv
            ? (_csv?.Keys.Select((k, i) => new TrendTagChoice(k.Device, k.Tag, _csv.Series[i].IsBoolean ? "Boolean" : "Número")) ?? []).ToList()
            : _main.Devices.SelectMany(d => d.Tags.Where(t => t.IsChartable).Select(t => new TrendTagChoice(d.Name, t.Name, t.DataType))).ToList();
        var trigger = TriggerTag?.Ref;
        TriggerChoices.Clear();
        foreach (var choice in all)
            TriggerChoices.Add(choice);
        if (trigger is not null)
            TriggerTag = TriggerChoices.FirstOrDefault(c => c.Ref == trigger) ?? AddMissingTrigger(trigger);
        Available.Clear();
        var search = Search.Trim();
        foreach (var choice in all)
        {
            if (Tags.Contains(choice.Ref))
                continue;
            if (search.Length > 0 && !choice.Text.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            Available.Add(choice);
        }
        Note = UsesCsv ? (_csv is null ? "Elige un fichero CSV exportado del histórico para ver sus tags." : "")
            : _main.IsConnected ? "" : "Conéctate al agente para ver sus tags y añadirlos.";
    }

    /// <summary>Añade una vista ya hecha (p. ej. desde la pestaña Histórico de un dispositivo), la selecciona y la abre.</summary>
    public async Task AddAndOpenAsync(TrendView view)
    {
        view.Agent = _main.AgentKey;
        _all.Add(view);
        var item = new TrendViewItem(view);
        Views.Add(item);
        OnPropertyChanged(nameof(HasViews));
        Selected = item;
        Persist();
        await OpenCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void New()
    {
        var view = new TrendView { Name = $"Tendencia {Views.Count + 1}", Agent = _main.AgentKey };
        _all.Add(view);
        var item = new TrendViewItem(view);
        Views.Add(item);
        OnPropertyChanged(nameof(HasViews));
        Selected = item;
        Persist();
    }

    [RelayCommand]
    private void AddTag(TrendTagChoice? choice)
    {
        if (choice is null || Tags.Contains(choice.Ref))
            return;
        Tags.Add(choice.Ref);
        Available.Remove(choice);
    }

    /// <summary>X-Y: pone el tag el primero (eje X).</summary>
    [RelayCommand]
    private void MakeXAxis(TrendTagRef? tag)
    {
        if (tag is null || Tags.IndexOf(tag) <= 0)
            return;
        Tags.Remove(tag);
        Tags.Insert(0, tag);
    }

    /// <summary>Añade todos los tags de la lista (los que deja la búsqueda).</summary>
    [RelayCommand]
    private void AddAll()
    {
        foreach (var choice in Available.ToArray())
            AddTag(choice);
    }

    [RelayCommand]
    private void RemoveTag(TrendTagRef? tag)
    {
        if (tag is null)
            return;
        Tags.Remove(tag);
        RefreshAvailable();
    }

    private bool CanSave() => Selected is not null && !string.IsNullOrWhiteSpace(Name);

    /// <summary>
    /// La vista se puede abrir: el disparo está completo (tag y, si no es booleano, un número) y el tipo tiene lo que
    /// necesita; si no, se explica en <see cref="TriggerError"/> o <see cref="KindError"/>.
    /// </summary>
    private bool CheckTrigger()
    {
        KindError = Kind.Value switch
        {
            ChartKind.Cycles when !TriggerEnabled => "Los ciclos superpuestos necesitan un disparo: actívalo abajo.",
            ChartKind.XY when Tags.Count < 2 => "La gráfica X-Y necesita al menos dos tags: el primero es el eje X.",
            ChartKind.Histogram or ChartKind.Gauges when LowLimit is { } low && HighLimit is { } high && low >= high
                => $"{LimitsText}: la inferior tiene que ser menor que la superior.",
            _ when UsesCsv && CsvPath is not { Length: > 0 } => "Elige el fichero CSV.",
            _ when UsesCsv && CsvError.Length > 0 => $"No se puede leer el CSV: {CsvError}",
            _ when IsCustomRange && !(TrendHistory.TryParse(HistoryFromText, out var from) && TrendHistory.TryParse(HistoryToText, out var to) && from < to)
                => "Periodo: escribe las fechas como 17/10/2026 06:00, con «desde» antes que «hasta».",
            _ => "",
        };
        TriggerError = !TriggerEnabled || !CanTrigger ? ""
            : TriggerTag is null ? "Elige el tag de disparo."
            : IsValueTrigger && !TriggerFilter.TryParseTarget(TriggerValue, out _) ? "El valor del disparo debe ser un número."
            : "";
        return TriggerError.Length == 0 && KindError.Length == 0;
    }

    [ObservableProperty]
    public partial string TriggerError { get; private set; } = "";

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (Selected is not { } item || !CheckTrigger())
            return;
        Apply(item);
        _main.Report($"Tendencia «{item.Name}» guardada");
    }

    /// <summary>Guarda y abre la gráfica de la vista: en vivo, del histórico (lo consulta) o las barras.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task OpenAsync()
    {
        if (Selected is not { } item || !CheckTrigger())
            return;
        Apply(item);
        IsLoading = true;
        OpenedView opened;
        try
        {
            opened = await BuildAsync(item.View);
        }
        finally
        {
            IsLoading = false;
        }
        if (opened.Error.Length > 0)
        {
            KindError = opened.Error;
            return;
        }
        if (Selected != item)
            return; // se ha elegido otra vista mientras se leía
        Live = opened.Live;
        Bars = opened.Bars;
    }

    /// <summary>
    /// Gráfica de una vista guardada, como al abrirla: en vivo, del histórico (lo consulta), de su CSV o las barras.
    /// Sirve para la vista abierta y para cada celda de un panel.
    /// </summary>
    internal async Task<OpenedView> BuildAsync(TrendView view)
    {
        if (view.DataSource == TrendDataSource.Csv && CanReadHistory(view.Kind))
            return BuildCsv(view);
        if (view.Kind == ChartKind.Bars)
        {
            var bars = new PeriodBarsViewModel(_main, view, t => _main.FindTag(t.Device, t.Tag));
            ConfigureBars(bars, view);
            _ = bars.RefreshCommand.ExecuteAsync(null);
            return new OpenedView(null, bars);
        }
        if (view.DataSource == TrendDataSource.History && CanReadHistory(view.Kind))
            return await BuildHistoryAsync(view);
        var trigger = view.Trigger;
        var live = new LiveTrendViewModel(
            () => view.Tags.Select(t => (t.ToString(), TrendSource.Of(_main.FindTag(t.Device, t.Tag)))).ToArray(),
            trigger,
            trigger is null ? null : () => TrendSource.Of(_main.FindTag(trigger.Device, trigger.Tag)),
            view.Kind)
        {
            Window = LiveTrendViewModel.Windows.FirstOrDefault(w => (int)w.Value.TotalMinutes == view.WindowMinutes) ?? LiveTrendViewModel.Windows[1],
        };
        live.Chart.EmptyText = view.Tags.Count == 0 ? "Esta vista no tiene tags: edítala y añade alguno"
            : trigger is not null ? "Esperando el disparo"
            : view.Kind == ChartKind.XY ? "Aún no hay valores de los dos ejes"
            : "Aún no hay valores de estos tags";
        Configure(live, view);
        return new OpenedView(live, null);
    }

    /// <summary>
    /// Lee del histórico del agente los tags de la vista (y el disparo) en su periodo y monta la gráfica con esos datos.
    /// Los tags que no se historizan se avisan como no disponibles.
    /// </summary>
    private async Task<OpenedView> BuildHistoryAsync(TrendView view)
    {
        if (_main.Client is not { } client)
            return new OpenedView(null, null, "Conéctate al agente para leer su histórico.");
        var (from, to) = TrendHistory.Window(view, DateTimeOffset.UtcNow);
        var trigger = view.Trigger;
        var refs = view.Tags.Concat(trigger is null ? [] : [trigger.Ref])
            .Select(r => (Ref: r, Tag: _main.FindTag(r.Device, r.Tag)))
            .Where(r => r.Tag?.Historized == true)
            .Select(r => (r.Ref, r.Tag!.IsBoolean))
            .ToList();
        Dictionary<TrendTagRef, TrendSource> sources;
        TimeSpan? aggregated = null;
        try
        {
            (sources, aggregated) = refs.Count == 0 ? ([], null) : await TrendHistory.LoadAsync(client, refs, from, to);
        }
        catch (Grpc.Core.RpcException ex)
        {
            _main.Report(_main.Describe(ex), isError: true);
            return new OpenedView(null, null, _main.Describe(ex));
        }
        var points = sources.Values.Sum(s => s.Points.Count);
        foreach (var (key, source) in sources.ToArray())
        {
            if (_main.FindTag(key.Device, key.Tag) is { } tag)
                sources[key] = source with { Display = tag.SeriesFormat };
        }
        var live = new LiveTrendViewModel(
            () => view.Tags.Select(t => (t.ToString(), sources.GetValueOrDefault(t))).ToArray(),
            trigger,
            trigger is null ? null : () => sources.GetValueOrDefault(trigger.Ref),
            view.Kind)
        {
            FixedWindow = (from, to),
            HistoryText = $"Histórico {TrendHistory.Describe(view)} · {points} puntos" +
                (aggregated is { } every ? $" · con muchos puntos: mín/máx cada {StateTimeline.FormatDuration(every)}" : ""),
        };
        live.Chart.EmptyText = view.Tags.Count == 0 ? "Esta vista no tiene tags: edítala y añade alguno"
            : refs.Count == 0 ? "Ningún tag de la vista se historiza: actívalo en la configuración del tag"
            : "No hay histórico de estos tags en ese periodo";
        Configure(live, view);
        return new OpenedView(live, null);
    }

    /// <summary>Lee el CSV de la vista (otra vez, por si ha cambiado) y monta su gráfica con los tags elegidos.</summary>
    private OpenedView BuildCsv(TrendView view)
    {
        HistoryCsvContent content;
        try
        {
            content = HistoryCsv.Load(view.CsvPath!);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            return new OpenedView(null, null, $"No se puede leer el CSV: {ex.Message}");
        }
        var sources = new Dictionary<TrendTagRef, TrendSource>();
        for (var i = 0; i < content.Keys.Count; i++)
        {
            var series = content.Series[i];
            sources[new TrendTagRef(content.Keys[i].Device, content.Keys[i].Tag)] =
                new TrendSource(series.Points as List<ChartPoint> ?? series.Points.ToList(), series.IsBoolean, series.Step) { Display = series.Display };
        }
        var file = Path.GetFileName(view.CsvPath!);
        if (view.Kind == ChartKind.Bars)
        {
            var bars = new PeriodBarsViewModel(view, sources, file);
            ConfigureBars(bars, view);
            _ = bars.RefreshCommand.ExecuteAsync(null);
            return new OpenedView(null, bars);
        }
        var now = DateTimeOffset.UtcNow;
        var trigger = view.Trigger;
        var live = new LiveTrendViewModel(
            () => view.Tags.Select(t => (t.ToString(), sources.GetValueOrDefault(t))).ToArray(),
            trigger,
            trigger is null ? null : () => sources.GetValueOrDefault(trigger.Ref),
            view.Kind)
        {
            FixedWindow = (content.From ?? now, content.To ?? now),
            HistoryText = $"CSV {file} · {content.Rows} filas",
            MissingText = "No están en el CSV",
        };
        live.Chart.EmptyText = view.Tags.Count == 0 ? "Esta vista no tiene tags: edítala y añade los del CSV" : "El CSV no tiene valores de estos tags";
        Configure(live, view);
        return new OpenedView(live, null);
    }

    private void ConfigureBars(PeriodBarsViewModel bars, TrendView view)
    {
        bars.Chart.Title = view.Name;
        bars.Chart.Source = SourceText(view);
        bars.Chart.AutoY = view.AutoY;
        bars.Chart.YMinInput = view.YMin is { } min ? (decimal)min : null;
        bars.Chart.YMaxInput = view.YMax is { } max ? (decimal)max : null;
    }

    private void Configure(LiveTrendViewModel live, TrendView view)
    {
        live.Chart.Title = view.Name;
        live.Chart.Source = SourceText(view);
        live.Chart.AutoY = view.AutoY;
        live.Chart.SeparateAxes = view.SeparateAxes;
        live.Chart.LowLimit = view.LowLimit;
        live.Chart.HighLimit = view.HighLimit;
        live.Chart.GaugeStyle = view.GaugeStyle;
        live.Chart.YMinInput = view.YMin is { } min ? (decimal)min : null;
        live.Chart.YMaxInput = view.YMax is { } max ? (decimal)max : null;
        // Ciclo de referencia guardado con la vista; lo que se cambie en la gráfica se vuelve a guardar.
        foreach (var (name, points) in view.ReferenceCycles ?? [])
            live.Reference[name] = points.Where(p => p.Length == 2).Select(p => new XYPoint(p[0], p[1])).ToList();
        live.Tolerance = (decimal)view.ReferenceTolerance;
        live.TolerancePercent = view.ReferenceTolerancePercent;
        live.RefreshTags();
        live.ReferenceChanged += (_, _) =>
        {
            view.ReferenceCycles = live.Reference.Count == 0 ? null
                : live.Reference.ToDictionary(r => r.Key, r => r.Value.Where(p => p.Y is not null).Select(p => new[] { p.X, p.Y!.Value }).ToList());
            view.ReferenceTolerance = (double)live.Tolerance;
            view.ReferenceTolerancePercent = live.TolerancePercent;
            Persist();
        };
    }

    /// <summary>De dónde salen los datos de la vista, para la cabecera de la imagen exportada.</summary>
    private string SourceText(TrendView view) =>
        view.DataSource == TrendDataSource.Csv && CanReadHistory(view.Kind) ? $"CSV {Path.GetFileName(view.CsvPath)}"
        : view.DataSource == TrendDataSource.History || view.Kind == ChartKind.Bars ? $"Histórico · {_main.AgentTitle}"
        : _main.AgentTitle;

    /// <summary>Vuelve de la gráfica a la configuración de la vista.</summary>
    [RelayCommand]
    private void Edit()
    {
        Live = null;
        Bars = null;
        RefreshAvailable();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { } item)
            return;
        if (!await _main.Dialogs.ConfirmAsync("Borrar tendencia", $"¿Borrar la vista «{item.Name}»?"))
            return;
        _all.Remove(item.View);
        Views.Remove(item);
        ForgetInPanels(item.View);
        OnPropertyChanged(nameof(HasViews));
        Selected = Views.FirstOrDefault();
        Persist();
    }

    /// <summary>Avanza la gráfica abierta (temporizador de la vista).</summary>
    public void Refresh(DateTimeOffset now)
    {
        Live?.Refresh(now);
        OpenPanel?.Refresh(now);
    }

    private void Apply(TrendViewItem item)
    {
        var view = item.View;
        view.Name = Name.Trim();
        view.Kind = Kind.Value;
        view.SeparateAxes = SeparateAxes;
        view.DataSource = DataSource.Value;
        view.CsvPath = CsvPath;
        view.HistoryMinutes = HistoryRange.Value;
        view.HistoryFrom = IsCustomRange && TrendHistory.TryParse(HistoryFromText, out var historyFrom) ? historyFrom : null;
        view.HistoryTo = IsCustomRange && TrendHistory.TryParse(HistoryToText, out var historyTo) ? historyTo : null;
        view.LowLimit = LowLimit is { } low ? (double)low : null;
        view.HighLimit = HighLimit is { } high ? (double)high : null;
        view.GaugeStyle = GaugeStyle.Value;
        view.BarPeriod = BarPeriod.Value;
        view.BarMetric = BarMetric.Value;
        view.BarDays = BarRange.Value;
        view.ShiftStartHour = (int)ShiftStartHour;
        view.WindowMinutes = (int)Window.Value.TotalMinutes;
        view.AutoY = AutoY;
        view.YMin = YMin is { } min ? (double)min : null;
        view.YMax = YMax is { } max ? (double)max : null;
        view.Tags = [.. Tags];
        view.Trigger = BuildTrigger();
        item.Refresh();
        Persist();
    }

    private void Persist() => TrendViewStore.Save(_path, _all);

    private void OnTagsChanged(object? sender, EventArgs e)
    {
        Live?.RefreshTags();
        OpenPanel?.RefreshTags();
        if (Live is null)
            RefreshAvailable();
    }

    public void Dispose()
    {
        _main.TagsChanged -= OnTagsChanged;
        OpenPanel?.Dispose();
    }
}
