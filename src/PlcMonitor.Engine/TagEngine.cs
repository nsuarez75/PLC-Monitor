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
        _time = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory.CreateLogger<TagEngine>();
        foreach (var device in configuration.Devices)
        {
            var supervisor = new DeviceSupervisor(device, registry.Get(device.Driver), loggerFactory, _time, this);
            _devices.Add(device.Name, supervisor);
            _statuses[device.Name] = supervisor.Status;
            var initial = TagValue.Bad(QualityCode.BadNotConnected, _time.GetUtcNow(), DeviceSupervisor.DisabledDetail);
            foreach (var tag in supervisor.Tags)
                _values[tag.Key] = new TagSnapshot(tag, initial);
        }
    }

    /// <summary>Configuración en uso. Refleja los cambios hechos en caliente (p.ej. activar un dispositivo).</summary>
    public AgentConfiguration Configuration { get; }

    /// <summary>Arranca los dispositivos que tienen Enabled.</summary>
    public async Task StartAsync()
    {
        foreach (var device in Configuration.Devices.Where(d => d.Enabled))
            await _devices[device.Name].StartAsync();
        _logger.LogInformation("Motor arrancado: {Enabled} de {Total} dispositivos activos",
            Configuration.Devices.Count(d => d.Enabled), Configuration.Devices.Count);
    }

    public async Task StopAsync()
    {
        await Task.WhenAll(_devices.Values.Select(d => d.StopAsync()));
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
        var supervisor = Find(device);
        Configuration.Devices.First(d => d.Name == supervisor.Name).Enabled = enabled;
        if (enabled)
            await supervisor.StartAsync();
        else
            await supervisor.StopAsync();
        _logger.LogInformation("{Device}: {Action}", supervisor.Name, enabled ? "activado" : "desactivado");
        return GetDevice(supervisor.Name);
    }

    /// <summary>Tags con su último valor. Si se indica dispositivo, solo los suyos.</summary>
    public IReadOnlyList<TagSnapshot> GetTags(string? device = null)
    {
        var supervisors = device is null ? _devices.Values.AsEnumerable() : [Find(device)];
        lock (_sync)
            return supervisors.SelectMany(s => s.Tags).Select(t => _values[t.Key]).ToArray();
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
            var tags = _devices.Values.SelectMany(s => s.Tags).Select(t => _values[t.Key]).ToArray();
            var devices = Configuration.Devices.Select(d => _statuses[d.Name]).ToArray();
            return new EngineSubscription(this, subscriber, tags, devices);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        await StopAsync();
        foreach (var supervisor in _devices.Values)
            await supervisor.DisposeAsync();
    }

    void IEngineSink.Publish(IReadOnlyList<TagUpdate> updates)
    {
        lock (_sync)
        {
            var changed = new List<TagUpdate>(updates.Count);
            foreach (var update in updates)
            {
                var current = _values[update.Key];
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

    private DeviceSupervisor Find(string device) =>
        _devices.TryGetValue(device, out var supervisor)
            ? supervisor
            : throw new KeyNotFoundException($"Dispositivo desconocido '{device}'.");

    internal sealed record Subscriber(Channel<EngineEvent> Channel);
}

/// <summary>Suscripción a los eventos del motor. Al liberarla deja de recibir.</summary>
public sealed class EngineSubscription : IDisposable
{
    private readonly TagEngine _engine;
    private readonly TagEngine.Subscriber _subscriber;

    internal EngineSubscription(TagEngine engine, TagEngine.Subscriber subscriber, IReadOnlyList<TagSnapshot> tags, IReadOnlyList<DeviceStatus> devices)
    {
        _engine = engine;
        _subscriber = subscriber;
        InitialTags = tags;
        InitialDevices = devices;
    }

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
