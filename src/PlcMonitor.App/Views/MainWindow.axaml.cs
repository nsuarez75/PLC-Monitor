using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using PlcMonitor.App.ViewModels;

namespace PlcMonitor.App.Views;

public sealed partial class MainWindow : Window
{
    /// <summary>Por debajo de este ancho los botones de la barra superior se quedan solo con el icono.</summary>
    private const double NarrowWidth = 1200;

    /// <summary>Lo que hay que mover el ratón con el botón pulsado para empezar a arrastrar tags.</summary>
    private const double DragThreshold = 6;

    private static readonly DataFormat<TagViewModel[]> TagsFormat = DataFormat.CreateInProcessFormat<TagViewModel[]>("plcmonitor-tags");
    private static readonly DataFormat<DeviceViewModel[]> DevicesFormat = DataFormat.CreateInProcessFormat<DeviceViewModel[]>("plcmonitor-devices");

    private readonly ListBox _tags;
    private readonly ListBox _devices;

    // Cabecera de grupo sobre la que se ha hecho clic derecho (el menú actúa sobre el grupo y no sobre los tags).
    private TagFolderViewModel? _menuFolder;

    // Pulsación sobre un tag que puede acabar en arrastre.
    private PointerPressedEventArgs? _press;
    private TagViewModel? _pressedTag;
    private DeviceViewModel? _pressedDevice;
    private ListBoxItem? _dropTarget;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _tags = this.FindControl<ListBox>("TagsList")!;
        _tags.DoubleTapped += OnTagDoubleTapped;
        _tags.ContextRequested += OnTagsContextRequested;
        _tags.KeyDown += OnTagsKeyDown;
        _tags.AddHandler(PointerPressedEvent, OnTagsPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _tags.AddHandler(PointerMovedEvent, OnTagsPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        _tags.AddHandler(PointerReleasedEvent, (_, _) => _press = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        _tags.AddHandler(DragDrop.DragOverEvent, OnTagsDragOver);
        _tags.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropTarget(null));
        _tags.AddHandler(DragDrop.DropEvent, OnTagsDrop);
        _devices = this.FindControl<ListBox>("DevicesList")!;
        _devices.AddHandler(PointerPressedEvent, OnDevicesPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _devices.AddHandler(PointerMovedEvent, OnDevicesPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        _devices.AddHandler(PointerReleasedEvent, (_, _) => _press = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        _devices.AddHandler(DragDrop.DragOverEvent, OnDevicesDragOver);
        _devices.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropTarget(null));
        _devices.AddHandler(DragDrop.DropEvent, OnDevicesDrop);
        this.FindControl<ContextMenu>("TagsMenu")!.Opening += OnTagsMenuOpening;
        this.FindControl<ContextMenu>("ColumnsMenu")!.Opening += OnColumnsMenuOpening;
        // La tendencia en vivo avanza con el reloj aunque no lleguen valores nuevos (solo si se está viendo: al volver a
        // la pestaña se pone al día en el siguiente paso, los valores se siguen recibiendo igual).
        var trend = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        trend.Tick += (_, _) =>
        {
            if (DataContext is MainViewModel { IsTrendsPage: true, Trends: { } trends })
                trends.Refresh(DateTimeOffset.UtcNow);
        };
        Opened += (_, _) => trend.Start();
        SizeChanged += (_, e) => Classes.Set("narrow", e.NewSize.Width < NarrowWidth);
        Closed += (_, _) => trend.Stop();
    }

    private static object? RowAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext;

    private TagViewModel[] SelectedTags => _tags.SelectedItems!.OfType<TagViewModel>().ToArray();

    /// <summary>Clic derecho sobre un tag que no está entre los elegidos: el menú actúa solo sobre él. Sobre una cabecera, sobre el grupo.</summary>
    private void OnTagsContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var row = RowAt(e.Source);
        _menuFolder = row as TagFolderViewModel;
        if (row is TagViewModel tag && !_tags.SelectedItems!.Contains(tag))
        {
            _tags.SelectedItems.Clear();
            _tags.SelectedItems.Add(tag);
        }
    }

    /// <summary>
    /// Menú de la tabla de tags: sobre una cabecera, renombrar o deshacer el grupo; sobre tags, editar, ver su histórico,
    /// moverlos a un grupo o quitarlos; y plegar o desplegar todos los grupos.
    /// </summary>
    private void OnTagsMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var menu = (ContextMenu)sender!;
        menu.Items.Clear();
        if (DataContext is not MainViewModel { SelectedDevice: { } device } main)
        {
            e.Cancel = true;
            return;
        }
        if (_menuFolder is { } folder)
        {
            if (folder.Name.Length > 0)
            {
                menu.Items.Add(new MenuItem { Header = "Renombrar grupo…", Command = new RelayCommand(() => _ = device.RenameFolderAsync(folder)) });
                menu.Items.Add(new MenuItem
                {
                    Header = "Deshacer el grupo (sus tags quedan sin grupo)",
                    Command = new RelayCommand(() => _ = device.MoveToFolderAsync([.. folder.Tags], "")),
                });
            }
        }
        else if (SelectedTags is { Length: > 0 } selected)
        {
            if (selected.Length == 1)
            {
                var tag = selected[0];
                menu.Items.Add(new MenuItem { Header = "Editar…", Command = main.OpenTagDetailsCommand, CommandParameter = tag });
                menu.Items.Add(new MenuItem
                {
                    Header = "Abrir su histórico",
                    Command = main.OpenTagHistoryCommand,
                    CommandParameter = tag,
                    IsEnabled = tag.Historized && main.HistorianEnabled,
                });
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(MoveMenu(device, selected));
            menu.Items.Add(new Separator());
            // Activar los desactivados y desactivar los activos (con una selección mixta salen las dos opciones).
            var disabled = selected.Where(t => t.IsDisabled).ToArray();
            var enabled = selected.Where(t => !t.IsDisabled).ToArray();
            if (disabled.Length > 0)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = disabled.Length == 1 ? "Activar tag" : $"Activar {disabled.Length} tags",
                    Command = new RelayCommand(() => _ = device.SetTagsEnabledAsync(disabled, enabled: true)),
                });
            }
            if (enabled.Length > 0)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = enabled.Length == 1 ? "Desactivar tag" : $"Desactivar {enabled.Length} tags",
                    Command = new RelayCommand(() => _ = device.SetTagsEnabledAsync(enabled, enabled: false)),
                });
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem
            {
                Header = selected.Length == 1 ? "Quitar tag…" : $"Quitar {selected.Length} tags…",
                Command = new RelayCommand(() => _ = device.RemoveTagsAsync(selected)),
                InputGesture = new KeyGesture(Key.Delete),
            });
        }
        if (device.Rows.OfType<TagFolderViewModel>().Any())
        {
            if (menu.Items.Count > 0)
                menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Desplegar todos los grupos", Command = device.SetAllFoldersCommand, CommandParameter = "true" });
            menu.Items.Add(new MenuItem { Header = "Plegar todos los grupos", Command = device.SetAllFoldersCommand, CommandParameter = "false" });
        }
        e.Cancel = menu.Items.Count == 0;
    }

    /// <summary>
    /// Menú de la cabecera de la tabla de tags: una casilla por columna (menos la del nombre), en el orden en que se ven,
    /// y volver a los anchos y al orden de serie.
    /// </summary>
    private void OnColumnsMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var menu = (ContextMenu)sender!;
        menu.Items.Clear();
        if (DataContext is not MainViewModel main)
        {
            e.Cancel = true;
            return;
        }
        foreach (var i in main.TagColumnSequence.Where(c => c > 0))
        {
            menu.Items.Add(new MenuItem
            {
                Header = MainViewModel.TagColumnTitles[i],
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = !main.IsTagColumnHidden(i),
                Command = main.ToggleTagColumnCommand,
                CommandParameter = i,
            });
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Mostrar todas las columnas", Command = main.ShowAllTagColumnsCommand, IsEnabled = main.TagHiddenColumns.Length > 0 });
        menu.Items.Add(new MenuItem { Header = "Restablecer anchos de columna", Command = main.ResetTagColumnsCommand });
        menu.Items.Add(new MenuItem
        {
            Header = "Restablecer orden de columnas",
            Command = main.ResetTagColumnOrderCommand,
            IsEnabled = main.TagColumnOrder != MainViewModel.DefaultTagColumnOrder,
        });
    }

    private static MenuItem MoveMenu(DeviceViewModel device, TagViewModel[] selected)
    {
        var move = new MenuItem { Header = selected.Length == 1 ? "Mover a grupo" : $"Mover {selected.Length} tags a grupo" };
        foreach (var folder in device.Folders)
        {
            var name = folder;
            move.Items.Add(new MenuItem
            {
                Header = name,
                Command = new RelayCommand(() => _ = device.MoveToFolderAsync(selected, name)),
                IsEnabled = !selected.All(t => string.Equals(t.Folder, name, StringComparison.CurrentCultureIgnoreCase)),
            });
        }
        if (device.Folders.Count > 0)
            move.Items.Add(new Separator());
        move.Items.Add(new MenuItem { Header = "Nuevo grupo…", Command = new RelayCommand(() => _ = device.MoveToNewFolderAsync(selected)) });
        move.Items.Add(new MenuItem
        {
            Header = "Sin grupo",
            Command = new RelayCommand(() => _ = device.MoveToFolderAsync(selected, "")),
            IsEnabled = selected.Any(t => t.Folder.Length > 0),
        });
        return move;
    }

    /// <summary>Supr quita los tags elegidos (tras confirmarlo).</summary>
    private void OnTagsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || e.Source is TextBox || DataContext is not MainViewModel { SelectedDevice: { } device } || SelectedTags is not { Length: > 0 } selected)
            return;
        e.Handled = true;
        _ = device.RemoveTagsAsync(selected);
    }

    private void OnTagsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _press = null;
        if (!e.GetCurrentPoint(_tags).Properties.IsLeftButtonPressed || e.Source is not Visual source
            || source.FindAncestorOfType<TextBox>(includeSelf: true) is not null || source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (RowAt(source) is TagViewModel tag)
        {
            _press = e;
            _pressedTag = tag;
        }
    }

    /// <summary>Al mover con el botón pulsado sobre un tag empieza el arrastre: el tag, o todos los elegidos si es uno de ellos.</summary>
    private async void OnTagsPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || _pressedTag is not { } tag)
            return;
        var delta = e.GetPosition(_tags) - press.GetPosition(_tags);
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;
        _press = null;
        var selected = SelectedTags;
        TagViewModel[] dragged = selected.Contains(tag) ? selected : [tag];
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(TagsFormat, dragged));
        try
        {
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        }
        finally
        {
            SetDropTarget(null);
        }
    }

    /// <summary>
    /// Qué pasa al soltar tags donde está el ratón: sobre la cabecera de un grupo, pasan a ese grupo (marca de fila
    /// entera); sobre un tag, se colocan delante o detrás según la mitad de la fila (marca de línea), y si la tabla está
    /// agrupada pasan también a su grupo.
    /// </summary>
    private (ListBoxItem Item, string Mark, Func<Task> Apply)? TagDropOf(DragEventArgs e)
    {
        if (DataContext is not MainViewModel { SelectedDevice: { } device } || e.DataTransfer.TryGetValue(TagsFormat) is not { Length: > 0 } tags
            || tags.Any(t => t.Device != device.Name) || ItemAt(e.Source) is not { } item)
            return null;
        switch (item.DataContext)
        {
            case TagFolderViewModel header when DeviceViewModel.DropFolder(header, tags) is { } folder:
                return (item, "droptarget", () => device.MoveToFolderAsync(tags, folder));
            case TagViewModel target:
                var after = IsLowerHalf(e, item);
                return device.CanMoveTags(tags, target, after) ? (item, after ? "dropafter" : "dropbefore", () => device.MoveTagsAsync(tags, target, after)) : null;
            default:
                return null;
        }
    }

    private static ListBoxItem? ItemAt(object? source) => (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);

    private static bool IsLowerHalf(DragEventArgs e, ListBoxItem item) => e.GetPosition(item).Y > item.Bounds.Height / 2;

    private void OnTagsDragOver(object? sender, DragEventArgs e)
    {
        var drop = TagDropOf(e);
        e.DragEffects = drop is null ? DragDropEffects.None : DragDropEffects.Move;
        SetDropTarget(drop?.Item, drop?.Mark);
    }

    private void OnTagsDrop(object? sender, DragEventArgs e)
    {
        SetDropTarget(null);
        if (TagDropOf(e) is not { } drop)
            return;
        e.DragEffects = DragDropEffects.Move;
        _ = drop.Apply();
    }

    private void OnDevicesPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _press = null;
        if (e.GetCurrentPoint(_devices).Properties.IsLeftButtonPressed && ItemAt(e.Source)?.DataContext is DeviceViewModel device)
        {
            _press = e;
            _pressedDevice = device;
        }
    }

    /// <summary>Al mover con el botón pulsado sobre un dispositivo empieza el arrastre para cambiar el orden.</summary>
    private async void OnDevicesPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || _pressedDevice is not { } device)
            return;
        var delta = e.GetPosition(_devices) - press.GetPosition(_devices);
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;
        _press = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(DevicesFormat, new[] { device }));
        try
        {
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        }
        finally
        {
            SetDropTarget(null);
        }
    }

    private (ListBoxItem Item, DeviceViewModel[] Devices, DeviceViewModel Target, bool After)? DeviceDropOf(DragEventArgs e)
    {
        if (DataContext is not MainViewModel main || e.DataTransfer.TryGetValue(DevicesFormat) is not { Length: > 0 } devices
            || ItemAt(e.Source) is not { DataContext: DeviceViewModel target } item)
            return null;
        var after = IsLowerHalf(e, item);
        return main.CanMoveDevices(devices, target, after) ? (item, devices, target, after) : null;
    }

    private void OnDevicesDragOver(object? sender, DragEventArgs e)
    {
        var drop = DeviceDropOf(e);
        e.DragEffects = drop is null ? DragDropEffects.None : DragDropEffects.Move;
        SetDropTarget(drop?.Item, drop is { After: true } ? "dropafter" : "dropbefore");
    }

    private void OnDevicesDrop(object? sender, DragEventArgs e)
    {
        SetDropTarget(null);
        if (DeviceDropOf(e) is not { } drop || DataContext is not MainViewModel main)
            return;
        e.DragEffects = DragDropEffects.Move;
        _ = main.MoveDevicesAsync(drop.Devices, drop.Target, drop.After);
    }

    private string? _dropMark;

    /// <summary>Marca la fila donde se soltaría: entera (droptarget) o con una línea arriba o abajo (dropbefore/dropafter).</summary>
    private void SetDropTarget(ListBoxItem? item, string? mark = null)
    {
        if (_dropTarget == item && _dropMark == mark)
            return;
        if (_dropTarget is not null && _dropMark is not null)
            _dropTarget.Classes.Remove(_dropMark);
        (_dropTarget, _dropMark) = item is null ? (null, null) : (item, mark);
        if (_dropTarget is not null && _dropMark is not null)
            _dropTarget.Classes.Add(_dropMark);
    }

    /// <summary>Doble clic en una fila de tags (fuera del campo y los botones de escritura): abre su detalle.</summary>
    private void OnTagDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<TextBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (RowAt(source) is TagViewModel tag && DataContext is MainViewModel main)
            main.OpenTagDetailsCommand.Execute(tag);
    }
}
