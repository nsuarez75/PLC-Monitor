using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using PlcMonitor.Core;
using PlcMonitor.Engine;
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
