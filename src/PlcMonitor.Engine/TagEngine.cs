using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PlcMonitor.Abstractions;
using PlcMonitor.Core;
using PlcMonitor.Engine.Configuration;

namespace PlcMonitor.Engine;

/// <summary>
/// Motor de recolección: un <see cref="DeviceSupervisor"/> por dispositivo, la caché con el último valor de cada tag
/// y la difusión de cambios a los suscriptores (API en vivo, historiador, OPC UA...).
/// </summary>
public sealed class TagEngine : IAsyncDisposable, IEngineSink
{
    private readonly Dictionary<string, DeviceSupervisor> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TagKey, TagSnapshot> _values = [];
    private readonly Dictionary<string, DeviceStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Subscriber> _subscribers = [];
    private readonly Lock _sync = new();
    // Serializa los cambios de configuración (alta, modificación, baja, activar/desactivar).
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly DriverRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private int _disposed;

    public TagEngine(AgentConfiguration configuration, DriverRegistry registry, ILoggerFactory loggerFactory, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var errors = ConfigurationValidator.Validate(configuration, registry);
        if (errors.Count > 0)
            throw new EngineConfigurationException(errors);

        Configuration = configuration;
        _registry = registry;
        _loggerFactory = loggerFactory;
        _time = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory.CreateLogger<TagEngine>();
        lock (_sync)
        {
            foreach (var device in configuration.Devices)
                AddSupervisorLocked(device);
        }
    }

    /// <summary>
    /// Configuración en uso, con los cambios hechos en caliente. Es el objeto vivo: para leerla desde otro hilo
    /// usa <see cref="SnapshotConfiguration"/>.
    /// </summary>
    public AgentConfiguration Configuration { get; }

    /// <summary>Arranca los dispositivos que tienen Enabled.</summary>
    public async Task StartAsync()
    {
        DeviceSupervisor[] enabled;
        lock (_sync)
            enabled = Configuration.Devices.Where(d => d.Enabled).Select(d => _devices[d.Name]).ToArray();
        foreach (var supervisor in enabled)
            await supervisor.StartAsync();
        _logger.LogInformation("Motor arrancado: {Enabled} de {Total} dispositivos activos", enabled.Length, Configuration.Devices.Count);
    }

    public async Task StopAsync()
    {
        DeviceSupervisor[] all;
        lock (_sync)
            all = [.. _devices.Values];
        await Task.WhenAll(all.Select(d => d.StopAsync()));
        lock (_sync)
        {
            foreach (var subscriber in _subscribers)
                subscriber.Channel.Writer.TryComplete();
            _subscribers.Clear();
        }
    }

    public IReadOnlyList<DeviceStatus> GetDevices()
    {
        lock (_sync)
            return Configuration.Devices.Select(d => _statuses[d.Name]).ToArray();
    }

    public DeviceStatus GetDevice(string device)
    {
        lock (_sync)
            return _statuses[Find(device).Name];
    }

    /// <summary>Activa o desactiva un dispositivo en caliente. El cambio queda en <see cref="Configuration"/>.</summary>
    public async Task<DeviceStatus> SetDeviceEnabledAsync(string device, bool enabled)
    {
        await _changes.WaitAsync();
        try
        {
            var supervisor = Find(device);
            lock (_sync)
                ConfigOf(supervisor.Name).Enabled = enabled;
            if (enabled)
                await supervisor.StartAsync();
            else
                await supervisor.StopAsync();
            _logger.LogInformation("{Device}: {Action}", supervisor.Name, enabled ? "activado" : "desactivado");
            return GetDevice(supervisor.Name);
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>Copia independiente de la configuración en uso (para guardarla o enviarla).</summary>
    public AgentConfiguration SnapshotConfiguration()
    {
        lock (_sync)
            return ConfigurationFile.Parse(ConfigurationFile.Serialize(Configuration));
    }

    /// <summary>Copia independiente de la configuración de un dispositivo.</summary>
    public DeviceConfiguration GetDeviceConfiguration(string device)
    {
        var name = Find(device).Name;
        return SnapshotConfiguration().Devices.First(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Da de alta o sustituye un dispositivo (con sus grupos y tags). Se valida la configuración completa resultante;
    /// si hay errores lanza <see cref="EngineConfigurationException"/> y no cambia nada. Si solo cambian los grupos, los
    /// tags o la llave de escritura, se aplica en caliente sin cortar la conexión y los tags que no cambian conservan su
    /// valor; si cambia la conexión (driver, ajustes, reconexión, activación o nombre), se detiene y se vuelve a crear.
    /// </summary>
    /// <param name="device">Nueva configuración del dispositivo (se copia).</param>
    /// <param name="originalName">Nombre actual si se renombra; null para alta o mismo nombre.</param>
    public async Task<DeviceStatus> PutDeviceAsync(DeviceConfiguration device, string? originalName = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        await _changes.WaitAsync();
        try
        {
            var copy = Clone(device);
            var replaces = string.IsNullOrWhiteSpace(originalName) ? copy.Name : originalName;
            var candidate = SnapshotConfiguration();
            var index = candidate.Devices.FindIndex(d => string.Equals(d.Name, replaces, StringComparison.OrdinalIgnoreCase));
            if (index < 0 && !string.IsNullOrWhiteSpace(originalName))
                throw new KeyNotFoundException($"Dispositivo desconocido '{originalName}'.");
            if (index >= 0)
                candidate.Devices[index] = copy;
            else
                candidate.Devices.Add(copy);
            var errors = ConfigurationValidator.Validate(candidate, _registry);
            if (errors.Count > 0)
                throw new EngineConfigurationException(errors);

            if (index >= 0 && CanReconfigureInPlace(Configuration.Devices[index], copy))
                return ReconfigureInPlace(index, copy);

            DeviceSupervisor? old = null;
            if (index >= 0)
            {
                lock (_sync)
                    old = _devices[Configuration.Devices[index].Name];
                await old.DisposeAsync();
            }

            DeviceSupervisor supervisor;
            lock (_sync)
            {
                if (old is not null)
                {
                    RemoveSupervisorLocked(old);
                    if (!string.Equals(old.Name, copy.Name, StringComparison.Ordinal))
                        Broadcast(new DeviceRemoved(old.Name));
                    Configuration.Devices[index] = copy;
                }
                else
                {
                    Configuration.Devices.Add(copy);
                }
                supervisor = AddSupervisorLocked(copy);
                Broadcast(new DeviceConfigured(_statuses[copy.Name], supervisor.Tags.Select(t => _values[t.Key]).ToArray()));
            }
            if (copy.Enabled)
                await supervisor.StartAsync();
            _logger.LogInformation("{Device}: {Action} ({Tags} tags)", copy.Name, old is null ? "dado de alta" : "reconfigurado", supervisor.Tags.Count);
            return GetDevice(copy.Name);
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>Mismo dispositivo y misma conexión: solo cambian grupos, tags o las llaves de escritura y de OPC UA.</summary>
    private static bool CanReconfigureInPlace(DeviceConfiguration current, DeviceConfiguration next)
    {
        if (!string.Equals(current.Name, next.Name, StringComparison.Ordinal))
            return false;
        static string ConnectionPart(DeviceConfiguration device)
        {
            var copy = Clone(device);
            copy.ScanGroups.Clear();
            copy.WritesEnabled = false;
            copy.OpcUaPublished = false;
            return ConfigurationFile.Serialize(new AgentConfiguration { Devices = [copy] });
        }
        return ConnectionPart(current) == ConnectionPart(next);
    }

    private DeviceStatus ReconfigureInPlace(int index, DeviceConfiguration copy)
    {
        DeviceSupervisor supervisor;
        lock (_sync)
        {
            supervisor = _devices[copy.Name];
            Configuration.Devices[index] = copy;
            // Primero los valores: lo que publiquen los grupos nuevos debe encontrar ya sus tags.
            var previous = supervisor.Tags.ToDictionary(t => t.Key);
            var next = DeviceSupervisor.BuildTags(copy);
            // Tags que desaparecen: si otro nuevo lee la misma dirección (un tag renombrado), hereda su valor.
            var removed = new Dictionary<(string Address, TagDataType? Type, TagScaling? Scaling), TagValue>();
            foreach (var key in previous.Keys.Except(next.Select(t => t.Key)))
            {
                removed.TryAdd((previous[key].Address, previous[key].DataType, previous[key].Scaling), _values[key].Value);
                _values.Remove(key);
            }
            var pending = TagValue.Bad(QualityCode.BadNotConnected, _time.GetUtcNow(),
                supervisor.Status.State == ConnectionState.Connected ? "Esperando la primera lectura" : supervisor.Status.Detail ?? DeviceSupervisor.DisabledDetail);
            foreach (var tag in next)
            {
                // Un tag que sigue leyendo la misma dirección con el mismo escalado conserva su valor; si no, espera a la próxima lectura.
                if (previous.TryGetValue(tag.Key, out var old))
                {
                    _values[tag.Key] = old.Address == tag.Address && old.DataType == tag.DataType && old.Scaling == tag.Scaling
                        ? _values[tag.Key] with { Definition = tag }
                        : new TagSnapshot(tag, pending);
                }
                else
                {
                    _values[tag.Key] = removed.Remove((tag.Address, tag.DataType, tag.Scaling), out var inherited)
                        ? new TagSnapshot(tag, inherited)
                        : new TagSnapshot(tag, pending);
                }
            }
            supervisor.Reconfigure(copy);
            Broadcast(new DeviceConfigured(_statuses[copy.Name], next.Select(t => _values[t.Key]).ToArray()));
        }
        _logger.LogInformation("{Device}: reconfigurado en caliente ({Tags} tags)", copy.Name, supervisor.Tags.Count);
        return GetDevice(copy.Name);
    }

    /// <summary>Copia de las transformaciones de valores.</summary>
    public IReadOnlyList<ValueTransform> GetTransforms()
    {
        lock (_sync)
            return CloneTransforms(Configuration.Transforms);
    }

    /// <summary>
    /// Sustituye todas las transformaciones. Si alguna que se quita la usa un tag, o la configuración resultante no es
    /// válida, lanza <see cref="EngineConfigurationException"/> y no cambia nada.
    /// </summary>
    public async Task<IReadOnlyList<ValueTransform>> SetTransformsAsync(IReadOnlyList<ValueTransform> transforms)
    {
        ArgumentNullException.ThrowIfNull(transforms);
        await _changes.WaitAsync();
        try
        {
            var candidate = SnapshotConfiguration();
            candidate.Transforms.Clear();
            candidate.Transforms.AddRange(CloneTransforms(transforms));
            var names = new HashSet<string>(candidate.Transforms.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
            var errors = candidate.Devices
                .SelectMany(d => d.ScanGroups.SelectMany(g => g.Tags).Where(t => !string.IsNullOrWhiteSpace(t.Transform) && !names.Contains(t.Transform))
                    .Select(t => (Transform: t.Transform!, Tag: $"{d.Name}/{t.EffectiveName}")))
                .GroupBy(x => x.Transform, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"La transformación '{g.Key}' la usan {string.Join(", ", g.Select(x => x.Tag))}: quítasela antes a esos tags.")
                .ToList();
            if (errors.Count == 0)
                errors.AddRange(ConfigurationValidator.Validate(candidate, _registry));
            if (errors.Count > 0)
                throw new EngineConfigurationException(errors);
            lock (_sync)
            {
                Configuration.Transforms.Clear();
                Configuration.Transforms.AddRange(candidate.Transforms);
                Broadcast(new TransformsChanged(CloneTransforms(candidate.Transforms)));
            }
            _logger.LogInformation("Transformaciones actualizadas ({Count})", candidate.Transforms.Count);
            return GetTransforms();
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>Copia de los ajustes del servidor OPC UA.</summary>
    public OpcUaSettings GetOpcUa()
    {
        lock (_sync)
            return CloneOpcUa(Configuration.OpcUa ?? new OpcUaSettings());
    }

    /// <summary>
    /// Sustituye los ajustes del servidor OPC UA (los aplica quien aloja el servidor). Si no son válidos lanza
    /// <see cref="EngineConfigurationException"/> y no cambia nada.
    /// </summary>
    public async Task<OpcUaSettings> SetOpcUaAsync(OpcUaSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _changes.WaitAsync();
        try
        {
            var candidate = SnapshotConfiguration();
            candidate.OpcUa = CloneOpcUa(settings);
            var errors = ConfigurationValidator.Validate(candidate, _registry);
            if (errors.Count > 0)
                throw new EngineConfigurationException(errors);
            lock (_sync)
                Configuration.OpcUa = candidate.OpcUa;
            _logger.LogInformation("Ajustes OPC UA actualizados (activo: {Enabled}, puerto {Port}, {Users} usuarios)",
                candidate.OpcUa.Enabled, candidate.OpcUa.Port, candidate.OpcUa.Users.Count);
            return GetOpcUa();
        }
        finally
        {
            _changes.Release();
        }
    }

    private static OpcUaSettings CloneOpcUa(OpcUaSettings settings) => new()
    {
        Enabled = settings.Enabled,
        Port = settings.Port,
        AllowWrites = settings.AllowWrites,
        Users = [.. (settings.Users ?? []).Select(u => new OpcUaUser { Name = u.Name.Trim(), Role = u.Role, PasswordHash = u.PasswordHash })],
    };

    private static List<ValueTransform> CloneTransforms(IEnumerable<ValueTransform> transforms) =>
        [.. transforms.Select(t => new ValueTransform
        {
            Name = t.Name.Trim(),
            Entries = [.. (t.Entries ?? []).Select(e => new TransformEntry { Value = e.Value, Text = e.Text.Trim() })],
        })];

    /// <summary>Da de baja un dispositivo: lo detiene y lo quita de la configuración.</summary>
    public async Task DeleteDeviceAsync(string device)
    {
        await _changes.WaitAsync();
        try
        {
            var supervisor = Find(device);
            await supervisor.DisposeAsync();
            lock (_sync)
            {
                RemoveSupervisorLocked(supervisor);
                Configuration.Devices.Remove(ConfigOf(supervisor.Name));
                Broadcast(new DeviceRemoved(supervisor.Name));
            }
            _logger.LogInformation("{Device}: dado de baja", supervisor.Name);
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>Explora las variables del dispositivo (debe estar conectado y su driver permitir exploración).</summary>
    public Task<BrowseResult> BrowseAsync(string device, BrowseQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Find(device).BrowseAsync(query, cancellationToken);
    }

    /// <summary>Tags con su último valor. Si se indica dispositivo, solo los suyos.</summary>
    public IReadOnlyList<TagSnapshot> GetTags(string? device = null)
    {
        var supervisors = device is null ? null : new[] { Find(device) };
        lock (_sync)
            return (supervisors ?? _devices.Values.AsEnumerable()).SelectMany(s => s.Tags).Select(t => _values[t.Key]).ToArray();
    }

    /// <summary>Escribe un valor (en texto, cultura invariante) en un tag configurado.</summary>
    public Task<WriteResult> WriteAsync(string device, string tag, string value, string user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var supervisor = Find(device);
        TagSnapshot? snapshot;
        lock (_sync)
            _values.TryGetValue(new TagKey(supervisor.Name, tag), out snapshot);
        if (snapshot is null)
            return Task.FromResult(new WriteResult(tag, QualityCode.BadAddressInvalid, $"El tag '{tag}' no existe en '{supervisor.Name}'."));
        return supervisor.WriteAsync(snapshot.Definition, value, snapshot.Value, user, cancellationToken);
    }

    /// <summary>Lee direcciones cualesquiera del dispositivo bajo demanda (no tienen que estar configuradas).</summary>
    public Task<IReadOnlyList<TagValue>> ReadAsync(string device, IReadOnlyList<string> addresses, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        return Find(device).ReadAsync(addresses, cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, string>> GetDeviceInfoAsync(string device, CancellationToken cancellationToken = default) =>
        Find(device).GetDeviceInfoAsync(cancellationToken);

    /// <summary>
    /// Se suscribe a los cambios. Devuelve una foto del estado actual y, a partir de ella, todos los eventos sin huecos.
    /// Si el suscriptor no consume y se llenan <paramref name="capacity"/> eventos, su canal se cierra con
    /// <see cref="SubscriberOverflowException"/> (debe volver a suscribirse y recibirá una foto nueva).
    /// </summary>
    public EngineSubscription Subscribe(int capacity = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        var channel = Channel.CreateBounded<EngineEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var subscriber = new Subscriber(channel);
        lock (_sync)
        {
            _subscribers.Add(subscriber);
            var tags = Configuration.Devices.SelectMany(d => _devices[d.Name].Tags).Select(t => _values[t.Key]).ToArray();
            var devices = Configuration.Devices.Select(d => _statuses[d.Name]).ToArray();
            return new EngineSubscription(this, subscriber, tags, devices, CloneTransforms(Configuration.Transforms));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        await StopAsync();
        DeviceSupervisor[] all;
        lock (_sync)
            all = [.. _devices.Values];
        foreach (var supervisor in all)
            await supervisor.DisposeAsync();
        _changes.Dispose();
    }

    void IEngineSink.Publish(IReadOnlyList<TagUpdate> updates)
    {
        lock (_sync)
        {
            var changed = new List<TagUpdate>(updates.Count);
            foreach (var update in updates)
            {
                // Un dispositivo que se está dando de baja aún puede publicar sus últimos valores.
                if (!_values.TryGetValue(update.Key, out var current))
                    continue;
                if (update.Value.HasSameValueAndQuality(current.Value) && update.Value.ErrorDetail == current.Value.ErrorDetail)
                    continue;
                _values[update.Key] = current with { Value = update.Value };
                changed.Add(update);
            }
            if (changed.Count > 0)
                Broadcast(new TagValuesChanged(changed));
        }
    }

    void IEngineSink.Publish(DeviceStatus status)
    {
        lock (_sync)
        {
            if (!_statuses.ContainsKey(status.Device))
                return;
            _statuses[status.Device] = status;
            Broadcast(new DeviceStatusChanged(status));
        }
    }

    internal void Unsubscribe(Subscriber subscriber)
    {
        lock (_sync)
            _subscribers.Remove(subscriber);
        subscriber.Channel.Writer.TryComplete();
    }

    private void Broadcast(EngineEvent engineEvent)
    {
        for (var i = _subscribers.Count - 1; i >= 0; i--)
        {
            var subscriber = _subscribers[i];
            if (subscriber.Channel.Writer.TryWrite(engineEvent))
                continue;
            _logger.LogWarning("Suscriptor desconectado: no consume los eventos a tiempo");
            subscriber.Channel.Writer.TryComplete(new SubscriberOverflowException());
            _subscribers.RemoveAt(i);
        }
    }

    private DeviceSupervisor Find(string device)
    {
        lock (_sync)
        {
            return _devices.TryGetValue(device, out var supervisor)
                ? supervisor
                : throw new KeyNotFoundException($"Dispositivo desconocido '{device}'.");
        }
    }

    private DeviceConfiguration ConfigOf(string device) =>
        Configuration.Devices.First(d => string.Equals(d.Name, device, StringComparison.OrdinalIgnoreCase));

    private DeviceSupervisor AddSupervisorLocked(DeviceConfiguration device)
    {
        var supervisor = new DeviceSupervisor(device, _registry.Get(device.Driver), _loggerFactory, _time, this);
        _devices.Add(device.Name, supervisor);
        _statuses[device.Name] = supervisor.Status;
        var initial = TagValue.Bad(QualityCode.BadNotConnected, _time.GetUtcNow(), DeviceSupervisor.DisabledDetail);
        foreach (var tag in supervisor.Tags)
            _values[tag.Key] = new TagSnapshot(tag, initial);
        return supervisor;
    }

    private void RemoveSupervisorLocked(DeviceSupervisor supervisor)
    {
        _devices.Remove(supervisor.Name);
        _statuses.Remove(supervisor.Name);
        foreach (var tag in supervisor.Tags)
            _values.Remove(tag.Key);
    }

    private static DeviceConfiguration Clone(DeviceConfiguration device) =>
        ConfigurationFile.Parse(ConfigurationFile.Serialize(new AgentConfiguration { Devices = [device] })).Devices[0];

    internal sealed record Subscriber(Channel<EngineEvent> Channel);
}

/// <summary>Suscripción a los eventos del motor. Al liberarla deja de recibir.</summary>
public sealed class EngineSubscription : IDisposable
{
    private readonly TagEngine _engine;
    private readonly TagEngine.Subscriber _subscriber;

    internal EngineSubscription(TagEngine engine, TagEngine.Subscriber subscriber, IReadOnlyList<TagSnapshot> tags, IReadOnlyList<DeviceStatus> devices,
        IReadOnlyList<ValueTransform> transforms)
    {
        _engine = engine;
        _subscriber = subscriber;
        InitialTags = tags;
        InitialDevices = devices;
        InitialTransforms = transforms;
    }

    /// <summary>Transformaciones de valores en el momento de suscribirse.</summary>
    public IReadOnlyList<ValueTransform> InitialTransforms { get; }

    /// <summary>Valores de todos los tags en el momento de suscribirse.</summary>
    public IReadOnlyList<TagSnapshot> InitialTags { get; }

    /// <summary>Estado de todos los dispositivos en el momento de suscribirse.</summary>
    public IReadOnlyList<DeviceStatus> InitialDevices { get; }

    public ChannelReader<EngineEvent> Events => _subscriber.Channel.Reader;

    public void Dispose() => _engine.Unsubscribe(_subscriber);
}

public sealed class SubscriberOverflowException : Exception
{
    public SubscriberOverflowException()
        : base("El suscriptor no consumía los eventos a tiempo y se ha desconectado.")
    {
    }

    public SubscriberOverflowException(string message)
        : base(message)
    {
    }

    public SubscriberOverflowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>La configuración tiene errores y el motor no puede arrancar.</summary>
public sealed class EngineConfigurationException : Exception
{
    public EngineConfigurationException()
        : this([])
    {
    }

    public EngineConfigurationException(string message)
        : base(message)
    {
        Errors = [message];
    }

    public EngineConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = [message];
    }

    public EngineConfigurationException(IReadOnlyList<string> errors)
        : base("Configuración no válida:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "  - " + e)))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; } = [];
}
