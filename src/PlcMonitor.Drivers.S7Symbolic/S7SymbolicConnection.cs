using Microsoft.Extensions.Logging;
using PlcMonitor.Abstractions;
using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;

namespace PlcMonitor.Drivers.S7Symbolic;

/// <summary>
/// Conexión simbólica S7CommPlus (TLS + legitimación) con S7-1200/1500.
/// Las direcciones son nombres simbólicos: <c>"MiDB".Motor.Velocidad</c>, <c>MiDB.Array[3]</c> o el nombre devuelto por la exploración.
/// </summary>
internal sealed class S7SymbolicConnection : DeviceConnectionBase, IBrowsableConnection
{
    private readonly S7SymbolicSettings _settings;
    private readonly Func<ISymbolicClient> _clientFactory;

    // Tags ya resueltos (o el motivo por el que no se pudieron resolver). Se vacía al reconectar.
    private readonly Dictionary<string, (PlcTag? Tag, string? Error)> _resolved = new(StringComparer.Ordinal);
    private ISymbolicClient? _client;
    private IReadOnlyList<SymbolEntry>? _symbols;

    public S7SymbolicConnection(
        string deviceName, S7SymbolicSettings settings, ILogger logger, Func<ISymbolicClient>? clientFactory = null, TimeProvider? timeProvider = null)
        : base(deviceName, logger, timeProvider)
    {
        _settings = settings;
        _clientFactory = clientFactory ?? (() => new S7CommPlusClient());
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
        return RunExclusiveAsync(ct => Task.Run(() => Browse(query), ct), cancellationToken);
    }

    protected override async Task OpenCoreAsync(CancellationToken cancellationToken)
    {
        S7Client.KeyLogDirectory = _settings.TlsKeyLogDirectory;
        var client = _clientFactory();
        var result = await Task.Run(() => client.Connect(_settings.Host, _settings.Password, _settings.Username, _settings.TimeoutMs), cancellationToken);
        if (result != 0)
        {
            client.Dispose();
            throw new S7CommPlusException("Conexión", result);
        }
        _client = client;
        _resolved.Clear();
    }

    protected override Task CloseCoreAsync()
    {
        var client = _client;
        _client = null;
        _resolved.Clear();
        client?.Dispose();
        return Task.CompletedTask;
    }

    protected override Task<IReadOnlyList<TagValue>> ReadCoreAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<TagValue>>(() => Read(addresses), cancellationToken);

    protected override Task<IReadOnlyList<WriteResult>> WriteCoreAsync(IReadOnlyList<WriteRequest> requests, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<WriteResult>>(() => Write(requests), cancellationToken);

    protected override Task<IReadOnlyDictionary<string, string>> GetDeviceInfoCoreAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>
        {
            ["Host"] = _settings.Host,
            ["Protocol"] = "S7CommPlus (TLS 1.3)",
            ["Legitimation"] = string.IsNullOrEmpty(_settings.Password) ? "sin contraseña" : "contraseña",
            ["SymbolsLoaded"] = _symbols is null ? "no" : _symbols.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

    protected override bool IsConnectionLost(Exception exception) =>
        base.IsConnectionLost(exception) || exception is S7CommPlusException { IsCommunicationError: true };

    private ISymbolicClient Client => _client ?? throw new IOException("Sesión S7CommPlus cerrada.");

    private TagValue[] Read(IReadOnlyList<string> addresses)
    {
        var client = Client;
        var results = new TagValue[addresses.Count];
        var resolved = new PlcTag?[addresses.Count];
        // Si la misma dirección aparece varias veces, el tag se lee una sola vez.
        var distinct = new HashSet<PlcTag>(ReferenceEqualityComparer.Instance);

        for (var i = 0; i < addresses.Count; i++)
        {
            var (tag, error) = Resolve(client, addresses[i]);
            if (tag is null)
                results[i] = TagValue.Bad(QualityCode.BadAddressInvalid, TimeProvider.GetUtcNow(), error);
            else
            {
                distinct.Add(tag);
                resolved[i] = tag;
            }
        }

        if (distinct.Count > 0)
        {
            var code = client.ReadTags(distinct.ToList());
            if (code != 0)
                throw new S7CommPlusException("Lectura", code);
        }

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

    private WriteResult[] Write(IReadOnlyList<WriteRequest> requests)
    {
        var client = Client;
        var results = new WriteResult?[requests.Count];
        var tags = new List<PlcTag>();
        var tagIndexes = new List<int>();

        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            var (template, error) = Resolve(client, request.Address);
            if (template is null)
            {
                results[i] = new WriteResult(request.Address, QualityCode.BadAddressInvalid, error);
                continue;
            }
            // Instancia propia para no alterar el tag usado en las lecturas.
            var tag = PlcTags.TagFactory(template.Name, template.Address, template.Datatype, template.GetType().Name.EndsWith("Array", StringComparison.Ordinal));
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
            var code = client.WriteTags(tags);
            if (code != 0)
                throw new S7CommPlusException("Escritura", code);
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

    private (PlcTag? Tag, string? Error) Resolve(ISymbolicClient client, string address)
    {
        if (_resolved.TryGetValue(address, out var cached))
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
                var tag = client.ResolveTag(address.Trim());
                entry = tag is null ? (null, $"El símbolo '{address}' no existe en la CPU.") : (tag, null);
            }
#pragma warning disable CA1031 // La librería lanza Exception genérica ante símbolos mal formados o índices fuera de rango.
            catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
            {
                entry = (null, $"Símbolo '{address}' no válido: {ex.Message}");
            }
        }
        _resolved[address] = entry;
        return entry;
    }

    private BrowseResult Browse(BrowseQuery query)
    {
        if (_symbols is null || query.Refresh)
        {
            var code = Client.Browse(out var symbols);
            if (code != 0)
                throw new S7CommPlusException("Exploración", code);
            _symbols = symbols;
            Logger.LogInformation("{Device}: {Count} símbolos explorados", DeviceName, symbols.Count);
        }

        var filter = query.Filter?.Trim();
        var items = new List<BrowseItem>(Math.Min(query.MaxResults, 1024));
        var matches = 0;
        foreach (var s in _symbols)
        {
            if (!string.IsNullOrEmpty(filter) && s.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            matches++;
            if (items.Count < query.MaxResults)
                items.Add(new BrowseItem(s.Name, PlcTagValueMapper.DataTypeOf(s.Softdatatype), PlcTagValueMapper.NativeTypeName(s.Softdatatype)));
        }
        return new BrowseResult(items, matches, _symbols.Count);
    }
}
