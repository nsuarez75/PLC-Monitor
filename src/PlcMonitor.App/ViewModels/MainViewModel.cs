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

/// <summary>Pestañas de la ventana principal.</summary>
public enum AppPage
{
    Tags,
    Trends,
    Transforms,
    OpcUa,
    Agents,
}

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
        TagColumns = ValidTagColumns(settings.TagColumns) ?? DefaultTagColumns;
        Browse = new BrowseViewModel(this, () => SelectedDevice);
        AddTag = new AddTagViewModel(this, () => SelectedDevice, DriverOf);
        History = new HistoryViewModel(this, () => SelectedDevice);
        History.OpenInTrendsRequested += (_, view) =>
        {
            OpenTrends();
            _ = Trends!.AddAndOpenAsync(view);
        };
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

    /// <summary>
    /// Pestaña de la ventana: Tags (dispositivos), Tendencias, Transformaciones o Agentes. Cambiar de pestaña no cierra
    /// nada: cada una sigue como se dejó (la tendencia abierta, lo editado sin guardar…).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTagsPage), nameof(IsTrendsPage), nameof(IsTransformsPage), nameof(IsOpcUaPage), nameof(IsAgentsPage))]
    public partial AppPage Page { get; set; }

    // La pestaña OPC UA solo se lee sola mientras está a la vista.
    partial void OnPageChanged(AppPage value)
    {
        if (value != AppPage.OpcUa)
            OpcUaPanel?.StopAutoRefresh();
    }

    public bool IsTagsPage => Page == AppPage.Tags;

    public bool IsTrendsPage => Page == AppPage.Trends;

    public bool IsTransformsPage => Page == AppPage.Transforms;

    public bool IsOpcUaPage => Page == AppPage.OpcUa;

    public bool IsAgentsPage => Page == AppPage.Agents;

    [RelayCommand]
    private void ShowTags() => Page = AppPage.Tags;

    /// <summary>Va a la pestaña Agentes (la crea la primera vez).</summary>
    [RelayCommand]
    private void OpenAgents()
    {
        Agents ??= new AgentsViewModel(this, _sshFactory, _localFactory);
        Page = AppPage.Agents;
    }

    /// <summary>Vuelve a la pestaña Tags (la de agentes sigue como estaba).</summary>
    [RelayCommand]
    private void CloseAgents() => Page = AppPage.Tags;

    /// <summary>Panel de transformaciones del agente conectado; null si aún no se ha abierto o sin conexión.</summary>
    [ObservableProperty]
    public partial TransformsViewModel? TransformsPanel { get; set; }

    /// <summary>Va a la pestaña Transformaciones (la crea la primera vez; lo editado sin guardar se conserva al cambiar de pestaña).</summary>
    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void OpenTransforms()
    {
        TransformsPanel ??= new TransformsViewModel(this);
        Page = AppPage.Transforms;
    }

    /// <summary>Vuelve a la pestaña Tags; lo editado en Transformaciones sigue ahí.</summary>
    [RelayCommand]
    private void CloseTransforms() => Page = AppPage.Tags;

    /// <summary>Servidor OPC UA del agente conectado; null si aún no se ha abierto o sin conexión.</summary>
    [ObservableProperty]
    public partial OpcUaViewModel? OpcUaPanel { get; set; }

    /// <summary>Va a la pestaña OPC UA (la crea la primera vez) y lee el estado del servidor del agente.</summary>
    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task OpenOpcUaAsync()
    {
        var panel = OpcUaPanel ??= new OpcUaViewModel(this);
        Page = AppPage.OpcUa;
        if (!panel.IsDirty)
            await panel.RefreshAsync();
        if (Page == AppPage.OpcUa)
            panel.StartAutoRefresh();
    }

    public BrowseViewModel Browse { get; }

    /// <summary>Alta de tags por dirección (la única forma en drivers sin explorador, como el absoluto).</summary>
    public AddTagViewModel AddTag { get; }

    private Driver? DriverOf(string? id) => Drivers.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// El driver del dispositivo seleccionado sabe explorar (simbólico). El absoluto (PUT/GET) no: se oculta la
    /// pestaña «Explorar» y los tags se añaden por dirección.
    /// </summary>
    public bool SelectedDeviceCanBrowse => DriverOf(SelectedDevice?.Driver)?.CanBrowse == true;

    /// <summary>Qué hacer cuando el dispositivo aún no tiene tags.</summary>
    public string EmptyTagsHint => SelectedDeviceCanBrowse
        ? "Añádelos con «Añadir tag» o búscalos en «Explorar» con el PLC conectado."
        : string.Equals(SelectedDevice?.Driver, AddTagViewModel.ModbusDriver, StringComparison.OrdinalIgnoreCase)
            ? "Añádelos con «Añadir tag» eligiendo la tabla y el número (p.ej. HR0), uno a uno o un rango de golpe."
            : "Añádelos con «Añadir tag» indicando su dirección (p.ej. DB1.DBW10).";

    /// <summary>Consulta del histórico del dispositivo seleccionado.</summary>
    public HistoryViewModel History { get; }

    /// <summary>Panel de vistas de tendencia abierto; null si no.</summary>
    [ObservableProperty]
    public partial TrendsViewModel? Trends { get; set; }

    /// <summary>Agente al que pertenecen las vistas de tendencia: el id de su perfil o, sin perfil, su URL.</summary>
    public string AgentKey => SelectedProfile?.Id ?? AgentUrl.Trim();

    public string AgentTitle => $"Agente {(SelectedProfile is { Name.Length: > 0 } profile ? profile.Name : AgentUrl.Trim())}";

    /// <summary>Han cambiado los tags del agente (foto nueva, dispositivo reconfigurado o quitado).</summary>
    public event EventHandler? TagsChanged;

    /// <summary>Tag del agente conectado; null si no existe.</summary>
    public TagViewModel? FindTag(string device, string tag) => _tagIndex.GetValueOrDefault((device, tag));

    // Agente para el que se creó Trends (las vistas guardadas son de cada agente).
    private string? _trendsAgent;

    /// <summary>Va a la pestaña Tendencias, con la vista que se dejó abierta (se crea la primera vez o al cambiar de agente).</summary>
    [RelayCommand]
    private void OpenTrends()
    {
        EnsureTrends();
        Page = AppPage.Trends;
    }

    private void EnsureTrends()
    {
        if (Trends is not null && _trendsAgent == AgentKey)
            return;
        Trends?.Dispose();
        _trendsAgent = AgentKey;
        Trends = new TrendsViewModel(this, TrendViewStore.PathNextTo(_settingsPath));
    }

    /// <summary>Vuelve a la pestaña Tags; las tendencias siguen como estaban.</summary>
    [RelayCommand]
    private void CloseTrends() => Page = AppPage.Tags;

    /// <summary>El agente conectado tiene el historiador activado.</summary>
    [ObservableProperty]
    public partial bool HistorianEnabled { get; set; }

    public IReadOnlyList<Driver> Drivers { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial string AgentUrl { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionText), nameof(IsReconnecting), nameof(IsOnline))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(NewDeviceCommand), nameof(EditDeviceCommand), nameof(DeleteDeviceCommand),
        nameof(OpenTransformsCommand), nameof(OpenOpcUaCommand))]
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

    /// <summary>Anchos de serie de las columnas de la tabla de tags (formato de <see cref="Controls.TableRow.Columns"/>).</summary>
    public const string DefaultTagColumns = "2*,2.2*,1.6*,90,130,100,250";

    /// <summary>Anchos de las columnas de la tabla de tags; se cambian arrastrando los bordes de la cabecera.</summary>
    [ObservableProperty]
    public partial string TagColumns { get; set; }

    public bool IsReconnecting => IsConnected && !IsLive;

    /// <summary>Conectado y recibiendo en vivo.</summary>
    public bool IsOnline => IsConnected && IsLive;

    [ObservableProperty]
    public partial string AgentDescription { get; set; } = "";

    /// <summary>Conectado con el token de solo lectura: no se ofrece escribir en los tags.</summary>
    [ObservableProperty]
    public partial bool ReadOnlyAccess { get; set; }

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
        AddTag.Reset();
        OnPropertyChanged(nameof(SelectedDeviceCanBrowse));
        OnPropertyChanged(nameof(EmptyTagsHint));
        // Sin explorador no hay pestaña «Explorar»: si estaba abierta se vuelve a Tags.
        if (DeviceTab == ExploreTab && !SelectedDeviceCanBrowse)
            DeviceTab = 0;
    }

    public void Report(string message, bool isError = false) => _ui.Post(() =>
    {
        Message = message;
        IsMessageError = isError;
    });

    public string Describe(Exception exception) => AgentErrors.Describe(exception, AgentUrl);

    /// <summary>Transformaciones de valores del agente conectado.</summary>
    public IReadOnlyList<ValueTransform> Transforms { get; private set; } = [];

    /// <summary>Han cambiado las transformaciones del agente.</summary>
    public event EventHandler? TransformsChanged;

    public ValueTransform? FindTransform(string name) =>
        Transforms.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Tags del agente conectado (todos los dispositivos).</summary>
    public IEnumerable<TagViewModel> AllTags => _tagIndex.Values;

    private void SetTransforms(IEnumerable<ValueTransform> transforms)
    {
        Transforms = [.. transforms];
        foreach (var tag in _tagIndex.Values)
            tag.RefreshDisplay();
        TransformsChanged?.Invoke(this, EventArgs.Empty);
    }

    private string FolderKey(string device, string folder) => $"{AgentKey}/{device}/{folder}";

    public bool IsFolderCollapsed(string device, string folder) => _settings.CollapsedFolders.Contains(FolderKey(device, folder));

    public void SetFolderCollapsed(string device, string folder, bool collapsed)
    {
        var key = FolderKey(device, folder);
        _settings.CollapsedFolders.Remove(key);
        if (collapsed)
            _settings.CollapsedFolders.Add(key);
        _settings.Save(_settingsPath);
    }

    /// <summary>Alterna sistema → claro → oscuro y lo recuerda.</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        Theme = Theme switch { "system" => "light", "light" => "dark", _ => "system" };
        _settings.Theme = Theme;
        _settings.Save(_settingsPath);
    }

    /// <summary>Guarda los anchos de columna al terminar de arrastrar un borde.</summary>
    [RelayCommand]
    private void SaveTagColumns()
    {
        _settings.TagColumns = TagColumns == DefaultTagColumns ? null : TagColumns;
        _settings.Save(_settingsPath);
    }

    [RelayCommand]
    private void ResetTagColumns()
    {
        TagColumns = DefaultTagColumns;
        SaveTagColumns();
    }

    // Unos anchos guardados con otro número de columnas (versión anterior o fichero editado) no se aplican.
    private static string? ValidTagColumns(string? columns) =>
        columns is not null && columns.Split(',').Length == DefaultTagColumns.Split(',').Length
            && columns.Split(',').All(c => double.TryParse(c.Trim().TrimEnd('*'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0)
            ? columns
            : null;

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
            ApplyInfo(info);
            _infoIsFresh = true;
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
            // Las tendencias abiertas eran de otro agente: se cargan las de este.
            if (Trends is not null)
                EnsureTrends();
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
        TransformsPanel?.Dispose();
        TransformsPanel = null;
        OpcUaPanel?.Dispose();
        OpcUaPanel = null;
        if (Page is AppPage.Transforms or AppPage.OpcUa)
            Page = AppPage.Tags;
        Devices.Clear();
        _tagIndex.Clear();
        SetTransforms([]);
        SelectedDevice = null;
        AgentDescription = "";
        ReadOnlyAccess = false;
        TagsChanged?.Invoke(this, EventArgs.Empty);
        Report("Desconectado");
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void NewDevice()
    {
        Editor = new DeviceEditorViewModel(this, Drivers, OnEditorSaved);
        Page = AppPage.Tags;
    }

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
            Page = AppPage.Tags;
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
            await Dialogs.ShowTagDetailsAsync(new TagDetailsViewModel(this, tag, config, HistorianEnabled, name => FindTag(tag.Device, name)?.IsBoolean == true,
                [.. Transforms.Select(t => t.Name)]));
        }
        catch (RpcException ex)
        {
            Report(Describe(ex), isError: true);
        }
    }

    /// <summary>Pestaña elegida del dispositivo: 0 = Tags, 1 = Histórico, 2 = Explorar.</summary>
    [ObservableProperty]
    public partial int DeviceTab { get; set; }

    public const int HistoryTab = 1;

    /// <summary>Índice de la pestaña «Explorar» del dispositivo.</summary>
    private const int ExploreTab = 2;

    /// <summary>Abre la pestaña Histórico con solo este tag marcado y lo consulta (menú del tag).</summary>
    [RelayCommand]
    private async Task OpenTagHistoryAsync(TagViewModel? tag)
    {
        if (tag is null)
            return;
        if (!tag.Historized)
        {
            Report($"{tag.Name} no se guarda en el histórico: actívalo en su detalle (doble clic → Histórico).");
            return;
        }
        if (SelectedDevice?.Name != tag.Device)
            SelectedDevice = Find(tag.Device);
        History.RefreshTags();
        if (!History.SelectOnly(tag.Name))
            return;
        DeviceTab = HistoryTab;
        Page = AppPage.Tags;
        await History.QueryCommand.ExecuteAsync(null);
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
            case SubscribeEvent.EventOneofCase.Transforms:
                SetTransforms(ev.Transforms.Transforms);
                break;
            default:
                break;
        }
    }

    private void ApplyInfo(AgentInfo info)
    {
        Drivers = [.. info.Drivers];
        OnPropertyChanged(nameof(SelectedDeviceCanBrowse));
        OnPropertyChanged(nameof(EmptyTagsHint));
        HistorianEnabled = info.Historian?.Enabled ?? false;
        // Un agente anterior a los roles no dice nada (UNSPECIFIED): su único token es de acceso completo.
        ReadOnlyAccess = info.Access == AccessLevel.Read;
        AgentDescription = $"{info.Machine} · {info.Os} · v{info.Version.Split('+')[0]}{(ReadOnlyAccess ? " · solo lectura" : "")}";
    }

    // La información del agente se acaba de leer al conectar: la primera foto no hace falta que la vuelva a pedir.
    private bool _infoIsFresh;

    /// <summary>
    /// Vuelve a leer versión, drivers e historiador del agente: tras volver a suscribirse puede ser otro (p.ej. se ha
    /// actualizado y reiniciado), y si no la barra superior seguiría con la versión anterior hasta reconectar a mano.
    /// </summary>
    private async Task RefreshInfoAsync()
    {
        if (Client is not { } client)
            return;
        try
        {
            var info = await client.GetInfoAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(10));
            _ui.Post(() =>
            {
                if (Client == client)
                    ApplyInfo(info);
            });
        }
        catch (RpcException)
        {
            // Sin información nueva se sigue con la anterior; la suscripción ya avisa si se pierde la conexión.
        }
    }

    private void ApplySnapshot(Snapshot snapshot)
    {
        if (_infoIsFresh)
            _infoIsFresh = false;
        else
            _ = RefreshInfoAsync();
        var selected = SelectedDevice?.Name;
        Devices.Clear();
        _tagIndex.Clear();
        // Primero las transformaciones: los tags las usan al recibir su valor.
        Transforms = [.. snapshot.Transforms];
        var tagsByDevice = snapshot.Tags.ToLookup(t => t.Device);
        foreach (var d in snapshot.Devices)
        {
            var device = new DeviceViewModel(this, d);
            AddTags(device, tagsByDevice[d.Name]);
            Devices.Add(device);
        }
        TransformsChanged?.Invoke(this, EventArgs.Empty);
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
        device.RebuildRows();
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
