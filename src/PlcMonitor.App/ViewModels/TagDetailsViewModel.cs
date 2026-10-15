using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;

namespace PlcMonitor.App.ViewModels;

/// <summary>
/// Ventana de detalle de un tag: su valor en vivo y su configuración (nombre, dirección, grupo de escaneo, escritura,
/// tipo). Al guardar se reescribe el dispositivo en el agente, que valida todo antes de aplicarlo.
/// </summary>
public sealed partial class TagDetailsViewModel : ObservableObject
{
    public const string AutomaticType = "Automático";

    private readonly IAgentContext _context;
    private readonly string _originalName;
    private readonly Dictionary<string, int> _groupIntervals;

    public TagDetailsViewModel(IAgentContext context, TagViewModel tag, DeviceConfig device, bool historianEnabled = true)
    {
        HistorianEnabled = historianEnabled;
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(device);
        _context = context;
        Tag = tag;
        Device = device.Name;
        WritesEnabled = device.WritesEnabled;
        _originalName = tag.Name;
        _groupIntervals = device.ScanGroups.ToDictionary(g => g.Name, g => g.IntervalMs, StringComparer.OrdinalIgnoreCase);
        Groups = [.. device.ScanGroups.Select(g => g.Name)];
        DataTypes = [AutomaticType, .. Enum.GetNames<DataType>().Where(n => n != nameof(Agent.Contracts.DataType.Unknown))];

        var (group, config) = Find(device, tag.Name);
        Name = string.IsNullOrEmpty(config?.Name) ? tag.Name : config.Name;
        Address = config?.Address ?? tag.Address;
        Writable = config?.Writable ?? tag.Writable;
        SelectedDataType = config is null || config.DataType == Agent.Contracts.DataType.Unknown ? AutomaticType : config.DataType.ToString();
        GroupName = group?.Name ?? "default";
        IntervalMs = group?.IntervalMs ?? 1000;
        if (config?.History is { } history)
        {
            HistoryEnabled = history.Enabled;
            HistoryMinIntervalMs = history.MinIntervalMs;
            HistoryMaxIntervalMs = history.MaxIntervalMs;
            HistoryDeadband = history.Deadband;
            HistoryDeadbandPercent = history.DeadbandPercent;
        }
    }

    /// <summary>El agente tiene el historiador activado.</summary>
    public bool HistorianEnabled { get; }

    [ObservableProperty]
    public partial bool HistoryEnabled { get; set; }

    /// <summary>Intervalo mínimo entre muestras (0 = cada cambio).</summary>
    [ObservableProperty]
    public partial int HistoryMinIntervalMs { get; set; }

    /// <summary>Tiempo máximo sin guardar (0 = nunca).</summary>
    [ObservableProperty]
    public partial int HistoryMaxIntervalMs { get; set; }

    [ObservableProperty]
    public partial double HistoryDeadband { get; set; }

    [ObservableProperty]
    public partial bool HistoryDeadbandPercent { get; set; }

    /// <summary>Tag en vivo (valor, calidad, hora).</summary>
    public TagViewModel Tag { get; }

    public string Device { get; }

    /// <summary>Primera llave de escritura del dispositivo.</summary>
    public bool WritesEnabled { get; }

    public ObservableCollection<string> Groups { get; }

    public ObservableCollection<string> DataTypes { get; }

    /// <summary>Se pone a true al guardar: la vista cierra la ventana.</summary>
    [ObservableProperty]
    public partial bool Saved { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Address { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupNote))]
    public partial string GroupName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupNote))]
    public partial int IntervalMs { get; set; }

    [ObservableProperty]
    public partial bool Writable { get; set; }

    [ObservableProperty]
    public partial string SelectedDataType { get; set; } = AutomaticType;

    [ObservableProperty]
    public partial string Errors { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Aviso cuando el periodo cambia un grupo que comparten otros tags, o cuando el grupo es nuevo.</summary>
    public string GroupNote
    {
        get
        {
            var name = GroupName.Trim();
            if (!_groupIntervals.TryGetValue(name, out var current))
                return $"Grupo nuevo: se creará con un periodo de {IntervalMs} ms.";
            return current == IntervalMs
                ? $"El grupo «{name}» se lee cada {current} ms."
                : $"Cambia el periodo de todo el grupo «{name}» (de {current} a {IntervalMs} ms).";
        }
    }

    /// <summary>Al elegir un grupo existente se muestra su periodo.</summary>
    partial void OnGroupNameChanged(string value)
    {
        if (_groupIntervals.TryGetValue(value.Trim(), out var interval))
            IntervalMs = interval;
    }

    private bool CanSave() => !IsBusy && !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Address);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_context.Client is not { } client)
            return;
        IsBusy = true;
        Errors = "";
        try
        {
            // Se parte de la configuración actual del agente por si ha cambiado mientras la ventana estaba abierta.
            var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = Device });
            var (oldGroup, tag) = Find(device, _originalName);
            if (tag is null)
            {
                Errors = $"El tag «{_originalName}» ya no existe en {Device}.";
                return;
            }
            oldGroup!.Tags.Remove(tag);

            var groupName = string.IsNullOrWhiteSpace(GroupName) ? "default" : GroupName.Trim();
            var group = device.ScanGroups.FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
            if (group is null)
                device.ScanGroups.Add(group = new ScanGroupConfig { Name = groupName });
            group.IntervalMs = IntervalMs;
            var name = Name.Trim();
            var address = Address.Trim();
            group.Tags.Add(new TagConfig
            {
                // Si el nombre es la dirección se guarda vacío (el agente usa la dirección).
                Name = name == address ? "" : name,
                Address = address,
                Writable = Writable,
                DataType = SelectedDataType == AutomaticType ? Agent.Contracts.DataType.Unknown : Enum.Parse<DataType>(SelectedDataType),
                History = HistoryEnabled || HistoryMinIntervalMs > 0 || HistoryMaxIntervalMs > 0 || HistoryDeadband > 0
                    ? new HistoryConfig
                    {
                        Enabled = HistoryEnabled,
                        MinIntervalMs = HistoryMinIntervalMs,
                        MaxIntervalMs = HistoryMaxIntervalMs,
                        Deadband = HistoryDeadband,
                        DeadbandPercent = HistoryDeadbandPercent,
                    }
                    : null,
            });
            foreach (var empty in device.ScanGroups.Where(g => g.Tags.Count == 0).ToArray())
                device.ScanGroups.Remove(empty);

            await client.PutDeviceAsync(new PutDeviceRequest { Device = device, Persist = true });
            _context.Report($"Tag {Device}/{name} guardado");
            Saved = true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        {
            Errors = ex.Status.Detail;
        }
        catch (RpcException ex)
        {
            Errors = _context.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static (ScanGroupConfig? Group, TagConfig? Tag) Find(DeviceConfig device, string tagName)
    {
        foreach (var group in device.ScanGroups)
        {
            var tag = group.Tags.FirstOrDefault(t => (string.IsNullOrEmpty(t.Name) ? t.Address : t.Name) == tagName);
            if (tag is not null)
                return (group, tag);
        }
        return (null, null);
    }
}
