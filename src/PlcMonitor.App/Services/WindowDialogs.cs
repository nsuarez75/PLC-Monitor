using Avalonia.Controls;
using PlcMonitor.App.Views;

namespace PlcMonitor.App.Services;

/// <summary>Diálogos modales sobre la ventana principal.</summary>
public sealed class WindowDialogs(Window owner) : IDialogs
{
    public Task<bool> ConfirmAsync(string title, string message) => new ConfirmWindow(title, message).ShowDialog<bool>(owner);

    public Task ShowTagDetailsAsync(ViewModels.TagDetailsViewModel details) => new TagDetailsWindow { DataContext = details }.ShowDialog(owner);
}
