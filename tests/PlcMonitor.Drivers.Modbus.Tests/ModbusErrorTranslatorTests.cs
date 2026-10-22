using FluentModbus;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Drivers.Modbus.Tests;

public class ModbusErrorTranslatorTests
{
    [Theory]
    [InlineData(ModbusExceptionCode.IllegalFunction, QualityCode.BadNotSupported, "01")]
    [InlineData(ModbusExceptionCode.IllegalDataAddress, QualityCode.BadAddressInvalid, "02")]
    [InlineData(ModbusExceptionCode.IllegalDataValue, QualityCode.Bad, "03")]
    [InlineData(ModbusExceptionCode.ServerDeviceFailure, QualityCode.Bad, "04")]
    [InlineData(ModbusExceptionCode.ServerDeviceBusy, QualityCode.Bad, "06")]
    [InlineData(ModbusExceptionCode.GatewayPathUnavailable, QualityCode.BadNotConnected, "0A")]
    [InlineData(ModbusExceptionCode.GatewayTargetDeviceFailedToRespond, QualityCode.BadTimeout, "0B")]
    public void Translates_modbus_exceptions(ModbusExceptionCode code, QualityCode quality, string number)
    {
        var (q, detail) = ModbusErrorTranslator.Describe(code);
        q.ShouldBe(quality);
        detail.ShouldContain($"excepción {number}");
    }

    [Fact]
    public void Unknown_errors_keep_the_fallback()
    {
        var (q, detail) = ModbusErrorTranslator.Translate(new InvalidOperationException("otro"), QualityCode.BadAddressInvalid);
        q.ShouldBe(QualityCode.BadAddressInvalid);
        detail.ShouldBe("otro");
    }
}
