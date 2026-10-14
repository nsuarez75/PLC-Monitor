using Microsoft.Extensions.Logging;

namespace PlcMonitor.Abstractions;

/// <summary>
/// Base para conexiones de drivers. Gestiona el estado, serializa las operaciones (un único acceso al
/// dispositivo a la vez) y convierte los fallos de comunicación en calidades malas.
/// Los drivers implementan solo los métodos *Core.
/// </summary>
public abstract class DeviceConnectionBase : IDeviceConnection
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    protected DeviceConnectionBase(string deviceName, ILogger logger, TimeProvider? timeProvider = null)
    {
        DeviceName = deviceName;
        Logger = logger;
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    public string DeviceName { get; }

    public abstract DriverCapabilities Capabilities { get; }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    protected ILogger Logger { get; }

    protected TimeProvider TimeProvider { get; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State == ConnectionState.Connected)
                return;
            SetState(ConnectionState.Connecting, null);
            await OpenCoreAsync(cancellationToken);
            SetState(ConnectionState.Connected, null);
        }
        catch (OperationCanceledException)
        {
            await SafeCloseAsync();
            SetState(ConnectionState.Disconnected, "Cancelado");
            throw;
        }
        catch (Exception ex)
        {
            await SafeCloseAsync();
            SetState(ConnectionState.Faulted, ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SafeCloseAsync();
            SetState(ConnectionState.Disconnected, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public abstract AddressValidation ValidateAddress(string address);

    public async Task<IReadOnlyList<TagValue>> ReadAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 0)
            return [];
        if (State != ConnectionState.Connected)
            return AllBad(addresses.Count, QualityCode.BadNotConnected, "No conectado");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadCoreAsync(addresses, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && IsConnectionLost(ex))
        {
            Logger.LogWarning(ex, "{Device}: conexión perdida durante la lectura", DeviceName);
            await SafeCloseAsync();
            SetState(ConnectionState.Faulted, ex.Message);
            return AllBad(addresses.Count, QualityCode.BadNotConnected, ex.Message);
        }
#pragma warning disable CA1031 // Un fallo inesperado del driver se refleja como calidad mala.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Logger.LogWarning(ex, "{Device}: error de lectura", DeviceName);
            return AllBad(addresses.Count, QualityCode.Bad, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<WriteRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
            return [];
        if (State != ConnectionState.Connected)
            return requests.Select(r => new WriteResult(r.Address, QualityCode.BadNotConnected, "No conectado")).ToArray();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await WriteCoreAsync(requests, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && IsConnectionLost(ex))
        {
            Logger.LogWarning(ex, "{Device}: conexión perdida durante la escritura", DeviceName);
            await SafeCloseAsync();
            SetState(ConnectionState.Faulted, ex.Message);
            return requests.Select(r => new WriteResult(r.Address, QualityCode.BadNotConnected, ex.Message)).ToArray();
        }
#pragma warning disable CA1031 // Un fallo inesperado del driver se refleja como resultado malo.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Logger.LogWarning(ex, "{Device}: error de escritura", DeviceName);
            return requests.Select(r => new WriteResult(r.Address, QualityCode.Bad, ex.Message)).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> GetDeviceInfoAsync(CancellationToken cancellationToken = default)
    {
        if (State != ConnectionState.Connected)
            return new Dictionary<string, string>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await GetDeviceInfoCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await SafeCloseAsync();
        DisposeCore();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Ejecuta <paramref name="action"/> con acceso exclusivo al dispositivo (para operaciones propias del driver).</summary>
    protected async Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (State != ConnectionState.Connected)
            throw new InvalidOperationException($"{DeviceName}: no conectado.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await action(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && IsConnectionLost(ex))
        {
            await SafeCloseAsync();
            SetState(ConnectionState.Faulted, ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected abstract Task OpenCoreAsync(CancellationToken cancellationToken);

    /// <summary>Cierra la comunicación. No debe lanzar excepciones.</summary>
    protected abstract Task CloseCoreAsync();

    /// <summary>Lee las direcciones. Los errores por dirección se devuelven como calidad mala, no como excepción.</summary>
    protected abstract Task<IReadOnlyList<TagValue>> ReadCoreAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken);

    protected abstract Task<IReadOnlyList<WriteResult>> WriteCoreAsync(IReadOnlyList<WriteRequest> requests, CancellationToken cancellationToken);

    protected virtual Task<IReadOnlyDictionary<string, string>> GetDeviceInfoCoreAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

    /// <summary>Indica si una excepción significa que la comunicación se ha perdido (y hay que reconectar).</summary>
    protected virtual bool IsConnectionLost(Exception exception) =>
        exception is IOException or System.Net.Sockets.SocketException or TimeoutException or ObjectDisposedException;

    protected virtual void DisposeCore()
    {
    }

    protected IReadOnlyList<TagValue> AllBad(int count, QualityCode quality, string? detail)
    {
        var bad = TagValue.Bad(quality, TimeProvider.GetUtcNow(), detail);
        return Enumerable.Repeat(bad, count).ToArray();
    }

    private void SetState(ConnectionState state, string? detail)
    {
        if (State == state)
            return;
        State = state;
        Logger.LogInformation("{Device}: {State} {Detail}", DeviceName, state, detail);
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state, detail));
    }

    private async Task SafeCloseAsync()
    {
        try
        {
            await CloseCoreAsync();
        }
#pragma warning disable CA1031 // Cerrar nunca debe propagar errores.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Logger.LogDebug(ex, "{Device}: error al cerrar", DeviceName);
        }
    }
}
