using System.Reflection;
using System.Runtime.InteropServices;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using PlcMonitor.Core;
using PlcMonitor.Engine;
using Api = PlcMonitor.Agent.Contracts;

namespace PlcMonitor.Agent;

/// <summary>Implementación de la API gRPC sobre el motor.</summary>
public sealed class AgentGrpcService(EngineHost host, DriverRegistry registry, ILogger<AgentGrpcService> logger) : Api.AgentService.AgentServiceBase
{
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
        };
        info.Drivers.AddRange(registry.Factories.Select(f => new Api.Driver { Id = f.TypeId, Name = f.DisplayName }));
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
        var user = string.IsNullOrWhiteSpace(request.User) ? $"anónimo@{context.Peer}" : $"{request.User}@{context.Peer}";
        var result = await Guard(() => Engine.WriteAsync(request.Device, request.Tag, request.Value, user, context.CancellationToken));
        return ProtoMapper.ToProto(result);
    }

    public override async Task Subscribe(Api.SubscribeRequest request, IServerStreamWriter<Api.SubscribeEvent> responseStream, ServerCallContext context)
    {
        var devices = request.Devices.Count == 0 ? null : new HashSet<string>(request.Devices, StringComparer.OrdinalIgnoreCase);
        foreach (var device in request.Devices)
            Guard(() => Engine.GetDevice(device));
        bool Wanted(string device) => devices is null || devices.Contains(device);

        using var subscription = Engine.Subscribe();
        var snapshot = new Api.Snapshot();
        var tagCounts = subscription.InitialTags.CountBy(t => t.Definition.Key.Device).ToDictionary(StringComparer.OrdinalIgnoreCase);
        snapshot.Devices.AddRange(subscription.InitialDevices.Where(d => Wanted(d.Device)).Select(d => ProtoMapper.ToProto(d, tagCounts.GetValueOrDefault(d.Device))));
        snapshot.Tags.AddRange(subscription.InitialTags.Where(t => Wanted(t.Definition.Key.Device)).Select(ProtoMapper.ToProto));
        await responseStream.WriteAsync(new Api.SubscribeEvent { Snapshot = snapshot }, context.CancellationToken);

        try
        {
            await foreach (var engineEvent in subscription.Events.ReadAllAsync(context.CancellationToken))
            {
                switch (engineEvent)
                {
                    case TagValuesChanged changed:
                        var updates = new Api.TagUpdates();
                        updates.Updates.AddRange(changed.Updates.Where(u => Wanted(u.Key.Device)).Select(ProtoMapper.ToProto));
                        if (updates.Updates.Count > 0)
                            await responseStream.WriteAsync(new Api.SubscribeEvent { TagUpdates = updates }, context.CancellationToken);
                        break;
                    case DeviceStatusChanged status when Wanted(status.Status.Device):
                        await responseStream.WriteAsync(new Api.SubscribeEvent { DeviceStatus = ToProto(status.Status) }, context.CancellationToken);
                        break;
                }
            }
        }
        catch (SubscriberOverflowException ex)
        {
            logger.LogWarning("Suscripción de {Peer} cerrada: {Message}", context.Peer, ex.Message);
            throw new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message));
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // El cliente cerró la suscripción.
        }
    }

    private Api.Device ToProto(DeviceStatus status) => ProtoMapper.ToProto(status, Engine.GetTags(status.Device).Count);

    /// <summary>Traduce las excepciones del motor a códigos gRPC.</summary>
    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (KeyNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
    }

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (KeyNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
    }
}
