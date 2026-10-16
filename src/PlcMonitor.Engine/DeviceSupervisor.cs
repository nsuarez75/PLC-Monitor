using Microsoft.Extensions.Logging;
using PlcMonitor.Abstractions;
using PlcMonitor.Core;
using PlcMonitor.Engine.Configuration;

namespace PlcMonitor.Engine;

/// <summary>Destino de lo que producen los supervisores (el motor).</summary>
internal interface IEngineSink
{
    void Publish(IReadOnlyList<TagUpdate> updates);

    void Publish(DeviceStatus status);
}

/// <summary>
/// Vigila un dispositivo: conecta, lee sus grupos de escaneo y, si la conexión cae, marca sus tags como malos y
/// reintenta con espera exponencial. Mientras está arrancado no se rinde nunca; se detiene con <see cref="StopAsync"/>.
/// </summary>
internal sealed class DeviceSupervisor : IAsyncDisposable
{
    internal const string DisabledDetail = "Dispositivo deshabilitado";

    private DeviceConfiguration _config;
    private readonly IDriverFactory _factory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly ILogger _audit;
    private readonly TimeProvider _time;
    private readonly IEngineSink _sink;
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task _loop = Task.CompletedTask;
    private volatile IDeviceConnection? _connection;
    private DeviceStatus _status;
    private int _disposed;
    private IReadOnlyList<TagKey> _pollableKeys = [];
    // Corta los grupos de escaneo en marcha para rehacerlos con los tags nuevos sin cerrar la conexión.
    private CancellationTokenSource? _regroup;

    public DeviceSupervisor(DeviceConfiguration config, IDriverFactory factory, ILoggerFactory loggerFactory, TimeProvider time, IEngineSink sink)
    {
        _config = config;
        _factory = factory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DeviceSupervisor>();
        _audit = loggerFactory.CreateLogger("PlcMonitor.Audit");
        _time = time;
        _sink = sink;
        Tags = BuildTags(config);
        _status = new DeviceStatus(config.Name, config.Driver, Enabled: false, config.WritesEnabled,
            ConnectionState.Disconnected, DisabledDetail, time.GetUtcNow());
    }

    public string Name => _config.Name;

    public IReadOnlyList<TagDefinition> Tags { get; private set; }

    /// <summary>Definiciones de los tags de una configuración de dispositivo.</summary>
    public static TagDefinition[] BuildTags(DeviceConfiguration config) => config.ScanGroups
        .SelectMany(g => g.Tags.Select(t => new TagDefinition(
            new TagKey(config.Name, t.EffectiveName), t.Address, g.Name, TimeSpan.FromMilliseconds(g.IntervalMs), t.Writable, t.DataType)
        {
            History = t.History,
        }))
        .ToArray();

    /// <summary>
    /// Cambia en caliente los grupos, los tags y la llave de escritura sin tocar la conexión: los grupos de escaneo se
    /// rehacen y siguen leyendo con la misma sesión. Solo vale si driver, ajustes de conexión y reconexión no cambian.
    /// </summary>
    public void Reconfigure(DeviceConfiguration config)
    {
        CancellationTokenSource? regroup;
        lock (_sync)
        {
            _config = config;
            Tags = BuildTags(config);
            regroup = _regroup;
        }
        SetStatus(s => s with { WritesEnabled = config.WritesEnabled });
        try
        {
            regroup?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Los grupos ya se estaban rehaciendo: leerán los tags nuevos.
        }
    }

    public DeviceStatus Status
    {
        get
        {
            lock (_sync)
                return _status;
        }
    }

    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_cts is not null)
                return;
            _cts = new CancellationTokenSource();
            SetStatus(s => s with { Enabled = true, State = ConnectionState.Disconnected, Detail = null, NextRetry = null });
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_cts is null)
                return;
            await _cts.CancelAsync();
            await _loop;
            _cts.Dispose();
            _cts = null;
            SetStatus(s => s with { Enabled = false, State = ConnectionState.Disconnected, Detail = DisabledDetail, NextRetry = null });
            PublishBad(Tags.Select(t => t.Key).ToArray(), QualityCode.BadNotConnected, DisabledDetail);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Lee direcciones bajo demanda (fuera de los grupos de escaneo).</summary>
    public async Task<IReadOnlyList<TagValue>> ReadAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is null)
        {
            var bad = TagValue.Bad(QualityCode.BadNotConnected, _time.GetUtcNow(), "No conectado");
            return Enumerable.Repeat(bad, addresses.Count).ToArray();
        }
        return await connection.ReadAsync(addresses, cancellationToken);
    }

    /// <summary>Explora las variables del dispositivo con la conexión en uso.</summary>
    public Task<BrowseResult> BrowseAsync(BrowseQuery query, CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is not IBrowsableConnection browsable)
        {
            throw connection is null
                ? new InvalidOperationException($"'{Name}' no está conectado.")
                : new NotSupportedException($"El driver '{_config.Driver}' no permite explorar variables.");
        }
        return browsable.BrowseAsync(query, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetDeviceInfoAsync(CancellationToken cancellationToken)
    {
        var connection = _connection;
        return connection is null ? new Dictionary<string, string>() : await connection.GetDeviceInfoAsync(cancellationToken);
    }

    /// <summary>
    /// Escribe un valor en texto en un tag. Exige las dos llaves (WritesEnabled del dispositivo y Writable del tag)
    /// y deja constancia en el log de auditoría.
    /// </summary>
    public async Task<WriteResult> WriteAsync(TagDefinition tag, string text, TagValue current, string user, CancellationToken cancellationToken)
    {
        var result = await WriteCoreAsync(tag, text, current, cancellationToken);
        _audit.LogInformation(
            "Escritura {Tag} ({Address}) = {Value} (antes {Previous}) por {User}: {Quality} {Detail}",
            tag.Key, tag.Address, text, ValueFormatter.Format(current), user, result.Quality, result.ErrorDetail);
        return result;
    }

    private async Task<WriteResult> WriteCoreAsync(TagDefinition tag, string text, TagValue current, CancellationToken cancellationToken)
    {
        if (!_config.WritesEnabled)
            return new WriteResult(tag.Address, QualityCode.BadWriteDisabled, "Las escrituras están deshabilitadas en el dispositivo.");
        if (!tag.Writable)
            return new WriteResult(tag.Address, QualityCode.BadWriteDisabled, "El tag no es escribible.");
        var connection = _connection;
        if (connection is null || connection.State != ConnectionState.Connected)
            return new WriteResult(tag.Address, QualityCode.BadNotConnected, "No conectado");

        var type = tag.DataType ?? TagDataType.Unknown;
        if (type == TagDataType.Unknown)
            type = current.DataType;
        if (type == TagDataType.Unknown)
            type = connection.ValidateAddress(tag.Address).DataType;
        if (type == TagDataType.Unknown)
            type = (await connection.ReadAsync([tag.Address], cancellationToken))[0].DataType;
        if (type == TagDataType.Unknown)
            return new WriteResult(tag.Address, QualityCode.BadTypeMismatch, "No se conoce el tipo del tag; indica dataType en la configuración.");

        if (!ValueParser.TryParse(text, type, out var value, out var error))
            return new WriteResult(tag.Address, QualityCode.BadTypeMismatch, error);
        return (await connection.WriteAsync([new WriteRequest(tag.Address, value)], cancellationToken))[0];
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        await StopAsync();
        _lifecycle.Dispose();
    }

    private async Task RunAsync(CancellationToken token)
    {
        IDeviceConnection connection;
        try
        {
            connection = _factory.CreateConnection(_config.ToDeviceDefinition(), _loggerFactory);
        }
#pragma warning disable CA1031 // Una configuración rechazada por el driver deja el dispositivo en fallo, no tumba el motor.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "{Device}: no se pudo crear la conexión", Name);
            SetStatus(s => s with { State = ConnectionState.Faulted, Detail = ex.Message });
            PublishBad(Tags.Select(t => t.Key).ToArray(), QualityCode.BadConfiguration, ex.Message);
            return;
        }

        await using (connection)
        {
            var backoff = new ReconnectBackoff(_config.Reconnect);
            while (!token.IsCancellationRequested)
            {
                string detail;
                SetStatus(s => s with { State = ConnectionState.Connecting, Detail = null, NextRetry = null });
                try
                {
                    await connection.ConnectAsync(token);
                    backoff.Reset();
                    _connection = connection;
                    SetStatus(s => s with { State = ConnectionState.Connected, Detail = null, NextRetry = null });
                    detail = await RunGroupsAsync(connection, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
#pragma warning disable CA1031 // Cualquier fallo de conexión se reintenta.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    detail = ex.Message;
                }
                finally
                {
                    _connection = null;
                }

                if (token.IsCancellationRequested)
                    break;
                PublishBad(_pollableKeys, QualityCode.BadNotConnected, detail);
                var delay = backoff.Next();
                _logger.LogWarning("{Device}: sin conexión ({Detail}); reintento {Attempt} en {Delay}", Name, detail, backoff.Attempts, delay);
                SetStatus(s => s with { State = ConnectionState.Faulted, Detail = detail, NextRetry = _time.GetUtcNow() + delay });
                try
                {
                    await Task.Delay(delay, _time, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            await connection.DisconnectAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Lee los grupos hasta que se pierde la conexión o se cancela. Devuelve el motivo de la pérdida. Si los tags cambian
    /// (<see cref="Reconfigure"/>), rehace los grupos y sigue con la misma conexión.
    /// </summary>
    private async Task<string> RunGroupsAsync(IDeviceConnection connection, CancellationToken token)
    {
        using var lost = CancellationTokenSource.CreateLinkedTokenSource(token);
        string? detail = null;
        void OnStateChanged(object? sender, ConnectionStateChangedEventArgs e)
        {
            if (e.State == ConnectionState.Connected)
                return;
            detail = e.Detail ?? e.State.ToString();
            try
            {
                lost.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        connection.StateChanged += OnStateChanged;
        try
        {
            if (connection.State != ConnectionState.Connected)
                return "Conexión perdida";
            while (true)
            {
                var regroup = CancellationTokenSource.CreateLinkedTokenSource(lost.Token);
                lock (_sync)
                    _regroup = regroup;
                try
                {
                    var groups = PrepareGroups(connection);
                    if (groups.Count == 0)
                        await Task.Delay(Timeout.InfiniteTimeSpan, _time, regroup.Token);
                    else
                        await Task.WhenAll(groups.Select(g => RunGroupAsync(connection, g, regroup.Token)));
                }
                catch (OperationCanceledException) when (regroup.IsCancellationRequested && !lost.IsCancellationRequested)
                {
                    _logger.LogInformation("{Device}: tags reconfigurados en caliente", Name);
                }
                finally
                {
                    lock (_sync)
                        _regroup = null;
                    regroup.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (lost.IsCancellationRequested)
        {
        }
        finally
        {
            connection.StateChanged -= OnStateChanged;
        }
        token.ThrowIfCancellationRequested();
        return detail ?? "Conexión perdida";
    }

    private async Task RunGroupAsync(IDeviceConnection connection, ScanGroup group, CancellationToken token)
    {
        await foreach (var changes in TagPoller.WatchAsync(connection, group.Addresses, group.Interval, _time, token))
        {
            var updates = new List<TagUpdate>(changes.Count);
            foreach (var change in changes)
            {
                foreach (var key in group.KeysByAddress[change.Address])
                    updates.Add(new TagUpdate(key, change.Value));
            }
            _sink.Publish(updates);
        }
    }

    /// <summary>Agrupa los tags por grupo de escaneo. Los de dirección no válida quedan con calidad BadConfiguration.</summary>
    private List<ScanGroup> PrepareGroups(IDeviceConnection connection)
    {
        var groups = new List<ScanGroup>();
        var invalid = new List<TagUpdate>();
        foreach (var group in Tags.GroupBy(t => t.Group))
        {
            var keysByAddress = new Dictionary<string, List<TagKey>>(StringComparer.Ordinal);
            foreach (var tag in group)
            {
                var validation = connection.ValidateAddress(tag.Address);
                if (!validation.IsValid)
                {
                    _logger.LogWarning("{Tag}: dirección no válida '{Address}': {Error}", tag.Key, tag.Address, validation.Error);
                    invalid.Add(new TagUpdate(tag.Key, TagValue.Bad(QualityCode.BadConfiguration, _time.GetUtcNow(), validation.Error)));
                    continue;
                }
                if (!keysByAddress.TryGetValue(tag.Address, out var keys))
                    keysByAddress[tag.Address] = keys = [];
                keys.Add(tag.Key);
            }
            if (keysByAddress.Count > 0)
            {
                groups.Add(new ScanGroup(
                    group.Key,
                    group.First().Interval,
                    [.. keysByAddress.Keys],
                    keysByAddress.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<TagKey>)kv.Value, StringComparer.Ordinal)));
            }
        }
        if (invalid.Count > 0)
            _sink.Publish(invalid);
        _pollableKeys = groups.SelectMany(g => g.KeysByAddress.Values.SelectMany(k => k)).ToArray();
        return groups;
    }

    private void PublishBad(IReadOnlyList<TagKey> keys, QualityCode quality, string? detail)
    {
        if (keys.Count == 0)
            return;
        var bad = TagValue.Bad(quality, _time.GetUtcNow(), detail);
        _sink.Publish(keys.Select(k => new TagUpdate(k, bad)).ToArray());
    }

    private void SetStatus(Func<DeviceStatus, DeviceStatus> change)
    {
        DeviceStatus status;
        lock (_sync)
        {
            status = change(_status) with { Timestamp = _time.GetUtcNow() };
            if (status with { Timestamp = _status.Timestamp } == _status)
                return;
            _status = status;
        }
        _sink.Publish(status);
    }

    private sealed record ScanGroup(string Name, TimeSpan Interval, string[] Addresses, IReadOnlyDictionary<string, IReadOnlyList<TagKey>> KeysByAddress);
}
