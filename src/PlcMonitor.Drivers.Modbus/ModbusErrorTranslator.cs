using FluentModbus;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Drivers.Modbus;

/// <summary>Traduce las excepciones Modbus que devuelve el esclavo a calidades y mensajes útiles.</summary>
internal static class ModbusErrorTranslator
{
    public static (QualityCode Quality, string Detail) Translate(Exception exception, QualityCode fallback)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is ModbusException { ExceptionCode: not ModbusExceptionCode.OK } modbus)
                return Describe(modbus.ExceptionCode);
        }
        return (fallback, exception.Message);
    }

    internal static (QualityCode Quality, string Detail) Describe(ModbusExceptionCode code) => code switch
    {
        ModbusExceptionCode.IllegalFunction => (QualityCode.BadNotSupported,
            "El equipo no admite esta función Modbus (excepción 01). Si falla al escribir un solo registro o coil, prueba a desactivar " +
            "«funciones de escritura simple» (05/06) en el dispositivo."),
        ModbusExceptionCode.IllegalDataAddress => (QualityCode.BadAddressInvalid,
            "El equipo no tiene esa dirección (excepción 02). Revisa la tabla, el número y si el equipo numera desde 0 o desde 1."),
        ModbusExceptionCode.IllegalDataValue => (QualityCode.Bad,
            "El equipo rechaza el valor o la cantidad pedida (excepción 03)."),
        ModbusExceptionCode.ServerDeviceFailure => (QualityCode.Bad,
            "Fallo interno del equipo al atender la petición (excepción 04)."),
        ModbusExceptionCode.Acknowledge => (QualityCode.Uncertain,
            "El equipo aceptó la petición pero tardará en completarla (excepción 05)."),
        ModbusExceptionCode.ServerDeviceBusy => (QualityCode.Bad,
            "El equipo está ocupado (excepción 06); se reintenta en la siguiente lectura."),
        ModbusExceptionCode.MemoryParityError => (QualityCode.Bad,
            "Error de paridad en la memoria del equipo (excepción 08)."),
        ModbusExceptionCode.GatewayPathUnavailable => (QualityCode.BadNotConnected,
            "La pasarela no tiene ruta hacia el esclavo (excepción 0A). Revisa el unit id y la configuración de la pasarela."),
        ModbusExceptionCode.GatewayTargetDeviceFailedToRespond => (QualityCode.BadTimeout,
            "El esclavo no respondió a la pasarela (excepción 0B). Revisa el unit id, el cableado RS-485 y los ajustes serie de la pasarela."),
        _ => (QualityCode.Bad, $"El equipo devolvió la excepción Modbus {(int)code:X2}."),
    };
}
