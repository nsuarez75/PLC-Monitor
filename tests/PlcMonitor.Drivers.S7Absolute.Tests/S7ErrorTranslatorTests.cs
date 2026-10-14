using PlcMonitor.Abstractions;

namespace PlcMonitor.Drivers.S7Absolute.Tests;

public class S7ErrorTranslatorTests
{
    // Respuesta real de un S7-1200 con PUT/GET deshabilitado al leer MW0 (S7.Net no interpreta la cabecera de error).
    private const string PutGetDisabled = "Received 12 bytes: '32-02-00-00-00-00-00-00-00-00-81-04', expected 26 bytes.";

    [Fact]
    public void Put_get_disabled_is_access_denied_with_a_hint()
    {
        var (quality, detail) = S7ErrorTranslator.Translate(new InvalidDataException(PutGetDisabled), QualityCode.BadAddressInvalid);

        quality.ShouldBe(QualityCode.BadAccessDenied);
        detail.ShouldContain("0x8104");
        detail.ShouldContain("PUT/GET");
    }

    [Fact]
    public void Header_error_is_found_in_inner_exceptions()
    {
        var ex = new InvalidOperationException("Error leyendo", new InvalidDataException(PutGetDisabled));

        S7ErrorTranslator.Translate(ex, QualityCode.Bad).Quality.ShouldBe(QualityCode.BadAccessDenied);
    }

    [Theory]
    [InlineData("'32-03-00-00-00-01-00-00-00-00-85-00'", 0x8500)]
    [InlineData("'32-02-00-00-00-00-00-02-00-00-D6-02-00'", 0xD602)]
    public void Header_error_is_extracted(string message, int expected)
    {
        S7ErrorTranslator.TryGetHeaderError(message, out var error).ShouldBeTrue();
        error.ShouldBe((ushort)expected);
    }

    [Theory]
    [InlineData("Área DataBlock DB99 no existe")]
    [InlineData("'32-02-00-00-00-00-00-00-00-00-00-00'")]
    [InlineData("'03-00-00-16-11-E0-00-00-00-01-00-C1'")]
    [InlineData("'32-02-00'")]
    public void Other_messages_keep_the_fallback(string message)
    {
        var (quality, detail) = S7ErrorTranslator.Translate(new InvalidOperationException(message), QualityCode.BadAddressInvalid);

        quality.ShouldBe(QualityCode.BadAddressInvalid);
        detail.ShouldBe(message);
    }

    [Fact]
    public void Unknown_header_error_is_reported_with_its_code()
    {
        var (quality, detail) = S7ErrorTranslator.Translate(
            new InvalidDataException("'32-02-00-00-00-00-00-00-00-00-87-01'"), QualityCode.BadAddressInvalid);

        quality.ShouldBe(QualityCode.Bad);
        detail.ShouldContain("0x8701");
    }
}
