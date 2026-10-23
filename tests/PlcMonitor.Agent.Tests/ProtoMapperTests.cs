using PlcMonitor.Abstractions;
using Api = PlcMonitor.Agent.Contracts;

namespace PlcMonitor.Agent.Tests;

public class ProtoMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Opc_ua_keys_round_trip_and_tags_without_the_field_stay_published()
    {
        var device = new PlcMonitor.Engine.Configuration.DeviceConfiguration
        {
            Name = "plc1",
            Driver = "fake",
            OpcUaPublished = true,
            ScanGroups = { new PlcMonitor.Engine.Configuration.ScanGroupConfiguration { Name = "g", IntervalMs = 100, Tags = [
                new PlcMonitor.Engine.Configuration.TagConfiguration { Address = "DB1.DBW0", OpcUaPublished = false },
                new PlcMonitor.Engine.Configuration.TagConfiguration { Address = "DB1.DBW2" },
            ] } },
        };

        var pb = ProtoMapper.ToProto(device);

        pb.OpcUaPublished.ShouldBeTrue();
        pb.ScanGroups[0].Tags.Select(t => t.OpcUaHidden).ShouldBe([true, false]);
        var back = ProtoMapper.FromProto(pb);
        back.OpcUaPublished.ShouldBeTrue();
        back.ScanGroups[0].Tags.Select(t => t.OpcUaPublished).ShouldBe([false, true]);
        // Un cliente anterior no manda los campos: dispositivo sin publicar y tags publicados.
        var old = ProtoMapper.FromProto(new Api.DeviceConfig { Name = "plc2", Driver = "fake", ScanGroups = { new Api.ScanGroupConfig { Name = "g", IntervalMs = 100, Tags = { new Api.TagConfig { Address = "DB1.DBW0" } } } } });
        old.OpcUaPublished.ShouldBeFalse();
        old.ScanGroups[0].Tags[0].OpcUaPublished.ShouldBeTrue();
    }

    [Fact]
    public void Disabled_tags_round_trip_and_tags_without_the_field_stay_enabled()
    {
        var device = new PlcMonitor.Engine.Configuration.DeviceConfiguration
        {
            Name = "plc1",
            Driver = "fake",
            ScanGroups = { new PlcMonitor.Engine.Configuration.ScanGroupConfiguration { Name = "g", IntervalMs = 100, Tags = [
                new PlcMonitor.Engine.Configuration.TagConfiguration { Address = "DB1.DBW0", Enabled = false },
                new PlcMonitor.Engine.Configuration.TagConfiguration { Address = "DB1.DBW2" },
            ] } },
        };

        var pb = ProtoMapper.ToProto(device);

        pb.ScanGroups[0].Tags.Select(t => t.Disabled).ShouldBe([true, false]);
        ProtoMapper.FromProto(pb).ScanGroups[0].Tags.Select(t => t.Enabled).ShouldBe([false, true]);
        // Un cliente anterior no manda el campo: el tag queda activo.
        var old = ProtoMapper.FromProto(new Api.DeviceConfig { Name = "plc2", Driver = "fake", ScanGroups = { new Api.ScanGroupConfig { Name = "g", IntervalMs = 100, Tags = { new Api.TagConfig { Address = "DB1.DBW0" } } } } });
        old.ScanGroups[0].Tags[0].Enabled.ShouldBeTrue();
    }

    [Fact]
    public void Tag_order_round_trips_and_is_absent_when_not_sent()
    {
        var device = new PlcMonitor.Engine.Configuration.DeviceConfiguration { Name = "plc1", Driver = "fake", TagOrder = ["b", "a"] };

        var pb = ProtoMapper.ToProto(device);

        pb.TagOrder.ShouldBe(["b", "a"]);
        ProtoMapper.FromProto(pb).TagOrder.ShouldBe(["b", "a"]);
        ProtoMapper.FromProto(new Api.DeviceConfig { Name = "plc2", Driver = "fake" }).TagOrder.ShouldBeNull();
    }

    [Fact]
    public void Disabled_tag_snapshot_says_so()
    {
        var definition = new PlcMonitor.Engine.TagDefinition(new PlcMonitor.Engine.TagKey("plc1", "t"), "DB1.DBW0", "g", TimeSpan.FromSeconds(1), false, null)
        {
            Enabled = false,
        };

        ProtoMapper.ToProto(new PlcMonitor.Engine.TagSnapshot(definition, TagValue.Bad(QualityCode.Bad, Now, "Tag desactivado"))).Disabled.ShouldBeTrue();
        ProtoMapper.ToProto(new PlcMonitor.Engine.TagSnapshot(definition with { Enabled = true }, TagValue.Bad(QualityCode.Bad, Now))).Disabled.ShouldBeFalse();
    }

    [Fact]
    public void Every_quality_has_a_proto_equivalent()
    {
        foreach (var quality in Enum.GetValues<QualityCode>())
            ProtoMapper.ToProto(quality).ToString().ShouldBe(quality.ToString());
    }

    [Fact]
    public void Every_data_type_has_a_proto_equivalent()
    {
        foreach (var type in Enum.GetValues<TagDataType>())
            ProtoMapper.ToProto(type).ToString().ShouldBe(type.ToString());
    }

    [Fact]
    public void Every_connection_state_has_a_proto_equivalent()
    {
        foreach (var state in Enum.GetValues<ConnectionState>())
            ProtoMapper.ToProto(state).ToString().ShouldBe(state.ToString());
    }

    public static TheoryData<object, Api.TagValue.TypedOneofCase> TypedValues => new()
    {
        { true, Api.TagValue.TypedOneofCase.BoolValue },
        { (short)-5, Api.TagValue.TypedOneofCase.IntValue },
        { -5L, Api.TagValue.TypedOneofCase.IntValue },
        { (byte)5, Api.TagValue.TypedOneofCase.UintValue },
        { 5u, Api.TagValue.TypedOneofCase.UintValue },
        { 1.5f, Api.TagValue.TypedOneofCase.DoubleValue },
        { 1.5d, Api.TagValue.TypedOneofCase.DoubleValue },
        { "hola", Api.TagValue.TypedOneofCase.StringValue },
        { new byte[] { 1, 2 }, Api.TagValue.TypedOneofCase.BytesValue },
        { new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Api.TagValue.TypedOneofCase.TimeValue },
        { TimeSpan.FromSeconds(3), Api.TagValue.TypedOneofCase.DurationValue },
        { new[] { 1, 2 }, Api.TagValue.TypedOneofCase.StringValue },
    };

    [Theory]
    [MemberData(nameof(TypedValues))]
    public void Values_are_sent_typed_and_as_text(object value, Api.TagValue.TypedOneofCase expected)
    {
        var pb = ProtoMapper.ToProto(TagValue.Good(value, TagDataType.Unknown, Now));

        pb.TypedCase.ShouldBe(expected);
        pb.Text.ShouldNotBeEmpty();
        pb.Quality.ShouldBe(Api.Quality.Good);
    }

    [Fact]
    public void Numbers_keep_their_value()
    {
        ProtoMapper.ToProto(TagValue.Good(-32768, TagDataType.Int16, Now)).IntValue.ShouldBe(-32768);
        ProtoMapper.ToProto(TagValue.Good(ulong.MaxValue, TagDataType.UInt64, Now)).UintValue.ShouldBe(ulong.MaxValue);
        ProtoMapper.ToProto(TagValue.Good(0.1f, TagDataType.Float, Now)).DoubleValue.ShouldBe(0.1f);
    }

    [Fact]
    public void Bad_value_has_no_text_and_carries_the_error()
    {
        var pb = ProtoMapper.ToProto(TagValue.Bad(QualityCode.BadTimeout, Now, "sin respuesta"));

        pb.Text.ShouldBeEmpty();
        pb.TypedCase.ShouldBe(Api.TagValue.TypedOneofCase.None);
        pb.Quality.ShouldBe(Api.Quality.BadTimeout);
        pb.Error.ShouldBe("sin respuesta");
        pb.Timestamp.ToDateTimeOffset().ShouldBe(Now);
    }
}
