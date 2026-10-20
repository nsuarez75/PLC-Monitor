using Microsoft.Extensions.Logging;
using PlcMonitor.Engine;
using PlcMonitor.Engine.Configuration;

namespace PlcMonitor.Historian;

/// <summary>
/// Historiador del agente: escucha los cambios del motor, decide qué guardar por tag (<see cref="HistorySampler"/>),
/// vuelca a disco por lotes y aplica la retención. Sigue los cambios de configuración en caliente.
/// </summary>
public sealed class HistorianService : IAsyncDisposable
{
    /// <summary>Paso del reloj de los muestreadores (frecuencia mínima y tiempo máximo).</summary>
    internal static TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(6);

    private readonly TagEngine _engine;
    private readonly HistorianSettings _settings;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Lock _sync = new();
    private readonly Dictionary<TagKey, HistorySampler> _samplers = [];
    // Tags que disparan el histórico de otros → muestreadores que dependen de ellos (se sigue su valor aunque no se historicen).
    private readonly Dictionary<TagKey, List<HistorySampler>> _byTrigger = [];
    private readonly List<HistorySample> _buffer = [];
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private SqliteHistorianStore? _store;
    private CancellationTokenSource? _cts;
    private Task _events = Task.CompletedTask;
    private Task _clock = Task.CompletedTask;
    private Task? _stopping;
    private int _disposed;

    public HistorianService(TagEngine engine, HistorianSettings settings, string baseDirectory, ILogger<HistorianService> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _settings = settings;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        Path = System.IO.Path.GetFullPath(settings.Path, baseDirectory);
    }

    public bool Enabled => _settings.Enabled;

    /// <summary>Carpeta de los ficheros del histórico.</summary>
    public string Path { get; }

    public Task StartAsync()
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Historiador desactivado");
            return Task.CompletedTask;
        }
        _store = new SqliteHistorianStore(Path);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        // La primera suscripción se toma aquí y no en la tarea: al volver ya se escucha, así no se pierde el estado
        // inicial de los tags si el motor publica algo antes de que la tarea arranque.
        var subscription = _engine.Subscribe();
        _events = Task.Run(() => ListenAsync(subscription, token), CancellationToken.None);
        _clock = Task.Run(() => ClockAsync(token), CancellationToken.None);
        _logger.LogInformation("Historiador en {Path} (retención {Days} días)", Path, _settings.RetentionDays);
        return Task.CompletedTask;
    }

    /// <summary>Para el historiador y vuelca lo pendiente. Idempotente: varias llamadas (incluso a la vez) esperan la misma parada.</summary>
    public Task StopAsync()
    {
        lock (_sync)
            return _stopping ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        if (_cts is not { } cts)
            return;
        await cts.CancelAsync();
        await Task.WhenAll(_events, _clock);
        await FlushAsync(CancellationToken.None);
        cts.Dispose();
    }

    public HistorianStats GetStats()
    {
        var (size, files) = _store?.Usage() ?? (0, 0);
        lock (_sync)
            return new HistorianStats(_settings.Enabled, Path, _settings.RetentionDays, size, files, _samplers.Count, _buffer.Count);
    }

    /// <summary>Consulta el histórico (vuelca antes lo pendiente para incluir lo más reciente).</summary>
    public async Task<IReadOnlyList<HistorySeries>> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (_store is null)
            throw new InvalidOperationException("El historiador está desactivado en este agente.");
        if (query.To < query.From)
            throw new ArgumentException("El final del intervalo es anterior al inicio.", nameof(query));
        await FlushAsync(cancellationToken);
        var result = new List<HistorySeries>(query.Tags.Count);
        foreach (var key in query.Tags)
        {
            if (query.Interval is { } interval)
            {
                // Los agregados se calculan sobre los puntos en bruto (hasta un millón por tag).
                var raw = await _store.ReadAsync(key, query.From, query.To, 1_000_000, cancellationToken);
                var buckets = HistoryAggregator.Aggregate(raw, query.From, interval, query.Aggregate);
                result.Add(new HistorySeries(key, buckets.Take(query.MaxPoints).ToArray(), buckets.Count > query.MaxPoints));
            }
            else
            {
                var points = await _store.ReadAsync(key, query.From, query.To, query.MaxPoints, cancellationToken);
                result.Add(new HistorySeries(key, points.Take(query.MaxPoints).ToArray(), points.Count > query.MaxPoints));
            }
        }
        return result;
    }

    /// <summary>
    /// Pasa el histórico guardado con un nombre a otro (al renombrar un tag o un dispositivo). Antes vuelca lo pendiente,
    /// para que lo último guardado con el nombre anterior también se mueva. Devuelve cuántos tags se han movido.
    /// </summary>
    public async Task<int> RenameAsync(IReadOnlyList<HistoryRename> renames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(renames);
        if (_store is null)
            throw new InvalidOperationException("El historiador está desactivado en este agente.");
        foreach (var rename in renames)
        {
            if (string.IsNullOrWhiteSpace(rename.FromDevice) || string.IsNullOrWhiteSpace(rename.ToDevice))
                throw new ArgumentException("Falta el dispositivo de origen o de destino.", nameof(renames));
            if ((rename.FromTag is null) != (rename.ToTag is null) || rename.FromTag?.Length == 0 || rename.ToTag?.Length == 0)
                throw new ArgumentException("Indica el tag de origen y el de destino, o ninguno para todo el dispositivo.", nameof(renames));
        }
        await _flushLock.WaitAsync(cancellationToken);
        try
        {
            await FlushLockedAsync(cancellationToken);
            var moved = await _store.RenameAsync(renames, cancellationToken);
            _logger.LogInformation("Histórico: {Count} tags cambiados de nombre", moved);
            return moved;
        }
        finally
        {
            _flushLock.Release();
        }
    }

    /// <summary>Vuelca a disco las muestras acumuladas.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_store is null || Volatile.Read(ref _disposed) == 1)
            return;
        await _flushLock.WaitAsync(cancellationToken);
        try
        {
            await FlushLockedAsync(cancellationToken);
        }
        finally
        {
            _flushLock.Release();
        }
    }

    // Con _flushLock tomado.
    private async Task FlushLockedAsync(CancellationToken cancellationToken)
    {
        if (_store is not { } store)
            return;
        HistorySample[] batch;
        lock (_sync)
        {
            batch = [.. _buffer];
            _buffer.Clear();
        }
        if (batch.Length == 0)
            return;
        try
        {
            await store.WriteAsync(batch, cancellationToken);
        }
#pragma warning disable CA1031 // Un fallo de disco no debe tumbar el agente: se reintenta en el siguiente volcado.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "No se pudieron guardar {Count} muestras del histórico; se reintentará", batch.Length);
            lock (_sync)
                _buffer.InsertRange(0, batch);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        // Se espera a un volcado en curso antes de cerrar los ficheros (el semáforo no se libera: no hace falta).
        await _flushLock.WaitAsync();
        _store?.Dispose();
        _flushLock.Release();
    }

    private async Task ListenAsync(EngineSubscription first, CancellationToken token)
    {
        EngineSubscription? next = first;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var subscription = next ?? _engine.Subscribe();
                next = null;
                lock (_sync)
                {
                    var previous = new Dictionary<TagKey, HistorySampler>(_samplers);
                    _samplers.Clear();
                    foreach (var tag in subscription.InitialTags)
                        Configure(tag.Definition, previous);
                    IndexTriggers();
                    var now = _time.GetUtcNow();
                    foreach (var tag in subscription.InitialTags)
                        Offer(tag.Definition.Key, tag.Value, now);
                }
                await foreach (var engineEvent in subscription.Events.ReadAllAsync(token))
                    Handle(engineEvent);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (SubscriberOverflowException)
            {
                // Demasiados cambios a la vez: nueva suscripción (los muestreadores se reconstruyen con la foto nueva).
                _logger.LogWarning("Historiador: suscripción desbordada; se vuelve a suscribir");
            }
        }
        next?.Dispose(); // parado antes de llegar a usar la primera suscripción
    }

    private void Handle(EngineEvent engineEvent)
    {
        var now = _time.GetUtcNow();
        lock (_sync)
        {
            switch (engineEvent)
            {
                case TagValuesChanged changed:
                    foreach (var update in changed.Updates)
                        Offer(update.Key, update.Value, now);
                    break;
                case DeviceConfigured configured:
                    var previous = new Dictionary<TagKey, HistorySampler>();
                    foreach (var key in _samplers.Keys.Where(k => k.Device == configured.Status.Device).ToArray())
                    {
                        previous[key] = _samplers[key];
                        _samplers.Remove(key);
                    }
                    foreach (var tag in configured.Tags)
                        Configure(tag.Definition, previous);
                    IndexTriggers();
                    foreach (var tag in configured.Tags)
                        Offer(tag.Definition.Key, tag.Value, now);
                    break;
                case DeviceRemoved removed:
                    foreach (var key in _samplers.Keys.Where(k => k.Device == removed.Device).ToArray())
                        _samplers.Remove(key);
                    IndexTriggers();
                    break;
                default:
                    break;
            }
        }
    }

    private void Configure(TagDefinition definition, Dictionary<TagKey, HistorySampler> previous)
    {
        if (!definition.IsHistorized)
            return;
        var sampler = new HistorySampler(definition.Key, definition.History!);
        if (previous.TryGetValue(definition.Key, out var old))
            sampler.ContinueFrom(old);
        _samplers[definition.Key] = sampler;
    }

    private void IndexTriggers()
    {
        _byTrigger.Clear();
        foreach (var sampler in _samplers.Values)
        {
            if (sampler.TriggerKey is { } trigger)
            {
                if (!_byTrigger.TryGetValue(trigger, out var list))
                    _byTrigger[trigger] = list = [];
                list.Add(sampler);
            }
        }
    }

    private void Offer(TagKey key, Abstractions.TagValue value, DateTimeOffset now)
    {
        if (_byTrigger.TryGetValue(key, out var dependents))
        {
            foreach (var dependent in dependents)
            {
                if (dependent.OfferTrigger(value, now) is { } triggered)
                    _buffer.Add(triggered);
            }
        }
        if (_samplers.TryGetValue(key, out var sampler) && sampler.Offer(value, now) is { } sample)
            _buffer.Add(sample);
    }

    private async Task ClockAsync(CancellationToken token)
    {
        try
        {
            await ClockLoopAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Parada durante un volcado o una purga: el volcado final lo hace StopAsync.
        }
    }

    private async Task ClockLoopAsync(CancellationToken token)
    {
        var lastFlush = _time.GetUtcNow();
        var lastPurge = DateTimeOffset.MinValue;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, _time, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            var now = _time.GetUtcNow();
            int pending;
            lock (_sync)
            {
                foreach (var sampler in _samplers.Values)
                {
                    if (sampler.Tick(now) is { } sample)
                        _buffer.Add(sample);
                }
                pending = _buffer.Count;
            }
            if (pending > 0 && (now - lastFlush >= TimeSpan.FromMilliseconds(_settings.FlushIntervalMs) || pending >= 5000))
            {
                await FlushAsync(token);
                lastFlush = now;
            }
            if (_settings.RetentionDays > 0 && now - lastPurge >= PurgeInterval)
            {
                lastPurge = now;
                try
                {
                    await _store!.PurgeAsync(now.AddDays(-_settings.RetentionDays), token);
                }
#pragma warning disable CA1031 // La purga se reintenta en el siguiente ciclo.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    _logger.LogError(ex, "No se pudo aplicar la retención del histórico");
                }
            }
        }
    }
}
