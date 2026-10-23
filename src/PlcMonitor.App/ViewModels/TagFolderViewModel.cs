using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcMonitor.App.Localization;

namespace PlcMonitor.App.ViewModels;

/// <summary>Cabecera plegable de un grupo de visualización en la tabla de tags.</summary>
public sealed partial class TagFolderViewModel : LocalizedObservableObject
{
    private readonly DeviceViewModel _owner;

    public TagFolderViewModel(DeviceViewModel owner, string name, IReadOnlyList<TagViewModel> tags, bool isExpanded)
    {
        _owner = owner;
        Name = name;
        Tags = tags;
        IsExpanded = isExpanded;
    }

    /// <summary>Nombre del grupo; vacío = los tags sin grupo.</summary>
    public string Name { get; }

    public string Title => Name.Length == 0 ? Loc.T("Sin grupo") : Name;

    public IReadOnlyList<TagViewModel> Tags { get; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public string CountText => Tags.Count == 1 ? Loc.T("1 tag") : Loc.F("{0} tags", Tags.Count);

    public int BadCount => Tags.Count(t => !t.IsGood && !t.IsDisabled);

    public bool HasBadTags => BadCount > 0;

    [RelayCommand]
    private void Toggle() => _owner.ToggleFolder(this);

    internal void NotifyCounts()
    {
        OnPropertyChanged(nameof(BadCount));
        OnPropertyChanged(nameof(HasBadTags));
    }
}
