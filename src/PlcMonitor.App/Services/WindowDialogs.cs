using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PlcMonitor.App.Views;

namespace PlcMonitor.App.Services;

/// <summary>Diálogos modales sobre la ventana principal.</summary>
public sealed class WindowDialogs(Window owner) : IDialogs
{
    public Task<bool> ConfirmAsync(string title, string message) => new ConfirmWindow(title, message).ShowDialog<bool>(owner);

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string extension)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType(extension.ToUpperInvariant()) { Patterns = [$"*.{extension}"] }],
        });
        return file?.TryGetLocalPath();
    }

    public Task ShowTagDetailsAsync(ViewModels.TagDetailsViewModel details)
    {
        var window = new TagDetailsWindow { DataContext = details };
        window.FitTo(owner);
        return window.ShowDialog(owner);
    }

    public async Task<string?> OpenFileAsync(string title, string extension)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = [$"*.{extension}"] }],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

}
