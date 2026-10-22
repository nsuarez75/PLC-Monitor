using Opc.Ua;
using PlcMonitor.Abstractions;

namespace PlcMonitor.OpcUa.Tests;

/// <summary>Contraseñas de los usuarios y conversión de tipos, valores y calidades.</summary>
public class OpcUaMappingTests
{
    [Fact]
    public void Passwords_are_stored_as_salted_hashes()
    {
        var hash = OpcUaPassword.Hash("secreto");

        hash.ShouldStartWith("pbkdf2-sha256$100000$");
        hash.ShouldNotContain("secreto");
        OpcUaPassword.Hash("secreto").ShouldNotBe(hash);
        OpcUaPassword.Verify("secreto", hash).ShouldBeTrue();
        OpcUaPassword.Verify("otro", hash).ShouldBeFalse();
        OpcUaPassword.Verify("", hash).ShouldBeFalse();
        OpcUaPassword.Verify("secreto", "mal$formado").ShouldBeFalse();
        OpcUaPassword.Verify("secreto", "pbkdf2-sha256$100000$no-base64$x").ShouldBeFalse();
    }

    [Theory]
    [InlineData(TagDataType.Boolean, false, "Boolean")]
    [InlineData(TagDataType.Int16, false, "Int16")]
    [InlineData(TagDataType.Float, false, "Float")]
    [InlineData(TagDataType.Int16, true, "Double")]
    [InlineData(TagDataType.TimeSpan, false, "Duration")]
    [InlineData(null, false, "BaseDataType")]
    public void Data_types_follow_the_tag(TagDataType? type, bool scaled, string expected)
    {
        var ids = new Dictionary<string, NodeId>
        {
            ["Boolean"] = DataTypeIds.Boolean, ["Int16"] = DataTypeIds.Int16, ["Float"] = DataTypeIds.Float,
            ["Double"] = DataTypeIds.Double, ["Duration"] = DataTypeIds.Duration, ["BaseDataType"] = DataTypeIds.BaseDataType,
        };

        OpcUaMapping.DataTypeOf(type, scaled).ShouldBe(ids[expected]);
    }

    [Fact]
    public void Written_values_reach_the_engine_as_invariant_text()
    {
        OpcUaMapping.ToWriteText(Variant.From(true)).ShouldBe("true");
        OpcUaMapping.ToWriteText(Variant.From(2.5)).ShouldBe("2.5");
        OpcUaMapping.ToWriteText(Variant.From((short)-7)).ShouldBe("-7");
        OpcUaMapping.ToWriteText(Variant.From("hola")).ShouldBe("hola");
    }

    [Fact]
    public void Values_take_the_type_declared_by_the_variable()
    {
        OpcUaMapping.ToVariant((short)7, DataTypeIds.Int32).ShouldBe(Variant.From(7));
        OpcUaMapping.ToVariant(7, DataTypeIds.Double).ShouldBe(Variant.From(7.0));
        OpcUaMapping.ToVariant("x", DataTypeIds.Int32).ShouldBe(Variant.From("x"));
        OpcUaMapping.ToVariant(70000, DataTypeIds.Int16).ShouldBe(Variant.From(70000));
    }

    [Fact]
    public void Qualities_map_to_status_codes()
    {
        OpcUaMapping.ToStatus(QualityCode.Good).ShouldBe(StatusCodes.Good);
        OpcUaMapping.ToStatus(QualityCode.BadNotConnected).ShouldBe(StatusCodes.BadNotConnected);
        OpcUaMapping.ToStatus(QualityCode.BadWriteDisabled).ShouldBe(StatusCodes.BadNotWritable);
        OpcUaMapping.ToStatus(QualityCode.Uncertain).ShouldBe(StatusCodes.Uncertain);
    }

    [Fact]
    public void History_points_keep_the_type_of_the_variable()
    {
        var point = new PlcMonitor.Historian.HistoryPoint(DateTimeOffset.UtcNow, 1, null, QualityCode.Good);

        OpcUaMapping.ToVariant(point, DataTypeIds.Boolean).ShouldBe(Variant.From(true));
        OpcUaMapping.ToVariant(point, DataTypeIds.Int16).ShouldBe(Variant.From((short)1));
        OpcUaMapping.ToVariant(point, DataTypeIds.Double).ShouldBe(Variant.From(1.0));
        OpcUaMapping.ToVariant(point with { Number = null, Text = "Marcha" }, DataTypeIds.String).ShouldBe(Variant.From("Marcha"));
    }
}
