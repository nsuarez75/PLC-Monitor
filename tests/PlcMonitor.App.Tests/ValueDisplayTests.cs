using PlcMonitor.Agent.Contracts;
using PlcMonitor.Agent.Contracts.Client;

namespace PlcMonitor.App.Tests;

/// <summary>Presentación de valores con unidad y decimales (compartida por la app y la CLI).</summary>
public class ValueDisplayTests
{
    [Theory]
    [InlineData("23.4500007629395", DataType.Float, 2, "bar", "23.45 bar")]
    [InlineData("5", DataType.Double, 2, "", "5.00")]
    [InlineData("-0.001", DataType.Float, 2, null, "0.00")]
    [InlineData("1234.5678", DataType.Double, 0, "rpm", "1235 rpm")]
    [InlineData("23.4500007629395", DataType.Float, null, "bar", "23.4500007629395 bar")]
    [InlineData("42", DataType.Int16, 2, "piezas", "42 piezas")]
    [InlineData("true", DataType.Boolean, 2, "bar", "true")]
    [InlineData("hola", DataType.String, 2, "bar", "hola")]
    [InlineData("", DataType.Float, 2, "bar", "")]
    [InlineData("1E-05", DataType.Float, 3, " mm ", "0.000 mm")]
    public void Scalars(string text, DataType type, int? decimals, string? unit, string expected) =>
        ValueDisplay.Format(text, type, decimals, unit).ShouldBe(expected);

    [Fact]
    public void Arrays_round_each_element_and_keep_the_truncation_note()
    {
        ValueDisplay.Format("[1.25, -2E-05, NaN, 3, … (40 elementos)]", DataType.Float, 1, "°C")
            .ShouldBe("[1.3, 0.0, NaN, 3.0, … (40 elementos)] °C");
    }

    [Fact]
    public void Label_puts_the_unit_in_brackets()
    {
        ValueDisplay.Label("presion", "bar").ShouldBe("presion [bar]");
        ValueDisplay.Label("presion", " ").ShouldBe("presion");
    }

    [Fact]
    public void Decimals_are_read_only_when_present()
    {
        var tag = new Tag();
        tag.GetDecimals().ShouldBeNull();
        tag.Decimals = 0;
        tag.GetDecimals().ShouldBe(0);
    }
}
