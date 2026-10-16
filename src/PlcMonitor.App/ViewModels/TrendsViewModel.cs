using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    private static string Describe(TrendView view) => view.Tags.Count switch
    {
        0 => "sin tags",
        1 => $"1 tag · {view.WindowMinutes} min",
        var n => $"{n} tags · {view.WindowMinutes} min",
    };
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
        Window = LiveTrendViewModel.Windows.FirstOrDefault(w => (int)w.Value.TotalMinutes == view?.WindowMinutes) ?? LiveTrendViewModel.Windows[1];
        AutoY = view?.AutoY ?? true;
        YMin = view?.YMin is { } min ? (decimal)min : null;
        YMax = view?.YMax is { } max ? (decimal)max : null;
        Tags.Clear();
        foreach (var tag in view?.Tags ?? [])
            Tags.Add(tag);
        Search = "";
        RefreshAvailable();
    }

    /// <summary>Tags que se pueden añadir: los del agente conectado (dibujables) que no estén ya.</summary>
    private void RefreshAvailable()
    {
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

    [RelayCommand]
    private void RemoveTag(TrendTagRef? tag)
    {
        if (tag is null)
            return;
        Tags.Remove(tag);
        RefreshAvailable();
    }

    private bool CanSave() => Selected is not null && !string.IsNullOrWhiteSpace(Name);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (Selected is not { } item)
            return;
        Apply(item);
        _main.Report($"Tendencia «{item.Name}» guardada");
    }

    /// <summary>Guarda y abre la gráfica en vivo de la vista.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Open()
    {
        if (Selected is not { } item)
            return;
        Apply(item);
        var view = item.View;
        var live = new LiveTrendViewModel(() => view.Tags.Select(t => (t.ToString(), _main.FindTag(t.Device, t.Tag))).ToArray())
        {
            Window = Window,
        };
        live.Chart.EmptyText = view.Tags.Count == 0 ? "Esta vista no tiene tags: edítala y añade alguno" : "Aún no hay valores de estos tags";
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
        view.WindowMinutes = (int)Window.Value.TotalMinutes;
        view.AutoY = AutoY;
        view.YMin = YMin is { } min ? (double)min : null;
        view.YMax = YMax is { } max ? (double)max : null;
        view.Tags = [.. Tags];
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
