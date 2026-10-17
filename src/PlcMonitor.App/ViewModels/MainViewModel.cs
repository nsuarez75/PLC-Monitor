using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Services;
using PlcMonitor.Deploy;
using PlcMonitor.Agent.Contracts.Client;

namespace PlcMonitor.App.ViewModels;

/// <summary>
/// Ventana principal: conexión con un agente, dispositivos con sus tags en vivo (por suscripción), explorador de
/// símbolos y editor de dispositivos.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAgentContext, IAsyncDisposable
{
    /// <summary>Espera antes de volver a suscribirse si se pierde la conexión con el agente.</summary>
    internal static TimeSpan ResubscribeDelay { get; set; } = TimeSpan.FromSeconds(2);

    private readonly IAgentConnector _connector;
    private readonly IUiDispatcher _ui;
    private readonly AppSettings _settings;
    private readonly string _settingsPath;
    private readonly string _profilesPath;
    private readonly Func<SshTarget, CancellationToken, Task<ISshSession>> _sshFactory;
    private readonly Func<ISshSession> _localFactory;
    private readonly Dictionary<(string Device, string Tag), TagViewModel> _tagIndex = [];
    private IDisposable? _channel;
    private CancellationTokenSource? _subscriptionCts;
    private Task _subscription = Task.CompletedTask;

    public MainViewModel(
        IAgentConnector connector,
        IUiDispatcher ui,
        IDialogs dialogs,
        AppSettings settings,
        string settingsPath,
        string? profilesPath = null,
        Func<SshTarget, CancellationToken, Task<ISshSession>>? sshFactory = null,
        Func<ISshSession>? localFactory = null)
    {
        _connector = connector;
        _ui = ui;
        Dialogs = dialogs;
        _settings = settings;
        _settingsPath = settingsPath;
        _profilesPath = profilesPath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "agents.json");
        _sshFactory = sshFactory ?? (async (target, ct) => await SshNetSession.ConnectAsync(target, ct));
        _localFactory = localFactory ?? (() => new LocalSession());
        Profiles = [.. AgentProfileStore.LoadOrCreate(_profilesPath, settings)];
        AgentUrl = settings.AgentUrl;
        SelectedProfile = Profiles.FirstOrDefault(p => string.Equals(p.Url, settings.AgentUrl, StringComparison.OrdinalIgnoreCase)) ?? Profiles.FirstOrDefault();
        Theme = settings.Theme is "light" or "dark" ? settings.Theme : "system";
        RecentAgents = [.. settings.RecentAgents];
        Browse = new BrowseViewModel(this, () => SelectedDevice);
        History = new HistoryViewModel(this, () => SelectedDevice);
    }

    public AgentService.AgentServiceClient? Client { get; private set; }

    public IDialogs Dialogs { get; }

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    public ObservableCollection<string> RecentAgents { get; }

    /// <summary>Agentes conocidos (perfiles).</summary>
    public ObservableCollection<AgentProfile> Profiles { get; }

    /// <summary>Perfil con el que se conecta.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial AgentProfile? SelectedProfile { get; set; }

    /// <summary>Panel de gestión de agentes abierto; null si no.</summary>
    [ObservableProperty]
    public partial AgentsViewModel? Agents { get; set; }

    partial void OnSelectedProfileChanged(AgentProfile? oldValue, AgentProfile? newValue)
    {
        if (oldValue is not null)
            oldValue.PropertyChanged -= OnSelectedProfilePropertyChanged;
        if (newValue is not null)
        {
            newValue.PropertyChanged += OnSelectedProfilePropertyChanged;
            AgentUrl = newValue.Url;
        }
    }

    // Instalar cambia la URL del perfil ya seleccionado (p.ej. de http://…:5310 a https://…:5311): la caja de URL la sigue,
    // si no Conectar iría a la antigua sin token ni certificado.
    private void OnSelectedProfilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentProfile.Url) && sender is AgentProfile profile && profile == SelectedProfile)
            AgentUrl = profile.Url;
    }

    /// <summary>Carpeta de paquetes del agente recordada entre sesiones.</summary>
    public string AgentPackagesFolder
    {
        get => _settings.AgentPackagesFolder ?? "";
        set
        {
            _settings.AgentPackagesFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            _settings.Save(_settingsPath);
        }
    }

    /// <summary>Guarda los perfiles en disco.</summary>
    public void SaveProfiles() => AgentProfileStore.Save(_profilesPath, Profiles);

    [RelayCommand]
    private void OpenAgents() => Agents = new AgentsViewModel(this, _sshFactory, _localFactory);

    [RelayCommand]
    private void CloseAgents() => Agents = null;

    public BrowseViewModel Browse { get; }

    /// <summary>Consulta del histórico del dispositivo seleccionado.</summary>
    public HistoryViewModel History { get; }

    /// <summary>Panel de vistas de tendencia abierto; null si no.</summary>
    [ObservableProperty]
    public partial TrendsViewModel? Trends { get; set; }

    /// <summary>Agente al que pertenecen las vistas de tendencia: el id de su perfil o, sin perfil, su URL.</summary>
    public string AgentKey => SelectedProfile?.Id ?? AgentUrl.Trim();

    /// <summary>Han cambiado los tags del agente (foto nueva, dispositivo reconfigurado o quitado).</summary>
    public event EventHandler? TagsChanged;

    /// <summary>Tag del agente conectado; null si no existe.</summary>
    public TagViewModel? FindTag(string device, string tag) => _tagIndex.GetValueOrDefault((device, tag));

    [RelayCommand]
    private void OpenTrends()
    {
        Trends?.Dispose();
        Trends = new TrendsViewModel(this, TrendViewStore.PathNextTo(_settingsPath));
    }

    [RelayCommand]
    private void CloseTrends()
    {
        Trends?.Dispose();
        Trends = null;
    }

    /// <summary>El agente conectado tiene el historiador activado.</summary>
    [ObservableProperty]
    public partial bool HistorianEnabled { get; set; }

    public IReadOnlyList<Driver> Drivers { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial string AgentUrl { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionText), nameof(IsReconnecting), nameof(IsOnline))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(NewDeviceCommand), nameof(EditDeviceCommand), nameof(DeleteDeviceCommand))]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial bool IsConnecting { get; set; }

    /// <summary>La suscripción está viva (si es false con IsConnected, se está reintentando).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionText), nameof(IsReconnecting), nameof(IsOnline))]
    public partial bool IsLive { get; set; }

    /// <summary>Tema elegido: "system", "light" o "dark". La vista lo aplica.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeText), nameof(ThemeIcon))]
    public partial string Theme { get; set; }

    public string ThemeText => Theme switch { "light" => "Claro", "dark" => "Oscuro", _ => "Sistema" };

    public string ThemeIcon => Theme switch { "light" => "sun", "dark" => "moon", _ => "monitor" };

    public bool IsReconnecting => IsConnected && !IsLive;

    /// <summary>Conectado y recibiendo en vivo.</summary>
    public bool IsOnline => IsConnected && IsLive;

    [ObservableProperty]
    public partial string AgentDescription { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditDeviceCommand), nameof(DeleteDeviceCommand))]
    public partial DeviceViewModel? SelectedDevice { get; set; }

    /// <summary>Formulario de dispositivo abierto (alta o edición); null si no hay.</summary>
    [ObservableProperty]
    public partial DeviceEditorViewModel? Editor { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = "";

    [ObservableProperty]
    public partial bool IsMessageError { get; set; }

    public string ConnectionText => !IsConnected ? "Sin conexión" : IsLive ? "En vivo" : "Reconectando…";

    partial void OnSelectedDeviceChanged(DeviceViewModel? value)
    {
        Browse.Clear();
        History.Reset();
    }

    public void Report(string message, bool isError = false) => _ui.Post(() =>
    {
        Message = message;
        IsMessageError = isError;
    });

    public string Describe(Exception exception) => AgentErrors.Describe(exception, AgentUrl);

    /// <summary>Alterna sistema → claro → oscuro y lo recuerda.</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        Theme = Theme switch { "system" => "light", "light" => "dark", _ => "system" };
        _settings.Theme = Theme;
        _settings.Save(_settingsPath);
    }

    private bool CanConnect() => !IsConnected && !IsConnecting && !string.IsNullOrWhiteSpace(AgentUrl);

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        var url = AgentUrl.Trim();
        var endpoint = SelectedProfile is { } profile && string.Equals(profile.Url, url, StringComparison.OrdinalIgnoreCase)
            ? profile.Endpoint
            : new AgentEndpoint(url);
        IsConnecting = true;
        Report($"Conectando con {url}…");
        try
        {
            var (client, channel) = _connector.Connect(endpoint);
            AgentInfo info;
            try
            {
                info = await client.GetInfoAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(10));
            }
            catch
            {
                channel.Dispose();
                throw;
            }
            Client = client;
            _channel = channel;
            Drivers = [.. info.Drivers];
            HistorianEnabled = info.Historian?.Enabled ?? false;
            AgentDescription = $"{info.Machine} · {info.Os} · v{info.Version.Split('+')[0]}";
            IsConnected = true;
            _settings.Remember(url);
            _settings.Save(_settingsPath);
            if (!RecentAgents.Contains(url))
                RecentAgents.Insert(0, url);
            if (!Profiles.Any(p => string.Equals(p.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                var added = new AgentProfile { Name = url, Url = url };
                Profiles.Add(added);
                SelectedProfile = added;
                SaveProfiles();
            }
            _subscriptionCts = new CancellationTokenSource();
            _subscription = RunSubscriptionAsync(client, _subscriptionCts.Token);
            Report($"Conectado a {url}");
        }
        catch (Exception ex) when (ex is RpcException or UriFormatException or InvalidOperationException)
        {
            Report(AgentErrors.Describe(ex, url), isError: true);
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task DisconnectAsync()
    {
        await StopSubscriptionAsync();
        _channel?.Dispose();
        _channel = null;
        Client = null;
        IsConnected = false;
        IsLive = false;
        Editor = null;
        Devices.Clear();
        _tagIndex.Clear();
        SelectedDevice = null;
        AgentDescription = "";
        TagsChanged?.Invoke(this, EventArgs.Empty);
        Report("Desconectado");
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void NewDevice() => Editor = new DeviceEditorViewModel(this, Drivers, OnEditorSaved);

    private bool CanEditDevice() => IsConnected && SelectedDevice is not null;

    [RelayCommand(CanExecute = nameof(CanEditDevice))]
    private async Task EditDeviceAsync()
    {
        if (Client is not { } client || SelectedDevice is not { } device)
            return;
        try
        {
            var config = await client.GetDeviceConfigAsync(new DeviceRequest { Device = device.Name });
            Editor = new DeviceEditorViewModel(this, Drivers, config, OnEditorSaved);
        }
        catch (RpcException ex)
        {
            Report(Describe(ex), isError: true);
        }
    }

    [RelayCommand]
    private void CloseEditor() => Editor = null;

    /// <summary>Abre la ventana de detalle y configuración de un tag (doble clic en la lista).</summary>
    [RelayCommand]
    private async Task OpenTagDetailsAsync(TagViewModel? tag)
    {
        if (tag is null || Client is not { } client)
            return;
        try
        {
            var config = await client.GetDeviceConfigAsync(new DeviceRequest { Device = tag.Device });
            await Dialogs.ShowTagDetailsAsync(new TagDetailsViewModel(this, tag, config, HistorianEnabled, name => FindTag(tag.Device, name)?.IsBoolean == true));
        }
        catch (RpcException ex)
        {
            Report(Describe(ex), isError: true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditDevice))]
    private async Task DeleteDeviceAsync()
    {
        if (Client is not { } client || SelectedDevice is not { } device)
            return;
        if (!await Dialogs.ConfirmAsync("Eliminar dispositivo", $"¿Dar de baja {device.Name} y sus {device.Tags.Count} tags?\nSe guarda en la configuración del agente."))
            return;
        try
        {
            await client.DeleteDeviceAsync(new DeleteDeviceRequest { Device = device.Name, Persist = true });
            Report($"{device.Name} eliminado");
        }
        catch (RpcException ex)
        {
            Report(Describe(ex), isError: true);
        }
    }

    private void OnEditorSaved(string device)
    {
        Editor = null;
        // El evento device_configured puede llegar antes o después; si ya está, se selecciona.
        if (Devices.FirstOrDefault(d => d.Name == device) is { } vm)
            SelectedDevice = vm;
        else
            _pendingSelection = device;
    }

    private string? _pendingSelection;

    /// <summary>Recibe los eventos del agente hasta que se cancela; si se corta, vuelve a suscribirse.</summary>
    private async Task RunSubscriptionAsync(AgentService.AgentServiceClient client, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var call = client.Subscribe(new SubscribeRequest(), cancellationToken: token);
                await foreach (var ev in call.ResponseStream.ReadAllAsync(token))
                    _ui.Post(() => Apply(ev));
                _ui.Post(() => IsLive = false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && token.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.ResourceExhausted)
            {
                // Esta app no consumió a tiempo: nueva suscripción y nueva foto.
                Report("Suscripción reiniciada (demasiados cambios a la vez)");
                continue;
            }
            catch (RpcException ex)
            {
                _ui.Post(() => IsLive = false);
                Report($"Conexión con el agente perdida: {Describe(ex)}. Reintentando…", isError: true);
            }
            try
            {
                await Task.Delay(ResubscribeDelay, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Aplica un evento del agente al modelo (en el hilo de la UI).</summary>
    internal void Apply(SubscribeEvent ev)
    {
        switch (ev.EventCase)
        {
            case SubscribeEvent.EventOneofCase.Snapshot:
                ApplySnapshot(ev.Snapshot);
                break;
            case SubscribeEvent.EventOneofCase.TagUpdates:
                foreach (var update in ev.TagUpdates.Updates)
                {
                    if (_tagIndex.TryGetValue((update.Device, update.Tag), out var tag))
                        tag.Apply(update.Value);
                }
                break;
            case SubscribeEvent.EventOneofCase.DeviceStatus:
                if (Find(ev.DeviceStatus.Name) is { } device)
                    device.Apply(ev.DeviceStatus);
                else
                    Devices.Add(new DeviceViewModel(this, ev.DeviceStatus));
                break;
            case SubscribeEvent.EventOneofCase.DeviceConfigured:
                ApplyConfigured(ev.DeviceConfigured);
                break;
            case SubscribeEvent.EventOneofCase.DeviceRemoved:
                Remove(ev.DeviceRemoved.Device);
                break;
            default:
                break;
        }
    }

    private void ApplySnapshot(Snapshot snapshot)
    {
        var selected = SelectedDevice?.Name;
        Devices.Clear();
        _tagIndex.Clear();
        var tagsByDevice = snapshot.Tags.ToLookup(t => t.Device);
        foreach (var d in snapshot.Devices)
        {
            var device = new DeviceViewModel(this, d);
            AddTags(device, tagsByDevice[d.Name]);
            Devices.Add(device);
        }
        SelectedDevice = Find(selected) ?? Devices.FirstOrDefault();
        IsLive = true;
        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyConfigured(DeviceConfigured configured)
    {
        var device = Find(configured.Device.Name);
        Dictionary<string, TagViewModel> previous = new(StringComparer.Ordinal);
        if (device is null)
        {
            device = new DeviceViewModel(this, configured.Device);
            Devices.Add(device);
        }
        else
        {
            device.Apply(configured.Device);
            foreach (var tag in device.Tags)
            {
                _tagIndex.Remove((tag.Device, tag.Name));
                previous[tag.Name] = tag;
            }
            device.Tags.Clear();
        }
        AddTags(device, configured.Tags);
        // La tendencia en vivo sigue con lo recibido antes del cambio (si el tag lee la misma dirección).
        foreach (var tag in device.Tags)
        {
            if (previous.TryGetValue(tag.Name, out var old) && old.Address == tag.Address)
                tag.AdoptTrend(old);
        }
        if (device == SelectedDevice)
            History.RefreshTags();
        TagsChanged?.Invoke(this, EventArgs.Empty);
        if (_pendingSelection == device.Name)
        {
            SelectedDevice = device;
            _pendingSelection = null;
        }
    }

    private void Remove(string name)
    {
        if (Find(name) is not { } device)
            return;
        foreach (var tag in device.Tags)
            _tagIndex.Remove((tag.Device, tag.Name));
        var wasSelected = SelectedDevice == device;
        Devices.Remove(device);
        if (wasSelected)
            SelectedDevice = Devices.FirstOrDefault();
        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddTags(DeviceViewModel device, IEnumerable<Tag> tags)
    {
        foreach (var t in tags)
        {
            var tag = new TagViewModel(this, t);
            device.Tags.Add(tag);
            _tagIndex[(t.Device, t.Name)] = tag;
        }
    }

    private DeviceViewModel? Find(string? name) => name is null ? null : Devices.FirstOrDefault(d => d.Name == name);

    private async Task StopSubscriptionAsync()
    {
        if (_subscriptionCts is null)
            return;
        await _subscriptionCts.CancelAsync();
        try
        {
            await _subscription;
        }
        catch (OperationCanceledException)
        {
        }
        _subscriptionCts.Dispose();
        _subscriptionCts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopSubscriptionAsync();
        _channel?.Dispose();
    }
}
