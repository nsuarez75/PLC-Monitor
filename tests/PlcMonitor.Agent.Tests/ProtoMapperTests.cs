using PlcMonitor.Abstractions;
using Api = PlcMonitor.Agent.Contracts;

namespace PlcMonitor.Agent.Tests;

public class ProtoMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

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
