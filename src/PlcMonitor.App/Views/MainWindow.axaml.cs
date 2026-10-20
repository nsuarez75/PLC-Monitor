using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        var tags = this.FindControl<ListBox>("TagsList")!;
        tags.DoubleTapped += OnTagDoubleTapped;
        tags.ContextRequested += OnTagsContextRequested;
        this.FindControl<ContextMenu>("TagsMenu")!.Opening += OnTagsMenuOpening;
        // La tendencia en vivo avanza con el reloj aunque no lleguen valores nuevos.
        var trend = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        trend.Tick += (_, _) => (DataContext as MainViewModel)?.Trends?.Refresh(DateTimeOffset.UtcNow);
        Opened += (_, _) => trend.Start();
        SizeChanged += (_, e) => Classes.Set("narrow", e.NewSize.Width < NarrowWidth);
        Closed += (_, _) => trend.Stop();
    }

    /// <summary>Clic derecho sobre un tag que no está entre los elegidos: el menú actúa solo sobre él.</summary>
    private void OnTagsContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is ListBox list && e.Source is Visual source
            && source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is TagViewModel tag && !list.SelectedItems!.Contains(tag))
        {
            list.SelectedItems.Clear();
            list.SelectedItems.Add(tag);
        }
    }

    /// <summary>Menú de la tabla de tags: mover los elegidos a un grupo y plegar o desplegar todos los grupos.</summary>
    private void OnTagsMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var menu = (ContextMenu)sender!;
        menu.Items.Clear();
        if (DataContext is not MainViewModel { SelectedDevice: { } device })
        {
            e.Cancel = true;
            return;
        }
        var selected = this.FindControl<ListBox>("TagsList")!.SelectedItems!.OfType<TagViewModel>().ToArray();
        if (selected.Length > 0)
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
            menu.Items.Add(move);
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

    /// <summary>Doble clic en una fila de tags (fuera del campo y los botones de escritura): abre su detalle.</summary>
    private void OnTagDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<TextBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is TagViewModel tag && DataContext is MainViewModel main)
            main.OpenTagDetailsCommand.Execute(tag);
    }
}
