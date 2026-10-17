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
        return kind + view.Tags.Count switch
        {
            0 => "sin tags",
            1 => $"1 tag · {view.WindowMinutes} min{(view.Trigger is null ? "" : " · disparo")}",
            var n => $"{n} tags · {view.WindowMinutes} min{(view.Trigger is null ? "" : " · disparo")}",
        };
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
    ];

    public static ChartKindChoice For(ChartKind kind) => All.FirstOrDefault(k => k.Value == kind) ?? All[0];

    public override string ToString() => Text;
}

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
        Tags.CollectionChanged += (_, _) => OnPropertyChanged(nameof(XAxisText));
        foreach (var view in _all.Where(v => v.Agent == main.AgentKey))
            Views.Add(new TrendViewItem(view));
        _main.TagsChanged += OnTagsChanged;
        Selected = Views.FirstOrDefault();
    }

    public ObservableCollection<TrendViewItem> Views { get; } = [];

    [ObservableProperty]
    public partial TrendViewItem? Selected { get; set; }

    /// <summary>Gráfica abierta (null = configurando la vista).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(IsViewing))]
    public partial LiveTrendViewModel? Live { get; private set; }

    public bool IsViewing => Live is not null;

    public bool IsEditing => Live is null && Selected is not null;

    public bool HasViews => Views.Count > 0;

    // ---- Formulario de la vista seleccionada (se aplica con Guardar o al abrir) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsXYKind))]
    public partial ChartKindChoice Kind { get; set; } = ChartKindChoice.All[0];

    /// <summary>X-Y: el primer tag es el eje X.</summary>
    public bool IsXYKind => Kind.Value == ChartKind.XY;

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
        Live = null;
        LoadForm(value?.View);
        OnPropertyChanged(nameof(IsEditing));
    }

    partial void OnSearchChanged(string value) => RefreshAvailable();

    private void LoadForm(TrendView? view)
    {
        Name = view?.Name ?? "";
        Kind = ChartKindChoice.For(view?.Kind ?? ChartKind.Trend);
        KindError = "";
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

    private TrendTrigger? BuildTrigger() => TriggerEnabled && TriggerTag is { } tag
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

    /// <summary>Tags que se pueden añadir: los del agente conectado (dibujables) que no estén ya.</summary>
    private void RefreshAvailable()
    {
        var trigger = TriggerTag?.Ref;
        TriggerChoices.Clear();
        foreach (var device in _main.Devices)
        {
            foreach (var tag in device.Tags.Where(t => t.IsChartable))
                TriggerChoices.Add(new TrendTagChoice(device.Name, tag.Name, tag.DataType));
        }
        if (trigger is not null)
            TriggerTag = TriggerChoices.FirstOrDefault(c => c.Ref == trigger) ?? AddMissingTrigger(trigger);
        Available.Clear();
        var search = Search.Trim();
        foreach (var device in _main.Devices)
        {
            foreach (var tag in device.Tags.Where(t => t.IsChartable))
            {
                var choice = new TrendTagChoice(device.Name, tag.Name, tag.DataType);
                if (Tags.Contains(choice.Ref))
                    continue;
                if (search.Length > 0 && !choice.Text.Contains(search, StringComparison.OrdinalIgnoreCase))
                    continue;
                Available.Add(choice);
            }
        }
        Note = _main.IsConnected ? "" : "Conéctate al agente para ver sus tags y añadirlos.";
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
            _ => "",
        };
        TriggerError = !TriggerEnabled ? ""
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

    /// <summary>Guarda y abre la gráfica en vivo de la vista.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Open()
    {
        if (Selected is not { } item || !CheckTrigger())
            return;
        Apply(item);
        var view = item.View;
        var trigger = view.Trigger;
        var live = new LiveTrendViewModel(
            () => view.Tags.Select(t => (t.ToString(), _main.FindTag(t.Device, t.Tag))).ToArray(),
            trigger,
            trigger is null ? null : () => _main.FindTag(trigger.Device, trigger.Tag),
            view.Kind)
        {
            Window = Window,
        };
        live.Chart.EmptyText = view.Tags.Count == 0 ? "Esta vista no tiene tags: edítala y añade alguno"
            : trigger is not null ? "Esperando el disparo"
            : view.Kind == ChartKind.XY ? "Aún no hay valores de los dos ejes"
            : "Aún no hay valores de estos tags";
        live.Chart.AutoY = view.AutoY;
        live.Chart.YMinInput = YMin;
        live.Chart.YMaxInput = YMax;
        live.RefreshTags();
        Live = live;
    }

    /// <summary>Vuelve de la gráfica a la configuración de la vista.</summary>
    [RelayCommand]
    private void Edit()
    {
        Live = null;
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
        OnPropertyChanged(nameof(HasViews));
        Selected = Views.FirstOrDefault();
        Persist();
    }

    /// <summary>Avanza la gráfica abierta (temporizador de la vista).</summary>
    public void Refresh(DateTimeOffset now) => Live?.Refresh(now);

    private void Apply(TrendViewItem item)
    {
        var view = item.View;
        view.Name = Name.Trim();
        view.Kind = Kind.Value;
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
        if (Live is null)
            RefreshAvailable();
    }

    public void Dispose() => _main.TagsChanged -= OnTagsChanged;
}
