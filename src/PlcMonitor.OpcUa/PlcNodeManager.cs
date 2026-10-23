using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Server;
using Opc.Ua.Server.Historian;
using PlcMonitor.Abstractions;
using PlcMonitor.Engine;
using PlcMonitor.Engine.Configuration;
using PlcMonitor.Historian;

namespace PlcMonitor.OpcUa;

/// <summary>
/// Espacio de direcciones con los tags publicados del motor (doble llave: el dispositivo y el tag), en
/// <c>Objects/PLC-Monitor/&lt;dispositivo&gt;/[carpetas]/&lt;tag&gt;</c>
/// (NodeId <c>ns=…;s=&lt;dispositivo&gt;/&lt;tag&gt;</c>) y el estado de cada dispositivo en <c>_Estado</c>. Sigue los cambios
/// del motor en caliente (valores, estado, altas, bajas y reconfiguraciones) con una única tarea que lo serializa todo.
/// </summary>
internal sealed class PlcNodeManager : AsyncCustomNodeManager
{
    public const string NamespaceUri = "urn:plc-monitor:tags";

    private readonly TagEngine _engine;
    private readonly HistorianService? _historian;
    private readonly Func<OpcUaSettings> _settings;
    private readonly ILogger _logger;
    private readonly PlcHistorianProvider _history;
    private readonly CancellationTokenSource _cts = new();
    // Estructura del espacio de direcciones: solo la tocan la tarea de eventos y RefreshAccessAsync, de una en una.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DeviceNode> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<NodeId, TagNode> _tags = new();
    private FolderState _root = null!;
    private Task _events = Task.CompletedTask;

    public PlcNodeManager(IServerInternal server, ApplicationConfiguration configuration, TagEngine engine, HistorianService? historian,
        Func<OpcUaSettings> settings, ILogger logger)
        : base(server, configuration, NamespaceUri)
    {
        _engine = engine;
        _historian = historian;
        _settings = settings;
        _logger = logger;
        _history = new PlcHistorianProvider(this, historian);
    }

    /// <summary>El tag de una variable (para el histórico); null si no es un tag.</summary>
    internal TagNode? FindTag(NodeId nodeId) => _tags.GetValueOrDefault(nodeId);

    public override async ValueTask CreateAddressSpaceAsync(IDictionary<NodeId, IList<IReference>> externalReferences, CancellationToken cancellationToken = default)
    {
        _root = new FolderState(null)
        {
            NodeId = new NodeId("PLC-Monitor", NamespaceIndex),
            BrowseName = new QualifiedName("PLC-Monitor", NamespaceIndex),
            DisplayName = LocalizedText.From("PLC-Monitor"),
            TypeDefinitionId = ObjectTypeIds.FolderType,
        };
        _root.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
        AddExternalReference(ObjectIds.ObjectsFolder, ReferenceTypeIds.Organizes, false, _root.NodeId, externalReferences);

        // La suscripción se toma antes de construir: lo que cambie mientras tanto llega después como evento.
        var subscription = _engine.Subscribe();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var tags = subscription.InitialTags.ToLookup(t => t.Definition.Key.Device, StringComparer.OrdinalIgnoreCase);
            foreach (var status in subscription.InitialDevices.Where(d => d.OpcUaPublished))
            {
                var device = CreateDevice(status);
                foreach (var tag in tags[status.Device].Where(t => t.Definition.IsOpcUaPublished))
                    CreateTag(device, tag);
            }
            await AddPredefinedNodeAsync(SystemContext, _root, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
        _events = Task.Run(() => ListenAsync(subscription, _cts.Token), CancellationToken.None);
    }

    protected override IHistorianProvider? GetHistorianProvider(NodeState node) =>
        node.NodeId.NamespaceIndex == NamespaceIndex && _tags.ContainsKey(node.NodeId) ? _history : null;

    /// <summary>Vuelve a calcular qué tags se pueden escribir (cambió la tercera llave o los usuarios).</summary>
    public async Task RefreshAccessAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var device in _devices.Values)
            {
                ApplyStatus(device, device.Status);
                foreach (var tag in device.Tags.Values)
                    ApplyAttributes(tag);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------- Eventos del motor ----------------

    private async Task ListenAsync(EngineSubscription first, CancellationToken token)
    {
        var subscription = first;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await foreach (var engineEvent in subscription.Events.ReadAllAsync(token))
                    await ApplyAsync(engineEvent, token);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (SubscriberOverflowException ex)
            {
                // Se han perdido cambios: se vuelve a suscribir y se reconstruye desde la foto nueva.
                _logger.LogWarning("OPC UA: {Message}. Se vuelve a leer el estado del motor.", ex.Message);
                subscription.Dispose();
                subscription = _engine.Subscribe();
                await ResyncAsync(subscription, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "OPC UA: error al aplicar un cambio del motor; se vuelve a leer el estado.");
                subscription.Dispose();
                subscription = _engine.Subscribe();
                await ResyncAsync(subscription, token);
            }
        }
        subscription.Dispose();
    }

    private async Task ApplyAsync(EngineEvent engineEvent, CancellationToken token)
    {
        switch (engineEvent)
        {
            case TagValuesChanged changed:
                foreach (var update in changed.Updates)
                {
                    if (_tags.TryGetValue(TagNodeId(update.Key), out var tag))
                        ApplyValue(tag.Variable, update.Value);
                }
                break;
            // Un cambio de la llave de publicación del dispositivo llega como reconfiguración (DeviceConfigured).
            case DeviceStatusChanged status:
                await _gate.WaitAsync(token);
                try
                {
                    if (_devices.TryGetValue(status.Status.Device, out var device))
                    {
                        var writesChanged = device.Status.WritesEnabled != status.Status.WritesEnabled;
                        ApplyStatus(device, status.Status);
                        if (writesChanged)
                        {
                            foreach (var tag in device.Tags.Values)
                                ApplyAttributes(tag);
                        }
                    }
                }
                finally
                {
                    _gate.Release();
                }
                break;
            case DeviceConfigured configured:
                await _gate.WaitAsync(token);
                try
                {
                    await SyncDeviceAsync(configured.Status, configured.Tags, token);
                }
                finally
                {
                    _gate.Release();
                }
                break;
            case DeviceRemoved removed:
                await _gate.WaitAsync(token);
                try
                {
                    await RemoveDeviceAsync(removed.Device, token);
                }
                finally
                {
                    _gate.Release();
                }
                break;
        }
    }

    private async Task ResyncAsync(EngineSubscription subscription, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var tags = subscription.InitialTags.ToLookup(t => t.Definition.Key.Device, StringComparer.OrdinalIgnoreCase);
            var present = subscription.InitialDevices.Where(d => d.OpcUaPublished).Select(d => d.Device).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var gone in _devices.Keys.Where(d => !present.Contains(d)).ToList())
                await RemoveDeviceAsync(gone, token);
            foreach (var status in subscription.InitialDevices.Where(d => d.OpcUaPublished))
                await SyncDeviceAsync(status, [.. tags[status.Device]], token);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Alta o reconfiguración de un dispositivo: los tags que siguen igual conservan su nodo (y las suscripciones de los
    /// clientes); los que cambian de tipo o de carpeta se vuelven a crear; los que ya no están (o ya no se publican) se
    /// quitan. Un dispositivo que deja de publicarse desaparece entero.
    /// </summary>
    private async Task SyncDeviceAsync(DeviceStatus status, IReadOnlyList<TagSnapshot> allTags, CancellationToken token)
    {
        if (!status.OpcUaPublished)
        {
            await RemoveDeviceAsync(status.Device, token);
            return;
        }
        var tags = allTags.Where(t => t.Definition.IsOpcUaPublished).ToList();
        if (!_devices.TryGetValue(status.Device, out var device))
        {
            device = CreateDevice(status);
            foreach (var tag in tags)
                CreateTag(device, tag);
            await AddPredefinedNodeAsync(SystemContext, device.Folder, token);
            return;
        }

        ApplyStatus(device, status);
        var wanted = tags.ToDictionary(t => t.Definition.Key.Tag, StringComparer.OrdinalIgnoreCase);
        foreach (var existing in device.Tags.Values.ToList())
        {
            if (!wanted.TryGetValue(existing.Key.Tag, out var next) || MustRecreate(existing, next))
                await RemoveTagAsync(device, existing, token);
        }
        foreach (var snapshot in tags)
        {
            if (device.Tags.TryGetValue(snapshot.Definition.Key.Tag, out var existing))
            {
                existing.Definition = snapshot.Definition;
                ApplyAttributes(existing);
                ApplyValue(existing.Variable, snapshot.Value);
                continue;
            }
            var added = CreateTag(device, snapshot);
            await AddPredefinedNodeAsync(SystemContext, added.NewRoot, token);
        }
        await RemoveEmptyFoldersAsync(device, token);
    }

    private static bool MustRecreate(TagNode existing, TagSnapshot next) =>
        existing.Variable.DataType != DataTypeOf(next) || !string.Equals(existing.Definition.Folder ?? "", next.Definition.Folder ?? "", StringComparison.Ordinal);

    private async Task RemoveDeviceAsync(string name, CancellationToken token)
    {
        if (!_devices.Remove(name, out var device))
            return;
        foreach (var tag in device.Tags.Values)
            _tags.TryRemove(tag.Variable.NodeId, out _);
        await DeleteNodeAsync(SystemContext, device.Folder.NodeId, token);
        _root.RemoveChild(device.Folder);
    }

    private async Task RemoveTagAsync(DeviceNode device, TagNode tag, CancellationToken token)
    {
        device.Tags.Remove(tag.Key.Tag);
        _tags.TryRemove(tag.Variable.NodeId, out _);
        var parent = tag.Variable.Parent;
        await DeleteNodeAsync(SystemContext, tag.Variable.NodeId, token);
        parent?.RemoveChild(tag.Variable);
    }

    /// <summary>Quita las carpetas que se han quedado vacías (de la más profunda a la más alta).</summary>
    private async Task RemoveEmptyFoldersAsync(DeviceNode device, CancellationToken token)
    {
        foreach (var (path, folder) in device.Folders.OrderByDescending(f => f.Key.Length).ToList())
        {
            var children = new List<BaseInstanceState>();
            folder.GetChildren(SystemContext, children);
            if (children.Count > 0)
                continue;
            device.Folders.Remove(path);
            var parent = folder.Parent;
            await DeleteNodeAsync(SystemContext, folder.NodeId, token);
            parent?.RemoveChild(folder);
        }
    }

    // ---------------- Construcción de nodos ----------------

    private DeviceNode CreateDevice(DeviceStatus status)
    {
        var name = status.Device;
        var folder = Folder(_root, $"dispositivo:{name}", name);
        var state = Folder(folder, $"estado:{name}", "_Estado");
        var device = new DeviceNode(name, folder, status)
        {
            Connected = StatusVariable(state, name, "Conectado", DataTypeIds.Boolean, "El agente está conectado al dispositivo."),
            State = StatusVariable(state, name, "Estado", DataTypeIds.String, "Desconectado, Conectando, Conectado o Error."),
            Detail = StatusVariable(state, name, "Detalle", DataTypeIds.String, "Motivo del último error o cambio de estado."),
            Enabled = StatusVariable(state, name, "Activo", DataTypeIds.Boolean, "El agente vigila el dispositivo."),
            WritesEnabled = StatusVariable(state, name, "EscrituraPermitida", DataTypeIds.Boolean,
                "Escritura permitida en el dispositivo (WritesEnabled) y en el servidor OPC UA (tercera llave)."),
        };
        _devices[name] = device;
        ApplyStatus(device, status);
        return device;
    }

    private FolderState Folder(NodeState parent, string id, string name)
    {
        var folder = new FolderState(parent)
        {
            NodeId = new NodeId(id, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = LocalizedText.From(name),
            TypeDefinitionId = ObjectTypeIds.FolderType,
            ReferenceTypeId = ReferenceTypeIds.Organizes,
        };
        parent.AddChild(folder);
        return folder;
    }

    private BaseDataVariableState StatusVariable(FolderState parent, string device, string name, NodeId dataType, string description)
    {
        var variable = new BaseDataVariableState(parent)
        {
            NodeId = new NodeId($"estado:{device}/{name}", NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = LocalizedText.From(name),
            Description = LocalizedText.From(description),
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            ReferenceTypeId = ReferenceTypeIds.HasComponent,
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
        };
        parent.AddChild(variable);
        return variable;
    }

    /// <summary>Crea el nodo del tag (y las carpetas que falten). <see cref="TagNode.NewRoot"/> es lo que hay que registrar.</summary>
    private TagNode CreateTag(DeviceNode device, TagSnapshot snapshot)
    {
        var definition = snapshot.Definition;
        var (parent, newRoot) = FolderFor(device, definition.Folder);
        var variable = new BaseDataVariableState(parent)
        {
            NodeId = TagNodeId(definition.Key),
            BrowseName = new QualifiedName(definition.Key.Tag, NamespaceIndex),
            DisplayName = LocalizedText.From(definition.Key.Tag),
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            ReferenceTypeId = ReferenceTypeIds.Organizes,
            DataType = DataTypeOf(snapshot),
            ValueRank = ValueRanks.Scalar,
            MinimumSamplingInterval = definition.Interval.TotalMilliseconds,
        };
        var tag = new TagNode(definition, variable, device) { NewRoot = newRoot ?? variable };
        variable.OnSimpleWriteValueAsync = (context, _, value, token) => WriteAsync(tag, context, value, token);
        variable.OnReadUserAccessLevel = (context, _, ref level) =>
        {
            // Por usuario: sin rol de escritura, el nivel de acceso del usuario no incluye escribir.
            if (!CanWrite(UserOf(context)))
                level = (byte)(level & ~AccessLevels.CurrentWrite);
            return ServiceResult.Good;
        };
        parent.AddChild(variable);
        device.Tags[definition.Key.Tag] = tag;
        _tags[variable.NodeId] = tag;
        ApplyAttributes(tag);
        ApplyValue(variable, snapshot.Value);
        return tag;
    }

    /// <summary>Carpeta del tag (grupo de visualización, con «/» para anidar); devuelve también la primera carpeta nueva.</summary>
    private (NodeState Parent, NodeState? NewRoot) FolderFor(DeviceNode device, string? folderPath)
    {
        NodeState parent = device.Folder;
        NodeState? newRoot = null;
        if (string.IsNullOrWhiteSpace(folderPath))
            return (parent, null);
        var path = "";
        foreach (var part in folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            path = path.Length == 0 ? part : $"{path}/{part}";
            if (!device.Folders.TryGetValue(path, out var folder))
            {
                folder = Folder(parent, $"carpeta:{device.Name}/{path}", part);
                device.Folders[path] = folder;
                newRoot ??= folder;
            }
            parent = folder;
        }
        return (parent, newRoot);
    }

    private NodeId TagNodeId(TagKey key) => new($"{key.Device}/{key.Tag}", NamespaceIndex);

    private static NodeId DataTypeOf(TagSnapshot snapshot) =>
        OpcUaMapping.DataTypeOf(snapshot.Definition.DataType ?? (snapshot.Value.DataType == TagDataType.Unknown ? null : snapshot.Value.DataType),
            snapshot.Definition.Scaling is not null);

    // ---------------- Atributos y valores ----------------

    /// <summary>Acceso, histórico y descripción del tag según su definición, el dispositivo y la tercera llave.</summary>
    private void ApplyAttributes(TagNode tag)
    {
        var definition = tag.Definition;
        var historized = definition.IsHistorized && _historian is not null && _historian.GetStats().Enabled;
        var writable = definition.Writable && tag.Device.Status.WritesEnabled && _settings().AllowWrites;
        var access = (byte)((writable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead) | (historized ? AccessLevels.HistoryRead : 0));
        var variable = tag.Variable;
        variable.AccessLevel = access;
        variable.UserAccessLevel = access;
        variable.Historizing = historized;
        var parts = new List<string> { $"Dirección: {definition.Address}" };
        if (!string.IsNullOrWhiteSpace(definition.Unit))
            parts.Add($"Unidad: {definition.Unit}");
        if (definition.Scaling is not null)
            parts.Add("Valor escalado a unidades de ingeniería");
        if (!string.IsNullOrWhiteSpace(definition.Transform))
            parts.Add($"Transformación: {definition.Transform}");
        variable.Description = LocalizedText.From(string.Join(" · ", parts));
        variable.ClearChangeMasks(SystemContext, false);
    }

    private void ApplyValue(BaseDataVariableState variable, TagValue value)
    {
        variable.Value = value.Quality.IsBad() ? Variant.Null : OpcUaMapping.ToVariant(value.Value, variable.DataType);
        variable.StatusCode = OpcUaMapping.ToStatus(value.Quality);
        variable.Timestamp = (DateTimeUtc)value.Timestamp.UtcDateTime;
        variable.ClearChangeMasks(SystemContext, false);
    }

    private void ApplyStatus(DeviceNode device, DeviceStatus status)
    {
        device.Status = status;
        var timestamp = (DateTimeUtc)status.Timestamp.UtcDateTime;
        Set(device.Connected, Variant.From(status.State == ConnectionState.Connected), timestamp);
        Set(device.State, Variant.From(status.State switch
        {
            ConnectionState.Connected => "Conectado",
            ConnectionState.Connecting => "Conectando",
            ConnectionState.Faulted => "Error",
            _ => "Desconectado",
        }), timestamp);
        Set(device.Detail, Variant.From(status.Detail ?? ""), timestamp);
        Set(device.Enabled, Variant.From(status.Enabled), timestamp);
        Set(device.WritesEnabled, Variant.From(status.WritesEnabled && _settings().AllowWrites), timestamp);
    }

    private void Set(BaseDataVariableState variable, Variant value, DateTimeUtc timestamp)
    {
        variable.Value = value;
        variable.StatusCode = StatusCodes.Good;
        variable.Timestamp = timestamp;
        variable.ClearChangeMasks(SystemContext, false);
    }

    // ---------------- Escritura ----------------

    private static string? UserOf(ISystemContext context) => (context as ISessionSystemContext)?.UserIdentity?.DisplayName;

    private bool CanWrite(string? user)
    {
        var settings = _settings();
        return settings.AllowWrites && user is not null
            && settings.Users.Any(u => u.Role == OpcUaRole.Write && string.Equals(u.Name, user, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Escritura de un cliente OPC UA: hace falta la tercera llave y un usuario con rol de escritura; las otras dos llaves
    /// (dispositivo y tag) las comprueba el motor, que además la deja en la auditoría.
    /// </summary>
    private async ValueTask<AttributeWriteResult> WriteAsync(TagNode tag, ISystemContext context, Variant value, CancellationToken token)
    {
        var user = UserOf(context);
        if (!_settings().AllowWrites)
            return Fail(StatusCodes.BadNotWritable, "El servidor OPC UA no permite escribir (tercera llave desactivada en el agente).");
        if (!CanWrite(user))
            return Fail(StatusCodes.BadUserAccessDenied, $"El usuario OPC UA '{user}' no tiene permiso de escritura.");
        var key = tag.Key;
        try
        {
            var result = await _engine.WriteAsync(key.Device, key.Tag, OpcUaMapping.ToWriteText(value), $"{user} (OPC UA)", token);
            return result.Success
                ? new AttributeWriteResult(ServiceResult.Good)
                : Fail(OpcUaMapping.ToStatus(result.Quality), result.ErrorDetail ?? result.Quality.ToString());
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException)
        {
            return Fail(StatusCodes.BadInvalidArgument, ex.Message);
        }

        static AttributeWriteResult Fail(StatusCode code, string message) => new(new ServiceResult(code, LocalizedText.From(message)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            try
            {
                _events.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // La tarea de eventos solo termina con errores ya registrados.
            }
            _cts.Dispose();
            _gate.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Nodos de un dispositivo.</summary>
    internal sealed class DeviceNode(string name, FolderState folder, DeviceStatus status)
    {
        public string Name { get; } = name;

        public FolderState Folder { get; } = folder;

        public DeviceStatus Status { get; set; } = status;

        public Dictionary<string, FolderState> Folders { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, TagNode> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

        public required BaseDataVariableState Connected { get; init; }

        public required BaseDataVariableState State { get; init; }

        public required BaseDataVariableState Detail { get; init; }

        public required BaseDataVariableState Enabled { get; init; }

        public required BaseDataVariableState WritesEnabled { get; init; }
    }

    /// <summary>Nodo de un tag con su definición actual.</summary>
    internal sealed class TagNode(TagDefinition definition, BaseDataVariableState variable, DeviceNode device)
    {
        public TagDefinition Definition { get; set; } = definition;

        public TagKey Key => Definition.Key;

        public BaseDataVariableState Variable { get; } = variable;

        public DeviceNode Device { get; } = device;

        /// <summary>Nodo más alto creado con el tag (su carpeta nueva o él mismo): el que se registra en caliente.</summary>
        public required NodeState NewRoot { get; init; }
    }
}
