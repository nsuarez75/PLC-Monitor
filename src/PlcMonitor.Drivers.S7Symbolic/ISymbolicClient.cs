using Microsoft.Extensions.Logging;
using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;
using LibException = S7CommPlusDriver.S7CommPlusException;

namespace PlcMonitor.Drivers.S7Symbolic;

/// <summary>Símbolo descubierto al explorar la CPU.</summary>
internal readonly record struct SymbolEntry(string Name, uint Softdatatype);

/// <summary>
/// Operaciones de la librería S7CommPlus que usa el driver (sustituible en tests).
/// Los errores de la librería se lanzan como <see cref="S7SymbolicException"/>.
/// </summary>
internal interface ISymbolicClient : IAsyncDisposable
{
    /// <summary>Seguridad negociada tras conectar (TLS o legacy), o null si no se sabe.</summary>
    string? NegotiatedSecurity { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Resuelve un símbolo a un tag nuevo listo para leer/escribir. null si no existe.</summary>
    Task<PlcTag?> ResolveTagAsync(string symbol, CancellationToken cancellationToken);

    /// <summary>Lee los tags: cada uno queda con su valor y su error de lectura.</summary>
    Task ReadTagsAsync(IReadOnlyList<PlcTag> tags, CancellationToken cancellationToken);

    /// <summary>Escribe los tags: cada uno queda con su error de escritura.</summary>
    Task WriteTagsAsync(IReadOnlyList<PlcTag> tags, CancellationToken cancellationToken);

    /// <summary>Explora todas las variables de la CPU.</summary>
    Task<IReadOnlyList<SymbolEntry>> BrowseAsync(CancellationToken cancellationToken);
}

/// <summary>Implementación real sobre <see cref="S7CommPlusClient"/> (fork de DotNetProjects.S7CommPlusDriver).</summary>
internal sealed class S7CommPlusSymbolicClient : ISymbolicClient
{
    private readonly S7CommPlusClient _client;

    public S7CommPlusSymbolicClient(S7SymbolicSettings settings, ILogger logger)
    {
        var timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs);
        _client = new S7CommPlusClient(new S7CommPlusClientOptions
        {
            Address = settings.Host,
            Password = settings.Password,
            Username = settings.Username,
            ConnectTimeout = timeout,
            RequestTimeout = timeout,
            SecurityMode = settings.Security switch
            {
                S7SymbolicSecurity.Tls => S7CommPlusSecurityMode.Tls,
                S7SymbolicSecurity.Legacy => S7CommPlusSecurityMode.LegacyChallenge,
                _ => S7CommPlusSecurityMode.Auto,
            },
            // La reconexión la hace el supervisor del motor y la doble llave de escritura la aplica el motor.
            AutoReconnect = false,
            WriteEnabled = true,
            Logger = logger,
        });
    }

    public string? NegotiatedSecurity => _client.Options.NegotiatedSecurityMode switch
    {
        S7CommPlusSecurityMode.Tls => "TLS",
        S7CommPlusSecurityMode.LegacyChallenge => "legacy (sin TLS)",
        _ => null,
    };

    public Task ConnectAsync(CancellationToken cancellationToken) =>
        TranslateAsync("Conexión", () => _client.ConnectAsync(cancellationToken));

    public async Task<PlcTag?> ResolveTagAsync(string symbol, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.GetTagBySymbolAsync(symbol, cancellationToken);
        }
        catch (LibException ex) when (ex.ErrorCode == S7Consts.errCliItemNotAvailable && !ex.IsTransient)
        {
            // Sintaxis de array no válida: la librería envuelve una ArgumentException.
            if (ex.InnerException is ArgumentException inner)
                throw new ArgumentException(inner.Message, nameof(symbol), ex);
            return null;
        }
        catch (LibException ex)
        {
            throw Wrap("Resolución de símbolo", ex);
        }
    }

    public Task ReadTagsAsync(IReadOnlyList<PlcTag> tags, CancellationToken cancellationToken) =>
        TranslateAsync("Lectura", () => _client.ReadAsync(tags, cancellationToken));

    public Task WriteTagsAsync(IReadOnlyList<PlcTag> tags, CancellationToken cancellationToken) =>
        TranslateAsync("Escritura", () => _client.WriteAsync(tags, cancellationToken));

    public async Task<IReadOnlyList<SymbolEntry>> BrowseAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<VarInfo> vars = [];
        await TranslateAsync("Exploración", async () => vars = await _client.BrowseAsync(cancellationToken));
        return vars.Select(v => new SymbolEntry(v.Name, v.Softdatatype)).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _client.DisconnectAsync();
        }
        catch (Exception ex) when (ex is LibException or IOException or ObjectDisposedException or TimeoutException)
        {
            // Al cerrar una sesión ya rota no hay nada útil que hacer con el error.
        }
        await _client.DisposeAsync();
    }

    private static async Task TranslateAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (LibException ex)
        {
            throw Wrap(operation, ex);
        }
    }

    private static S7SymbolicException Wrap(string operation, LibException ex) =>
        new(operation, ex.ErrorCode, ex.IsTransient || ex is S7CommPlusTimeoutException, ex);
}
