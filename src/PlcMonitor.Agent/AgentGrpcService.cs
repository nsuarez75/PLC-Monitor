using System.Reflection;
using System.Runtime.InteropServices;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using PlcMonitor.Abstractions;
using PlcMonitor.Core;
using PlcMonitor.Engine;
using PlcMonitor.Engine.Configuration;
using Api = PlcMonitor.Agent.Contracts;

namespace PlcMonitor.Agent;

/// <summary>Implementación de la API gRPC sobre el motor.</summary>
public sealed class AgentGrpcService(EngineHost host, DriverRegistry registry, IHostApplicationLifetime lifetime, ILogger<AgentGrpcService> logger, ILoggerFactory loggerFactory)
    : Api.AgentService.AgentServiceBase
{
    /// <summary>Cambios de configuración y de histórico: al mismo log de auditoría que las escrituras.</summary>
    private readonly ILogger _audit = loggerFactory.CreateLogger("PlcMonitor.Audit");

    private TagEngine Engine => host.Engine;

    public override Task<Api.AgentInfo> GetInfo(Empty request, ServerCallContext context)
    {
        var info = new Api.AgentInfo
        {
            Version = typeof(AgentGrpcService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "",
            Machine = Environment.MachineName,
            Os = RuntimeInformation.OSDescription,
            Started = Timestamp.FromDateTimeOffset(host.Started),
            ConfigPath = host.ConfigPath,
            Access = AgentCaller.From(context).Access == AgentAccess.Full ? Api.AccessLevel.Full : Api.AccessLevel.Read,
        };
        var stats = host.Historian.GetStats();
        info.Historian = new Api.HistorianInfo
        {
            Enabled = stats.Enabled,
            Path = stats.Path,
            RetentionDays = stats.RetentionDays,
            SizeBytes = stats.SizeBytes,
            Files = stats.Files,
            HistorizedTags = stats.HistorizedTags,
        };
        info.Drivers.AddRange(registry.Factories.Select(f => new Api.Driver
        {
            Id = f.TypeId,
            Name = f.DisplayName,
            DefaultSettingsJson = f.DefaultSettings().GetRawText(),
            CanBrowse = f.Capabilities.HasFlag(DriverCapabilities.Browse),
            CanWrite = f.Capabilities.HasFlag(DriverCapabilities.Write),
        }));
        return Task.FromResult(info);
    }

    public override Task<Api.DeviceList> ListDevices(Empty request, ServerCallContext context)
    {
        var list = new Api.DeviceList();
        list.Devices.AddRange(Engine.GetDevices().Select(ToProto));
        return Task.FromResult(list);
    }

    public override async Task<Api.Device> SetDeviceEnabled(Api.SetDeviceEnabledRequest request, ServerCallContext context)
    {
        var status = await Guard(() => Engine.SetDeviceEnabledAsync(request.Device, request.Enabled));
        if (request.Persist)
            await host.SaveConfigurationAsync(context.CancellationToken);
        _audit.LogInformation("Dispositivo {Device} {State} por {User}", status.Device, request.Enabled ? "activado" : "desactivado", AgentCaller.Describe(context));
        return ToProto(status);
    }

    public override async Task<Api.DeviceInfo> GetDeviceInfo(Api.DeviceRequest request, ServerCallContext context)
    {
        var properties = await Guard(() => Engine.GetDeviceInfoAsync(request.Device, context.CancellationToken));
        var info = new Api.DeviceInfo();
        foreach (var (key, value) in properties)
            info.Properties[key] = value;
        return info;
    }

    public override Task<Api.TagList> ListTags(Api.ListTagsRequest request, ServerCallContext context)
    {
        var tags = Guard(() => Engine.GetTags(string.IsNullOrEmpty(request.Device) ? null : request.Device));
        var list = new Api.TagList();
        list.Tags.AddRange(tags.Select(ProtoMapper.ToProto));
        return Task.FromResult(list);
    }

    public override async Task<Api.ReadReply> Read(Api.ReadRequest request, ServerCallContext context)
    {
        if (request.Addresses.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Indica al menos una dirección."));
        var values = await Guard(() => Engine.ReadAsync(request.Device, request.Addresses, context.CancellationToken));
        var reply = new Api.ReadReply();
        reply.Values.AddRange(values.Select(ProtoMapper.ToProto));
        return reply;
    }

    public override async Task<Api.WriteReply> Write(Api.WriteRequest request, ServerCallContext context)
    {
        var user = AgentCaller.Describe(context, request.User);
        var result = await Guard(() => Engine.WriteAsync(request.Device, request.Tag, request.Value, user, context.CancellationToken));
        return ProtoMapper.ToProto(result);
    }

    public override async Task Subscribe(Api.SubscribeRequest request, IServerStreamWriter<Api.SubscribeEvent> responseStream, ServerCallContext context)
    {
        var devices = request.Devices.Count == 0 ? null : new HashSet<string>(request.Devices, StringComparer.OrdinalIgnoreCase);
        foreach (var device in request.Devices)
            Guard(() => Engine.GetDevice(device));
        bool Wanted(string device) => devices is null || devices.Contains(device);

        // La suscripción termina cuando el cliente la cierra o cuando el agente empieza a pararse (si no, el host esperaría
        // a que el cliente se fuera y la parada se alargaría hasta su tiempo máximo).
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
        var token = stopping.Token;
        using var subscription = Engine.Subscribe();
        var snapshot = new Api.Snapshot();
        var tagCounts = subscription.InitialTags.CountBy(t => t.Definition.Key.Device).ToDictionary(StringComparer.OrdinalIgnoreCase);
        snapshot.Devices.AddRange(subscription.InitialDevices.Where(d => Wanted(d.Device)).Select(d => ProtoMapper.ToProto(d, tagCounts.GetValueOrDefault(d.Device))));
        snapshot.Tags.AddRange(subscription.InitialTags.Where(t => Wanted(t.Definition.Key.Device)).Select(ProtoMapper.ToProto));
        snapshot.Transforms.AddRange(subscription.InitialTransforms.Select(ProtoMapper.ToProto));
        await responseStream.WriteAsync(new Api.SubscribeEvent { Snapshot = snapshot }, token);

        try
        {
            await foreach (var engineEvent in subscription.Events.ReadAllAsync(token))
            {
                switch (engineEvent)
                {
                    case TagValuesChanged changed:
                        var updates = new Api.TagUpdates();
                        updates.Updates.AddRange(changed.Updates.Where(u => Wanted(u.Key.Device)).Select(ProtoMapper.ToProto));
                        if (updates.Updates.Count > 0)
                            await responseStream.WriteAsync(new Api.SubscribeEvent { TagUpdates = updates }, token);
                        break;
                    case DeviceStatusChanged status when Wanted(status.Status.Device):
                        await responseStream.WriteAsync(new Api.SubscribeEvent { DeviceStatus = ToProto(status.Status) }, token);
                        break;
                    case DeviceConfigured configured when Wanted(configured.Status.Device):
                        var pb = new Api.DeviceConfigured { Device = ProtoMapper.ToProto(configured.Status, configured.Tags.Count) };
                        pb.Tags.AddRange(configured.Tags.Select(ProtoMapper.ToProto));
                        await responseStream.WriteAsync(new Api.SubscribeEvent { DeviceConfigured = pb }, token);
                        break;
                    case DeviceRemoved removed when Wanted(removed.Device):
                        await responseStream.WriteAsync(
                            new Api.SubscribeEvent { DeviceRemoved = new Api.DeviceRemoved { Device = removed.Device } }, token);
                        break;
                    case TransformsChanged transforms:
                        await responseStream.WriteAsync(new Api.SubscribeEvent { Transforms = ProtoMapper.ToProto(transforms.Transforms) }, token);
                        break;
                }
            }
        }
        catch (SubscriberOverflowException ex)
        {
            logger.LogWarning("Suscripción de {Peer} cerrada: {Message}", context.Peer, ex.Message);
            throw new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message));
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested || lifetime.ApplicationStopping.IsCancellationRequested)
        {
            // El cliente cerró la suscripción o el agente se está parando.
        }
    }

    public override async Task<Api.BrowseReply> Browse(Api.BrowseRequest request, ServerCallContext context)
    {
        var query = new BrowseQuery(
            string.IsNullOrWhiteSpace(request.Filter) ? null : request.Filter,
            request.MaxResults > 0 ? request.MaxResults : 100,
            request.Refresh);
        var result = await Guard(() => Engine.BrowseAsync(request.Device, query, context.CancellationToken));
        return ProtoMapper.ToProto(result);
    }

    public override Task<Api.Configuration> GetConfiguration(Empty request, ServerCallContext context)
    {
        var pb = new Api.Configuration();
        pb.Devices.AddRange(Engine.SnapshotConfiguration().Devices.Select(ToMaskedProto));
        return Task.FromResult(pb);
    }

    public override Task<Api.DeviceConfig> GetDeviceConfig(Api.DeviceRequest request, ServerCallContext context) =>
        Task.FromResult(ToMaskedProto(Guard(() => Engine.GetDeviceConfiguration(request.Device))));

    /// <summary>Las contraseñas no salen del agente: el cliente ve <see cref="ConfigSecrets.Masked"/>.</summary>
    private static Api.DeviceConfig ToMaskedProto(DeviceConfiguration device)
    {
        ConfigSecrets.Mask(device);
        return ProtoMapper.ToProto(device);
    }

    public override async Task<Api.Device> PutDevice(Api.PutDeviceRequest request, ServerCallContext context)
    {
        if (request.Device is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Falta el dispositivo."));
        var device = Guard(() => ProtoMapper.FromProto(request.Device));
        ConfigSecrets.Restore(device, FindConfiguration(string.IsNullOrWhiteSpace(request.OriginalName) ? device.Name : request.OriginalName));
        var status = await Guard(() => Engine.PutDeviceAsync(device, string.IsNullOrWhiteSpace(request.OriginalName) ? null : request.OriginalName));
        if (request.Persist)
            await host.SaveConfigurationAsync(context.CancellationToken);
        _audit.LogInformation("Configuración de {Device} cambiada por {User} (escrituras {Writes})", status.Device, AgentCaller.Describe(context),
            status.WritesEnabled ? "activadas" : "desactivadas");
        return ToProto(status);
    }

    public override async Task<Empty> DeleteDevice(Api.DeleteDeviceRequest request, ServerCallContext context)
    {
        await Guard(async () =>
        {
            await Engine.DeleteDeviceAsync(request.Device);
            return true;
        });
        if (request.Persist)
            await host.SaveConfigurationAsync(context.CancellationToken);
        _audit.LogInformation("Dispositivo {Device} dado de baja por {User}", request.Device, AgentCaller.Describe(context));
        return new Empty();
    }

    public override async Task<Api.HistoryReply> QueryHistory(Api.HistoryRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Device) || request.Tags.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Indica el dispositivo y al menos un tag."));
        var to = request.To?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        var from = request.From?.ToDateTimeOffset() ?? to.AddHours(-1);
        var query = new PlcMonitor.Historian.HistoryQuery(
            request.Tags.Select(t => new TagKey(request.Device, t)).ToArray(),
            from,
            to,
            request.IntervalMs > 0 ? TimeSpan.FromMilliseconds(request.IntervalMs) : null,
            (PlcMonitor.Historian.HistoryAggregate)(int)request.Aggregate,
            request.MaxPoints > 0 ? request.MaxPoints : 10_000);
        var series = await Guard(() => host.Historian.QueryAsync(query, context.CancellationToken));
        var reply = new Api.HistoryReply();
        reply.Series.AddRange(series.Select(ProtoMapper.ToProto));
        return reply;
    }

    public override Task<Api.TransformList> GetTransforms(Empty request, ServerCallContext context) =>
        Task.FromResult(ProtoMapper.ToProto(Engine.GetTransforms()));

    public override async Task<Api.TransformList> PutTransforms(Api.PutTransformsRequest request, ServerCallContext context)
    {
        var transforms = await Guard(() => Engine.SetTransformsAsync(request.Transforms.Select(ProtoMapper.FromProto).ToArray()));
        if (request.Persist)
            await host.SaveConfigurationAsync(context.CancellationToken);
        _audit.LogInformation("Transformaciones cambiadas por {User}", AgentCaller.Describe(context));
        return ProtoMapper.ToProto(transforms);
    }

    public override async Task<Api.RenameHistoryReply> RenameHistory(Api.RenameHistoryRequest request, ServerCallContext context)
    {
        var renames = request.Renames.Select(r => new PlcMonitor.Historian.HistoryRename(
            r.FromDevice.Trim(), string.IsNullOrWhiteSpace(r.FromTag) ? null : r.FromTag.Trim(),
            r.ToDevice.Trim(), string.IsNullOrWhiteSpace(r.ToTag) ? null : r.ToTag.Trim())).ToArray();
        var moved = await Guard(() => host.Historian.RenameAsync(renames, context.CancellationToken));
        _audit.LogInformation("Histórico renombrado por {User}: {Moved} tags", AgentCaller.Describe(context), moved);
        return new Api.RenameHistoryReply { Moved = moved };
    }

    public override async Task<Api.DeleteHistoryReply> DeleteHistory(Api.DeleteHistoryRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Device) || request.Tags.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Indica el dispositivo y al menos un tag."));
        if (request.From is null || request.To is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Indica el inicio y el final del intervalo que se borra."));
        var keys = request.Tags.Select(t => new TagKey(request.Device.Trim(), t.Trim())).ToArray();
        var deleted = await Guard(() => host.Historian.DeleteAsync(keys, request.From.ToDateTimeOffset(), request.To.ToDateTimeOffset(), context.CancellationToken));
        _audit.LogInformation("Histórico borrado por {User}: {Deleted} muestras de {Device}/{Tags}", AgentCaller.Describe(context), deleted, request.Device, string.Join(", ", request.Tags));
        return new Api.DeleteHistoryReply { Deleted = deleted };
    }

    private Api.Device ToProto(DeviceStatus status) => ProtoMapper.ToProto(status, Engine.GetTags(status.Device).Count);

    /// <summary>Traduce las excepciones del motor a códigos gRPC.</summary>
    private DeviceConfiguration? FindConfiguration(string name) =>
        Engine.SnapshotConfiguration().Devices.FirstOrDefault(d => string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (Translate(ex) is { } rpc)
        {
            throw rpc;
        }
    }

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (Translate(ex) is { } rpc)
        {
            throw rpc;
        }
    }

    private static RpcException? Translate(Exception ex) => ex switch
    {
        KeyNotFoundException => new RpcException(new Status(StatusCode.NotFound, ex.Message)),
        EngineConfigurationException config => new RpcException(new Status(StatusCode.InvalidArgument,
            config.Errors.Count > 0 ? string.Join(Environment.NewLine, config.Errors) : config.Message)),
        ArgumentException => new RpcException(new Status(StatusCode.InvalidArgument, ex.Message)),
        InvalidOperationException or NotSupportedException => new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message)),
        _ => null,
    };
}
