using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Protobuf.Collections;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;

namespace PlcMonitor.App.ViewModels;

/// <summary>Formulario de alta o edición de un dispositivo (sin sus tags, que se conservan).</summary>
public sealed partial class DeviceEditorViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly IAgentContext _context;
    private readonly IReadOnlyList<Driver> _drivers;
    private readonly RepeatedField<ScanGroupConfig> _groups = [];
    private readonly Action<string> _saved;

    /// <summary>Alta de un dispositivo nuevo.</summary>
    public DeviceEditorViewModel(IAgentContext context, IReadOnlyList<Driver> drivers, Action<string> saved)
    {
        _context = context;
        _drivers = drivers ?? throw new ArgumentNullException(nameof(drivers));
        _saved = saved;
        DriverIds = [.. drivers.Select(d => d.Id)];
        DriverOptions = [.. drivers.Select(d => new SettingOption(d.Id, string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name))];
        Title = "Nuevo dispositivo";
        Enabled = true;
        DriverId = DriverIds.FirstOrDefault() ?? "";
    }

    /// <summary>Edición de un dispositivo existente.</summary>
    public DeviceEditorViewModel(IAgentContext context, IReadOnlyList<Driver> drivers, DeviceConfig config, Action<string> saved)
        : this(context, drivers, saved)
    {
        ArgumentNullException.ThrowIfNull(config);
        OriginalName = config.Name;
        Title = $"Editar {config.Name}";
        Name = config.Name;
        DriverId = config.Driver;
        SettingsJson = Pretty(config.SettingsJson);
        Enabled = config.Enabled;
        WritesEnabled = config.WritesEnabled;
        OpcUaPublished = config.OpcUaPublished;
        if (config.Reconnect is { } r)
        {
            InitialDelayMs = r.InitialDelayMs;
            MaxDelayMs = r.MaxDelayMs;
            Multiplier = r.Multiplier;
        }
        _groups.AddRange(config.ScanGroups);
        TagSummary = $"{_groups.Sum(g => g.Tags.Count)} tags en {_groups.Count} grupos (se conservan)";
        LoadForm(SettingsJson);
    }

    /// <summary>Nombre actual (null en un alta).</summary>
    public string? OriginalName { get; }

    public bool IsNew => OriginalName is null;

    public string Title { get; }

    public ObservableCollection<string> DriverIds { get; }

    /// <summary>Drivers con su nombre para el desplegable.</summary>
    public IReadOnlyList<SettingOption> DriverOptions { get; }

    public SettingOption? SelectedDriver
    {
        get => DriverOptions.FirstOrDefault(o => o.Value == DriverId);
        set
        {
            if (value is not null)
                DriverId = value.Value;
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDriver))]
    public partial string DriverId { get; set; } = "";

    [ObservableProperty]
    public partial string SettingsJson { get; set; } = "{}";

    /// <summary>Formulario de los ajustes del driver; null si el driver no tiene (solo JSON).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForm), nameof(IsJsonVisible))]
    public partial DriverSettingsForm? Form { get; set; }

    public bool HasForm => Form is not null;

    /// <summary>Editar los ajustes como JSON en vez de con el formulario.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJsonVisible), nameof(IsFormVisible))]
    public partial bool ShowJson { get; set; }

    public bool IsJsonVisible => ShowJson || Form is null;

    public bool IsFormVisible => !ShowJson && Form is not null;

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial bool WritesEnabled { get; set; }

    /// <summary>Primera llave de publicación en el servidor OPC UA del agente (los tags tienen la segunda).</summary>
    [ObservableProperty]
    public partial bool OpcUaPublished { get; set; }

    [ObservableProperty]
    public partial int InitialDelayMs { get; set; } = 1000;

    [ObservableProperty]
    public partial int MaxDelayMs { get; set; } = 30000;

    [ObservableProperty]
    public partial double Multiplier { get; set; } = 2;

    [ObservableProperty]
    public partial string Errors { get; set; } = "";

    [ObservableProperty]
    public partial string TagSummary { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>En un alta, al cambiar de driver se cargan sus ajustes por defecto.</summary>
    partial void OnDriverIdChanged(string value)
    {
        if (IsNew && _drivers.FirstOrDefault(d => d.Id == value) is { } driver)
        {
            SettingsJson = Pretty(driver.DefaultSettingsJson);
            LoadForm(SettingsJson);
        }
    }

    /// <summary>Al pasar del JSON al formulario se relee el JSON (si es válido); al revés, el JSON refleja el formulario.</summary>
    partial void OnShowJsonChanged(bool value)
    {
        if (Form is null)
            return;
        if (value)
        {
            SettingsJson = Form.ToJson();
            return;
        }
        try
        {
            Form.Load(SettingsJson);
            Errors = "";
        }
        catch (JsonException ex)
        {
            Errors = $"El JSON no es válido: {ex.Message}";
            ShowJson = true;
        }
    }

    private void LoadForm(string json)
    {
        var form = DriverSettingsForm.For(DriverId, LoadSerialPortsAsync);
        try
        {
            form?.Load(json);
        }
        catch (JsonException)
        {
            // Ajustes que no son un objeto JSON: se corrigen a mano.
            form = null;
        }
        Form = null;
        ShowJson = false;
        Form = form;
    }

    /// <summary>Puertos serie del equipo del agente; null si no se pudo preguntar.</summary>
    private async Task<IReadOnlyList<string>?> LoadSerialPortsAsync()
    {
        if (_context.Client is not { } client)
            return null;
        try
        {
            var info = await client.GetInfoAsync(new Google.Protobuf.WellKnownTypes.Empty(), deadline: DateTime.UtcNow.AddSeconds(10));
            return [.. info.SerialPorts];
        }
        catch (RpcException)
        {
            return null;
        }
    }

    private bool CanSave() => !IsBusy && !string.IsNullOrWhiteSpace(Name);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_context.Client is not { } client)
            return;
        IsBusy = true;
        Errors = "";
        try
        {
            if (IsFormVisible)
                SettingsJson = Form!.ToJson();
            var config = new DeviceConfig
            {
                Name = Name.Trim(),
                Driver = DriverId,
                Enabled = Enabled,
                WritesEnabled = WritesEnabled,
                OpcUaPublished = OpcUaPublished,
                SettingsJson = SettingsJson,
                Reconnect = new ReconnectConfig { InitialDelayMs = InitialDelayMs, MaxDelayMs = MaxDelayMs, Multiplier = Multiplier },
            };
            config.ScanGroups.AddRange(_groups);
            var status = await client.PutDeviceAsync(new PutDeviceRequest { Device = config, OriginalName = OriginalName ?? "", Persist = true });
            _context.Report($"{status.Name} guardado");
            if (OriginalName is { } original && original != status.Name && _groups.SelectMany(g => g.Tags).Any(t => t.History?.Enabled == true))
            {
                await HistoryMover.OfferAsync(_context, client, new HistoryRename { FromDevice = original, ToDevice = status.Name },
                    $"los tags de «{original}»", status.Name);
            }
            _saved(status.Name);
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

    private static string Pretty(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return JsonSerializer.Serialize(doc.RootElement, Indented);
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
