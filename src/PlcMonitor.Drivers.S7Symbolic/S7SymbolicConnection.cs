using System.Globalization;
using Microsoft.Extensions.Logging;
using PlcMonitor.Abstractions;
using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;

namespace PlcMonitor.Drivers.S7Symbolic;

/// <summary>
/// Conexión simbólica S7CommPlus con S7-1200/1500: TLS o autenticación clásica sin TLS, más legitimación por contraseña.
/// Las direcciones son nombres simbólicos: <c>"MiDB".Motor.Velocidad</c>, <c>MiDB.Array[3]</c> o el nombre devuelto por la exploración.
/// </summary>
internal sealed class S7SymbolicConnection : DeviceConnectionBase, IBrowsableConnection
{
    private readonly S7SymbolicSettings _settings;
    private readonly Func<ISymbolicClient> _clientFactory;

    // Tags ya resueltos (o el motivo por el que no se pudieron resolver). Se vacían al reconectar.
    // Las escrituras usan instancias propias para no alterar el valor leído (y conservar la longitud de los String).
    private readonly Dictionary<string, (PlcTag? Tag, string? Error)> _resolved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (PlcTag? Tag, string? Error)> _resolvedForWrite = new(StringComparer.Ordinal);
    private ISymbolicClient? _client;
    private DateTimeOffset? _metadataRefreshed;
    private IReadOnlyList<SymbolEntry>? _symbols;
    private int _symbolCount;

    public S7SymbolicConnection(
        string deviceName, S7SymbolicSettings settings, ILogger logger, Func<ISymbolicClient>? clientFactory = null, TimeProvider? timeProvider = null)
        : base(deviceName, logger, timeProvider)
    {
        _settings = settings;
        _clientFactory = clientFactory ?? (() => new S7CommPlusSymbolicClient(settings, logger));
    }

    public override DriverCapabilities Capabilities => DriverCapabilities.Read | DriverCapabilities.Write | DriverCapabilities.Browse;

    public override AddressValidation ValidateAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return AddressValidation.Invalid("Símbolo vacío.");
        if (address.Count(c => c == '"') % 2 != 0)
            return AddressValidation.Invalid("Comillas sin cerrar.");
        if (address.Count(c => c == '[') != address.Count(c => c == ']'))
            return AddressValidation.Invalid("Corchetes sin cerrar.");
        if (address.EndsWith('.') || address.StartsWith('.') || address.Contains("..", StringComparison.Ordinal))
            return AddressValidation.Invalid("Separador '.' mal colocado.");
        // El tipo solo se conoce al resolver el símbolo en la CPU.
        return AddressValidation.Valid();
    }

    public Task<BrowseResult> BrowseAsync(BrowseQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return RunExclusiveAsync(ct => BrowseCoreAsync(query, ct), cancellationToken);
    }

    protected override async Task OpenCoreAsync(CancellationToken cancellationToken)
    {
        var client = _clientFactory();
        try
        {
            await client.ConnectAsync(cancellationToken);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
        _client = client;
        _resolved.Clear();
        _resolvedForWrite.Clear();
        Logger.LogInformation("{Device}: sesión S7CommPlus abierta ({Security})", DeviceName, client.NegotiatedSecurity ?? "?");
    }

    protected override async Task CloseCoreAsync()
    {
        var client = _client;
        _client = null;
        _resolved.Clear();
        _resolvedForWrite.Clear();
        if (client is not null)
            await client.DisposeAsync();
    }

    protected override Task<IReadOnlyDictionary<string, string>> GetDeviceInfoCoreAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>
        {
            ["Host"] = _settings.Host,
            ["Protocol"] = "S7CommPlus",
            ["Security"] = _client?.NegotiatedSecurity ?? _settings.Security.ToString().ToLowerInvariant(),
            ["Legitimation"] = string.IsNullOrEmpty(_settings.Password) ? "sin contraseña" : "contraseña",
            ["SymbolsLoaded"] = _symbols is null ? "no" : _symbols.Count.ToString(CultureInfo.InvariantCulture),
        });

    protected override bool IsConnectionLost(Exception exception) =>
        base.IsConnectionLost(exception) || exception is S7SymbolicException { IsCommunicationError: true };

    private ISymbolicClient Client => _client ?? throw new IOException("Sesión S7CommPlus cerrada.");

    protected override async Task<IReadOnlyList<TagValue>> ReadCoreAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken)
    {
        var client = Client;
        var results = new TagValue[addresses.Count];
        var resolved = new PlcTag?[addresses.Count];
        // Si la misma dirección aparece varias veces, el tag se lee una sola vez.
        var distinct = new HashSet<PlcTag>(ReferenceEqualityComparer.Instance);

        for (var i = 0; i < addresses.Count; i++)
        {
            var (tag, error) = await ResolveAsync(client, _resolved, addresses[i], cancellationToken);
            if (tag is null)
                results[i] = TagValue.Bad(QualityCode.BadAddressInvalid, TimeProvider.GetUtcNow(), error);
            else
            {
                distinct.Add(tag);
                resolved[i] = tag;
            }
        }

        if (distinct.Count > 0)
            await client.ReadTagsAsync(distinct.ToList(), cancellationToken);

        var timestamp = TimeProvider.GetUtcNow();
        for (var i = 0; i < addresses.Count; i++)
        {
            if (resolved[i] is not { } tag)
                continue;
            var quality = PlcTagValueMapper.QualityOf(tag);
            if (quality.IsGood())
            {
                var (value, type) = PlcTagValueMapper.GetValue(tag);
                results[i] = TagValue.Good(value, type, timestamp);
            }
            else
            {
                results[i] = TagValue.Bad(quality, timestamp, $"Error de lectura 0x{tag.LastReadError:X}");
            }
        }
        return results;
    }

    protected override async Task<IReadOnlyList<WriteResult>> WriteCoreAsync(IReadOnlyList<WriteRequest> requests, CancellationToken cancellationToken)
    {
        var client = Client;
        var results = new WriteResult?[requests.Count];
        var tags = new List<PlcTag>();
        var tagIndexes = new List<int>();

        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            var (tag, error) = await ResolveAsync(client, _resolvedForWrite, request.Address, cancellationToken);
            if (tag is null)
            {
                results[i] = new WriteResult(request.Address, QualityCode.BadAddressInvalid, error);
                continue;
            }
            if (tags.Contains(tag))
            {
                results[i] = new WriteResult(request.Address, QualityCode.BadConfiguration, "Dirección repetida en la misma escritura.");
                continue;
            }
            if (!PlcTagValueMapper.TrySetValue(tag, request.Value, out var convError))
            {
                results[i] = new WriteResult(request.Address, QualityCode.BadTypeMismatch, convError);
                continue;
            }
            tags.Add(tag);
            tagIndexes.Add(i);
        }

        if (tags.Count > 0)
        {
            await client.WriteTagsAsync(tags, cancellationToken);
            for (var t = 0; t < tags.Count; t++)
            {
                var i = tagIndexes[t];
                var error = tags[t].LastWriteError;
                results[i] = error == 0
                    ? new WriteResult(requests[i].Address, QualityCode.Good)
                    : new WriteResult(requests[i].Address, QualityCode.BadAccessDenied, $"La CPU rechazó la escritura (0x{error:X})");
            }
        }
        return results.Select(r => r!).ToArray();
    }

    private async Task<(PlcTag? Tag, string? Error)> ResolveAsync(
        ISymbolicClient client, Dictionary<string, (PlcTag? Tag, string? Error)> cache, string address, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(address, out var cached))
            return cached;

        var validation = ValidateAddress(address);
        (PlcTag?, string?) entry;
        if (!validation.IsValid)
        {
            entry = (null, validation.Error);
        }
        else
        {
            try
            {
                var tag = await client.ResolveTagAsync(address.Trim(), cancellationToken);
                // Puede ser un símbolo añadido al PLC después de conectar: se vuelve a leer el programa y se reintenta
                // (como mucho una vez cada MetadataRefreshInterval, para no releerlo por cada símbolo que de verdad no existe).
                if (tag is null && await RefreshMetadataAsync(client, force: false, cancellationToken))
                    tag = await client.ResolveTagAsync(address.Trim(), cancellationToken);
                entry = tag is null ? (null, $"El símbolo '{address}' no existe en la CPU.") : (tag, null);
            }
            catch (ArgumentException ex)
            {
                entry = (null, $"Símbolo '{address}' no válido: {ex.Message}");
            }
        }
        cache[address] = entry;
        return entry;
    }

    /// <summary>Tiempo mínimo entre dos relecturas del programa provocadas por símbolos que no se encuentran.</summary>
    internal static TimeSpan MetadataRefreshInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Vuelve a leer el programa de la CPU para resolver símbolos. Devuelve false si no se ha hecho (se hizo hace menos
    /// de <see cref="MetadataRefreshInterval"/> y no se fuerza).
    /// </summary>
    private async Task<bool> RefreshMetadataAsync(ISymbolicClient client, bool force, CancellationToken cancellationToken)
    {
        var now = TimeProvider.GetUtcNow();
        if (!force && _metadataRefreshed is { } last && now - last < MetadataRefreshInterval)
            return false;
        await client.InvalidateMetadataAsync(cancellationToken);
        _metadataRefreshed = now;
        return true;
    }

    private async Task<BrowseResult> BrowseCoreAsync(BrowseQuery query, CancellationToken cancellationToken)
    {
        if (query.Refresh)
        {
            // «Releer»: el programa puede haber cambiado. Los símbolos que no se encontraron se vuelven a buscar.
            await RefreshMetadataAsync(Client, force: true, cancellationToken);
            foreach (var cache in new[] { _resolved, _resolvedForWrite })
            {
                foreach (var missing in cache.Where(e => e.Value.Tag is null).Select(e => e.Key).ToArray())
                    cache.Remove(missing);
            }
        }
        if (_symbols is null || query.Refresh)
        {
            var symbols = await Client.BrowseAsync(cancellationToken);
            _symbols = symbols;
            _symbolCount = symbols.Sum(s => 1 + ElementCount(s.Dimensions));
            Logger.LogInformation("{Device}: {Count} símbolos explorados", DeviceName, symbols.Count);
        }

        var filter = query.Filter?.Trim();
        var items = new List<BrowseItem>(Math.Min(query.MaxResults, 1024));
        var matches = 0;
        foreach (var s in _symbols)
        {
            var dataType = PlcTagValueMapper.DataTypeOf(s.Softdatatype);
            var nativeType = PlcTagValueMapper.NativeTypeName(s.Softdatatype);
            // Un array se ofrece entero (con su tipo real) y elemento a elemento.
            if (s.Dimensions is not { Count: > 0 } dimensions)
            {
                Add(s.Name, dataType, nativeType);
                continue;
            }
            Add(s.Name, dataType, $"{ArrayTypeName(dimensions)} of {nativeType}");
            foreach (var index in ElementIndices(dimensions))
                Add(s.Name + index, dataType, nativeType);
        }
        return new BrowseResult(items, matches, _symbolCount);

        void Add(string address, TagDataType dataType, string nativeType)
        {
            if (!string.IsNullOrEmpty(filter) && address.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                return;
            matches++;
            if (items.Count < query.MaxResults)
                items.Add(new BrowseItem(address, dataType, nativeType));
        }
    }

    private static int ElementCount(IReadOnlyList<ArrayDimension>? dimensions) =>
        dimensions is { Count: > 0 } ? (int)Math.Min(int.MaxValue, dimensions.Aggregate(1L, (n, d) => n * d.Count)) : 0;

    // Array[0..9] o Array[1..2, 0..3], como en TIA Portal.
    private static string ArrayTypeName(IReadOnlyList<ArrayDimension> dimensions) =>
        "Array[" + string.Join(", ", dimensions.Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.LowerBound}..{d.LowerBound + d.Count - 1}"))) + "]";

    // Índices de todos los elementos, el último índice el que más rápido varía: [1,0], [1,1]...
    private static IEnumerable<string> ElementIndices(IReadOnlyList<ArrayDimension> dimensions)
    {
        var current = dimensions.Select(d => (long)d.LowerBound).ToArray();
        var total = ElementCount(dimensions);
        for (var n = 0; n < total; n++)
        {
            yield return "[" + string.Join(",", current.Select(i => i.ToString(CultureInfo.InvariantCulture))) + "]";
            for (var k = current.Length - 1; k >= 0; k--)
            {
                if (++current[k] < dimensions[k].LowerBound + dimensions[k].Count)
                    break;
                current[k] = dimensions[k].LowerBound;
            }
        }
    }
}
