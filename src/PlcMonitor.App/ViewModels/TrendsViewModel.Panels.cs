using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcMonitor.App.Localization;
using PlcMonitor.App.Services;

namespace PlcMonitor.App.ViewModels;

/// <summary>Paneles: varias vistas guardadas en una rejilla, que se configuran y se abren como las vistas.</summary>
public sealed partial class TrendsViewModel
{
    private readonly string _panelsPath;
    private readonly List<TrendPanel> _allPanels;

    public static IReadOnlyList<Choice<int>> ColumnChoices { get; } =
        [.. Enumerable.Range(1, TrendPanel.MaxColumns).Select(n => new Choice<int>(n == 1 ? "1 columna" : $"{n} columnas", n))];

    public ObservableCollection<TrendPanelItem> Panels { get; } = [];

    public bool HasPanels => Panels.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingPanel), nameof(IsViewingPanel), nameof(HeaderTitle), nameof(HeaderHint))]
    [NotifyCanExecuteChangedFor(nameof(SavePanelCommand), nameof(OpenPanelCommand))]
    public partial TrendPanelItem? SelectedPanel { get; set; }

    /// <summary>Panel abierto (null = configurándolo).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingPanel), nameof(IsViewingPanel))]
    public partial OpenPanelViewModel? OpenPanel { get; private set; }

    public bool IsEditingPanel => SelectedPanel is not null && OpenPanel is null;

    public bool IsViewingPanel => OpenPanel is not null;

    // ---- Formulario del panel elegido ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePanelCommand), nameof(OpenPanelCommand))]
    public partial string PanelName { get; set; } = "";

    [ObservableProperty]
    public partial Choice<int> PanelColumns { get; set; } = ColumnChoices[1];

    [ObservableProperty]
    public partial bool PanelSyncZoom { get; set; } = true;

    /// <summary>Vistas del panel, en orden.</summary>
    public ObservableCollection<TrendViewItem> PanelViews { get; } = [];

    /// <summary>Vistas que se pueden añadir al panel (las que no están).</summary>
    public ObservableCollection<TrendViewItem> PanelAvailable { get; } = [];

    /// <summary>Lo que le falta al panel para abrirse.</summary>
    [ObservableProperty]
    public partial string PanelError { get; private set; } = "";

    partial void OnSelectedPanelChanged(TrendPanelItem? value)
    {
        if (value is not null)
            Selected = null;
        ClosePanel();
        LoadPanelForm(value?.Panel);
    }

    private void LoadPanelForm(TrendPanel? panel)
    {
        PanelName = panel?.Name ?? "";
        PanelColumns = ColumnChoices.FirstOrDefault(c => c.Value == panel?.Columns) ?? ColumnChoices[1];
        PanelSyncZoom = panel?.SyncZoom ?? true;
        PanelError = "";
        PanelViews.Clear();
        foreach (var id in panel?.Views ?? [])
        {
            if (Views.FirstOrDefault(v => v.View.Id == id) is { } item)
                PanelViews.Add(item);
        }
        RefreshPanelAvailable();
    }

    private void RefreshPanelAvailable()
    {
        PanelAvailable.Clear();
        foreach (var item in Views.Where(v => !PanelViews.Contains(v)))
            PanelAvailable.Add(item);
    }

    [RelayCommand]
    private void NewPanel()
    {
        var panel = new TrendPanel { Name = $"Panel {Panels.Count + 1}", Agent = _main.AgentKey };
        _allPanels.Add(panel);
        var item = new TrendPanelItem(panel);
        Panels.Add(item);
        OnPropertyChanged(nameof(HasPanels));
        SelectedPanel = item;
        PersistPanels();
    }

    [RelayCommand]
    private void AddPanelView(TrendViewItem? item)
    {
        if (item is null || PanelViews.Contains(item))
            return;
        PanelViews.Add(item);
        PanelAvailable.Remove(item);
        PanelError = "";
    }

    [RelayCommand]
    private void RemovePanelView(TrendViewItem? item)
    {
        if (item is null || !PanelViews.Remove(item))
            return;
        RefreshPanelAvailable();
    }

    [RelayCommand]
    private void MovePanelViewUp(TrendViewItem? item) => MovePanelView(item, -1);

    [RelayCommand]
    private void MovePanelViewDown(TrendViewItem? item) => MovePanelView(item, +1);

    private void MovePanelView(TrendViewItem? item, int step)
    {
        var index = item is null ? -1 : PanelViews.IndexOf(item);
        if (index < 0 || index + step < 0 || index + step >= PanelViews.Count)
            return;
        PanelViews.Move(index, index + step);
    }

    private bool CanSavePanel() => SelectedPanel is not null && !string.IsNullOrWhiteSpace(PanelName);

    [RelayCommand(CanExecute = nameof(CanSavePanel))]
    private void SavePanel()
    {
        if (SelectedPanel is not { } item)
            return;
        ApplyPanel(item);
        _main.Report(Loc.F("Panel «{0}» guardado", item.Name));
    }

    /// <summary>Guarda el panel y abre las gráficas de todas sus vistas (las del histórico se consultan a la vez).</summary>
    [RelayCommand(CanExecute = nameof(CanSavePanel))]
    private async Task OpenPanelAsync()
    {
        if (SelectedPanel is not { } item)
            return;
        ApplyPanel(item);
        var views = item.Panel.Views.Select(id => _all.FirstOrDefault(v => v.Id == id)).OfType<TrendView>().ToList();
        if (views.Count == 0)
        {
            PanelError = Loc.T("Añade al menos una vista al panel.");
            return;
        }
        PanelError = "";
        IsLoading = true;
        OpenedView[] opened;
        try
        {
            opened = await Task.WhenAll(views.Select(BuildAsync));
        }
        finally
        {
            IsLoading = false;
        }
        if (SelectedPanel != item)
        {
            foreach (var o in opened)
                o.Live?.Dispose();
            return; // se ha elegido otra cosa mientras se leía
        }
        ClosePanel();
        OpenPanel = new OpenPanelViewModel(item.Panel, [.. views.Select((v, i) => new PanelCellViewModel(v.Name, opened[i]))]);
    }

    /// <summary>Vuelve del panel abierto a su configuración.</summary>
    [RelayCommand]
    private void EditPanel() => ClosePanel();

    [RelayCommand]
    private async Task DeletePanelAsync()
    {
        if (SelectedPanel is not { } item)
            return;
        if (!await _main.Dialogs.ConfirmAsync(Loc.T("Borrar panel"), Loc.F("¿Borrar el panel «{0}»? Las vistas no se borran.", item.Name)))
            return;
        _allPanels.Remove(item.Panel);
        Panels.Remove(item);
        OnPropertyChanged(nameof(HasPanels));
        SelectedPanel = null;
        Selected = Views.FirstOrDefault();
        PersistPanels();
    }

    private void ApplyPanel(TrendPanelItem item)
    {
        var panel = item.Panel;
        panel.Name = PanelName.Trim();
        panel.Columns = PanelColumns.Value;
        panel.SyncZoom = PanelSyncZoom;
        panel.Views = [.. PanelViews.Select(v => v.View.Id)];
        item.Refresh();
        OnPropertyChanged(nameof(HeaderTitle));
        PersistPanels();
    }

    private void ClosePanel()
    {
        OpenPanel?.Dispose();
        OpenPanel = null;
    }

    /// <summary>Una vista borrada sale de los paneles que la tenían.</summary>
    private void ForgetInPanels(TrendView view)
    {
        var changed = false;
        foreach (var panel in _allPanels)
            changed |= panel.Views.Remove(view.Id);
        if (!changed)
            return;
        foreach (var item in Panels)
            item.Refresh();
        PersistPanels();
    }

    private void PersistPanels() => TrendPanelStore.Save(_panelsPath, _allPanels);
}
