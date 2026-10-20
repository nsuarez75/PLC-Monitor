using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;

namespace PlcMonitor.App.ViewModels;

/// <summary>Un dispositivo del agente con su estado y sus tags.</summary>
public sealed partial class DeviceViewModel : ObservableObject
{
    private readonly IAgentContext _context;

    public DeviceViewModel(IAgentContext context, Device device)
    {
        _context = context;
        Name = device?.Name ?? throw new ArgumentNullException(nameof(device));
        Apply(device);
        Tags.CollectionChanged += (_, e) =>
        {
            foreach (var tag in e.NewItems?.OfType<TagViewModel>() ?? [])
                tag.PropertyChanged += OnTagChanged;
            foreach (var tag in e.OldItems?.OfType<TagViewModel>() ?? [])
                tag.PropertyChanged -= OnTagChanged;
            NotifyCounts();
        };
    }

    /// <summary>Tags con calidad buena.</summary>
    public int GoodCount => Tags.Count(t => t.IsGood);

    /// <summary>Tags con calidad mala o incierta.</summary>
    public int BadCount => Tags.Count(t => !t.IsGood);

    public bool HasBadTags => BadCount > 0;

    public bool HasTags => Tags.Count > 0;

    public string TagCountText => Tags.Count == 1 ? "1 tag" : $"{Tags.Count} tags";

    private void OnTagChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TagViewModel.Quality))
        {
            NotifyCounts();
            foreach (var folder in _folders)
                folder.NotifyCounts();
        }
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(GoodCount));
        OnPropertyChanged(nameof(BadCount));
        OnPropertyChanged(nameof(HasBadTags));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(TagCountText));
    }

    public string Name { get; }

    public ObservableCollection<TagViewModel> Tags { get; } = [];

    private readonly List<TagFolderViewModel> _folders = [];

    /// <summary>
    /// Filas de la tabla de tags: los tags tal cual si ninguno tiene grupo de visualización; si no, cada grupo (por
    /// nombre, y al final los tags sin grupo) con su cabecera plegable y, si está desplegado, sus tags.
    /// </summary>
    public ObservableCollection<object> Rows { get; } = [];

    /// <summary>Grupos de visualización en uso en el dispositivo, por nombre.</summary>
    public IReadOnlyList<string> Folders => [.. _folders.Where(f => f.Name.Length > 0).Select(f => f.Name)];

    /// <summary>Rehace <see cref="Rows"/> a partir de <see cref="Tags"/> (tras cambiar los tags del dispositivo).</summary>
    public void RebuildRows()
    {
        Rows.Clear();
        _folders.Clear();
        if (Tags.All(t => t.Folder.Length == 0))
        {
            foreach (var tag in Tags)
                Rows.Add(tag);
            return;
        }
        var groups = Tags.GroupBy(t => t.Folder, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => g.Key.Length == 0 ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
        foreach (var group in groups)
        {
            var folder = new TagFolderViewModel(this, group.Key, [.. group], !_context.IsFolderCollapsed(Name, group.Key));
            _folders.Add(folder);
            Rows.Add(folder);
            if (folder.IsExpanded)
            {
                foreach (var tag in folder.Tags)
                    Rows.Add(tag);
            }
        }
    }

    /// <summary>Pliega o despliega un grupo (quita o inserta sus filas, sin rehacer el resto de la tabla).</summary>
    internal void ToggleFolder(TagFolderViewModel folder)
    {
        var index = Rows.IndexOf(folder);
        if (index < 0)
            return;
        if (folder.IsExpanded)
        {
            for (var i = 0; i < folder.Tags.Count && index + 1 < Rows.Count && Rows[index + 1] is TagViewModel; i++)
                Rows.RemoveAt(index + 1);
        }
        else
        {
            for (var i = 0; i < folder.Tags.Count; i++)
                Rows.Insert(index + 1 + i, folder.Tags[i]);
        }
        folder.IsExpanded = !folder.IsExpanded;
        _context.SetFolderCollapsed(Name, folder.Name, !folder.IsExpanded);
    }

    /// <summary>
    /// Mueve tags al grupo de visualización <paramref name="folder"/> (vacío = sin grupo). Se reescribe el dispositivo en
    /// el agente; la tabla se rehace al llegar el cambio por la suscripción.
    /// </summary>
    public async Task MoveToFolderAsync(IReadOnlyList<TagViewModel> tags, string folder)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (_context.Client is not { } client || tags.Count == 0)
            return;
        folder = folder.Trim();
        try
        {
            var names = tags.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = Name });
            foreach (var tag in device.ScanGroups.SelectMany(g => g.Tags).Where(t => names.Contains(string.IsNullOrEmpty(t.Name) ? t.Address : t.Name)))
                tag.Folder = folder;
            await client.PutDeviceAsync(new PutDeviceRequest { Device = device, Persist = true });
            var what = tags.Count == 1 ? $"{tags[0].Name}" : $"{tags.Count} tags";
            _context.Report(folder.Length == 0 ? $"{what} sin grupo" : $"{what} en el grupo «{folder}»");
        }
        catch (RpcException ex)
        {
            _context.Report(_context.Describe(ex), isError: true);
        }
    }

    /// <summary>Pide el nombre de un grupo nuevo y mueve a él los tags.</summary>
    public async Task MoveToNewFolderAsync(IReadOnlyList<TagViewModel> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var name = await _context.Dialogs.PromptAsync("Nuevo grupo", tags.Count == 1
            ? $"Nombre del grupo para {tags[0].Name}:"
            : $"Nombre del grupo para los {tags.Count} tags seleccionados:");
        if (!string.IsNullOrWhiteSpace(name))
            await MoveToFolderAsync(tags, name);
    }

    /// <summary>Pliega o despliega todos los grupos.</summary>
    [RelayCommand]
    private void SetAllFolders(string expand)
    {
        var wanted = bool.Parse(expand);
        foreach (var folder in _folders.Where(f => f.IsExpanded != wanted).ToArray())
            ToggleFolder(folder);
    }

    [ObservableProperty]
    public partial string Driver { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial bool WritesEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsFaulted), nameof(IsConnecting), nameof(StateText))]
    public partial ConnectionState State { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; } = "";

    [ObservableProperty]
    public partial string Since { get; set; } = "";

    public bool IsConnected => State == ConnectionState.Connected;

    public bool IsFaulted => State == ConnectionState.Faulted;

    public bool IsConnecting => State == ConnectionState.Connecting;

    public string StateText => State switch
    {
        ConnectionState.Connected => "Conectado",
        ConnectionState.Connecting => "Conectando…",
        ConnectionState.Faulted => "En fallo",
        _ => Enabled ? "Desconectado" : "Desactivado",
    };

    public string ToggleText => Enabled ? "Desactivar" : "Activar";

    public void Apply(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Driver = device.Driver;
        Enabled = device.Enabled;
        WritesEnabled = device.WritesEnabled;
        State = device.State;
        var detail = device.Detail;
        if (device.NextRetry is not null)
            detail = $"{detail} · reintento a las {device.NextRetry.ToDateTime().ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}".Trim(' ', '·');
        Detail = detail;
        Since = device.Since?.ToDateTime().ToLocalTime().ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture) ?? "";
        OnPropertyChanged(nameof(StateText));
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync()
    {
        if (_context.Client is not { } client)
            return;
        try
        {
            // El estado lo trae la suscripción: aplicar aquí la respuesta podría pisar un estado más reciente.
            var device = await client.SetDeviceEnabledAsync(new SetDeviceEnabledRequest { Device = Name, Enabled = !Enabled, Persist = true });
            _context.Report($"{Name} {(device.Enabled ? "activado" : "desactivado")}");
        }
        catch (RpcException ex)
        {
            _context.Report(_context.Describe(ex), isError: true);
        }
    }
}
