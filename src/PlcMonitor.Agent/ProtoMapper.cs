using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using PlcMonitor.Core;
using PlcMonitor.Engine;
using PlcMonitor.Engine.Configuration;
using Abs = PlcMonitor.Abstractions;
using Api = PlcMonitor.Agent.Contracts;

namespace PlcMonitor.Agent;

/// <summary>Conversión entre el modelo del motor y los mensajes gRPC.</summary>
internal static class ProtoMapper
{
    // Los enums del .proto tienen los mismos nombres que los del motor (protoc quita el prefijo y pasa a PascalCase).
    public static Api.Quality ToProto(Abs.QualityCode quality) => System.Enum.Parse<Api.Quality>(quality.ToString());

    public static Api.DataType ToProto(Abs.TagDataType type) => System.Enum.Parse<Api.DataType>(type.ToString());

    public static Api.ConnectionState ToProto(Abs.ConnectionState state) => System.Enum.Parse<Api.ConnectionState>(state.ToString());

    public static Api.Device ToProto(DeviceStatus status, int tagCount)
    {
        var device = new Api.Device
        {
            Name = status.Device,
            Driver = status.Driver,
            Enabled = status.Enabled,
            WritesEnabled = status.WritesEnabled,
            State = ToProto(status.State),
            Detail = status.Detail ?? "",
            Since = Timestamp.FromDateTimeOffset(status.Timestamp),
            TagCount = tagCount,
        };
        if (status.NextRetry is { } next)
            device.NextRetry = Timestamp.FromDateTimeOffset(next);
        return device;
    }

    public static Api.Tag ToProto(TagSnapshot snapshot) => new()
    {
        Device = snapshot.Definition.Key.Device,
        Name = snapshot.Definition.Key.Tag,
        Address = snapshot.Definition.Address,
        Group = snapshot.Definition.Group,
        IntervalMs = (int)snapshot.Definition.Interval.TotalMilliseconds,
        Writable = snapshot.Definition.Writable,
        Value = ToProto(snapshot.Value),
        Historized = snapshot.Definition.IsHistorized,
    };

    public static Api.TagUpdate ToProto(TagUpdate update) => new()
    {
        Device = update.Key.Device,
        Tag = update.Key.Tag,
        Value = ToProto(update.Value),
    };

    public static Api.TagValue ToProto(Abs.TagValue value)
    {
        var pb = new Api.TagValue
        {
            Text = Abs.QualityCodeExtensions.IsBad(value.Quality) ? "" : ValueFormatter.Format(value.Value),
            DataType = ToProto(value.DataType),
            Quality = ToProto(value.Quality),
            Timestamp = Timestamp.FromDateTimeOffset(value.Timestamp),
            Error = value.ErrorDetail ?? "",
        };
        SetTyped(pb, value.Value);
        return pb;
    }

    public static Api.WriteReply ToProto(Abs.WriteResult result) => new()
    {
        Success = result.Success,
        Quality = ToProto(result.Quality),
        Error = result.ErrorDetail ?? "",
    };

    public static Abs.TagDataType FromProto(Api.DataType type) => System.Enum.Parse<Abs.TagDataType>(type.ToString());

    public static Api.BrowseReply ToProto(Abs.BrowseResult result)
    {
        var reply = new Api.BrowseReply { TotalMatches = result.TotalMatches, TotalSymbols = result.TotalSymbols };
        reply.Items.AddRange(result.Items.Select(i => new Api.BrowseItem
        {
            Address = i.Address,
            DataType = ToProto(i.DataType),
            NativeType = i.NativeType ?? "",
        }));
        return reply;
    }

    public static Api.DeviceConfig ToProto(DeviceConfiguration device)
    {
        var pb = new Api.DeviceConfig
        {
            Name = device.Name,
            Driver = device.Driver,
            Enabled = device.Enabled,
            WritesEnabled = device.WritesEnabled,
            SettingsJson = device.Settings.ValueKind == JsonValueKind.Undefined ? "{}" : JsonSerializer.Serialize(device.Settings),
            Reconnect = new Api.ReconnectConfig
            {
                InitialDelayMs = device.Reconnect.InitialDelayMs,
                MaxDelayMs = device.Reconnect.MaxDelayMs,
                Multiplier = device.Reconnect.Multiplier,
            },
        };
        foreach (var group in device.ScanGroups)
        {
            var g = new Api.ScanGroupConfig { Name = group.Name, IntervalMs = group.IntervalMs };
            g.Tags.AddRange(group.Tags.Select(t => new Api.TagConfig
            {
                Name = t.Name ?? "",
                Address = t.Address,
                Writable = t.Writable,
                DataType = t.DataType is { } type ? ToProto(type) : Api.DataType.Unknown,
                History = t.History is { } h
                    ? new Api.HistoryConfig
                    {
                        Enabled = h.Enabled,
                        MinIntervalMs = h.MinIntervalMs,
                        Deadband = h.Deadband,
                        DeadbandPercent = h.DeadbandMode == DeadbandMode.Percent,
                        MaxIntervalMs = h.MaxIntervalMs,
                        Trigger = h.Trigger is { } trigger
                            ? new Api.HistoryTrigger { Tag = trigger.Tag, Condition = ToProto(trigger.Condition), Value = trigger.Value, HoldMs = trigger.HoldMs }
                            : null,
                    }
                    : null,
            }));
            pb.ScanGroups.Add(g);
        }
        return pb;
    }

    /// <summary>Convierte la configuración recibida. Lanza <see cref="ArgumentException"/> si los ajustes no son JSON válido.</summary>
    public static DeviceConfiguration FromProto(Api.DeviceConfig pb)
    {
        JsonElement settings;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(pb.SettingsJson) ? "{}" : pb.SettingsJson);
            settings = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Los ajustes de '{pb.Name}' no son JSON válido: {ex.Message}", nameof(pb), ex);
        }

        var device = new DeviceConfiguration
        {
            Name = pb.Name.Trim(),
            Driver = pb.Driver.Trim(),
            Enabled = pb.Enabled,
            WritesEnabled = pb.WritesEnabled,
            Settings = settings,
            Reconnect = pb.Reconnect is null
                ? new ReconnectSettings()
                : new ReconnectSettings { InitialDelayMs = pb.Reconnect.InitialDelayMs, MaxDelayMs = pb.Reconnect.MaxDelayMs, Multiplier = pb.Reconnect.Multiplier },
        };
        foreach (var g in pb.ScanGroups)
        {
            var group = new ScanGroupConfiguration { Name = g.Name.Trim(), IntervalMs = g.IntervalMs };
            group.Tags.AddRange(g.Tags.Select(t => new TagConfiguration
            {
                Name = string.IsNullOrWhiteSpace(t.Name) ? null : t.Name.Trim(),
                Address = t.Address.Trim(),
                Writable = t.Writable,
                DataType = t.DataType == Api.DataType.Unknown ? null : FromProto(t.DataType),
                History = t.History is { } h
                    ? new TagHistorySettings
                    {
                        Enabled = h.Enabled,
                        MinIntervalMs = h.MinIntervalMs,
                        Deadband = h.Deadband,
                        DeadbandMode = h.DeadbandPercent ? DeadbandMode.Percent : DeadbandMode.Absolute,
                        MaxIntervalMs = h.MaxIntervalMs,
                        Trigger = h.Trigger is { } trigger
                            ? new HistoryTrigger { Tag = trigger.Tag.Trim(), Condition = FromProto(trigger.Condition), Value = trigger.Value.Trim(), HoldMs = trigger.HoldMs }
                            : null,
                    }
                    : null,
            }));
            device.ScanGroups.Add(group);
        }
        return device;
    }

    public static Api.TriggerCondition ToProto(TriggerCondition condition) => condition switch
    {
        TriggerCondition.NotEqual => Api.TriggerCondition.NotEqual,
        TriggerCondition.Greater => Api.TriggerCondition.Greater,
        TriggerCondition.GreaterOrEqual => Api.TriggerCondition.GreaterOrEqual,
        TriggerCondition.Less => Api.TriggerCondition.Less,
        TriggerCondition.LessOrEqual => Api.TriggerCondition.LessOrEqual,
        _ => Api.TriggerCondition.Equal,
    };

    public static TriggerCondition FromProto(Api.TriggerCondition condition) => condition switch
    {
        Api.TriggerCondition.NotEqual => TriggerCondition.NotEqual,
        Api.TriggerCondition.Greater => TriggerCondition.Greater,
        Api.TriggerCondition.GreaterOrEqual => TriggerCondition.GreaterOrEqual,
        Api.TriggerCondition.Less => TriggerCondition.Less,
        Api.TriggerCondition.LessOrEqual => TriggerCondition.LessOrEqual,
        _ => TriggerCondition.Equal,
    };

    public static Api.HistorySeries ToProto(PlcMonitor.Historian.HistorySeries series)
    {
        var pb = new Api.HistorySeries { Device = series.Key.Device, Tag = series.Key.Tag, Truncated = series.Truncated };
        foreach (var p in series.Points)
        {
            var point = new Api.HistoryPoint { Timestamp = Timestamp.FromDateTimeOffset(p.Timestamp), Text = p.Text ?? "", Quality = ToProto(p.Quality) };
            if (p.Number is { } n)
                point.Number = n;
            pb.Points.Add(point);
        }
        return pb;
    }

    private static void SetTyped(Api.TagValue pb, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case bool b:
                pb.BoolValue = b;
                break;
            case sbyte or short or int or long:
                pb.IntValue = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
                break;
            case byte or ushort or uint or ulong:
                pb.UintValue = Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture);
                break;
            case float f:
                pb.DoubleValue = f;
                break;
            case double d:
                pb.DoubleValue = d;
                break;
            case string s:
                pb.StringValue = s;
                break;
            case byte[] bytes:
                pb.BytesValue = ByteString.CopyFrom(bytes);
                break;
            case DateTime dt:
                pb.TimeValue = Timestamp.FromDateTime(dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                break;
            case DateTimeOffset dto:
                pb.TimeValue = Timestamp.FromDateTimeOffset(dto);
                break;
            case TimeSpan ts:
                pb.DurationValue = Duration.FromTimeSpan(ts);
                break;
            default:
                // Arrays y otros tipos: solo en texto.
                pb.StringValue = ValueFormatter.Format(value);
                break;
        }
    }
}
