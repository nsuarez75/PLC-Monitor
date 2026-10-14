namespace PlcMonitor.Abstractions;

/// <summary>Calidad de un valor, inspirada en OPC UA (Good / Uncertain / Bad).</summary>
public enum QualityCode
{
    Good = 0,
    Uncertain = 1,

    /// <summary>Error genérico.</summary>
    Bad = 100,

    /// <summary>El dispositivo no está conectado.</summary>
    BadNotConnected,

    /// <summary>La dirección no es válida o no existe en el dispositivo.</summary>
    BadAddressInvalid,

    /// <summary>El dispositivo deniega el acceso (permisos, contraseña, protección).</summary>
    BadAccessDenied,

    /// <summary>El valor no se puede convertir al tipo del dato.</summary>
    BadTypeMismatch,

    /// <summary>El dispositivo no respondió a tiempo.</summary>
    BadTimeout,

    /// <summary>La configuración del tag o del dispositivo es incorrecta.</summary>
    BadConfiguration,

    /// <summary>La operación no está soportada por el driver.</summary>
    BadNotSupported,

    /// <summary>Las escrituras están deshabilitadas.</summary>
    BadWriteDisabled,
}

public static class QualityCodeExtensions
{
    public static bool IsGood(this QualityCode quality) => quality == QualityCode.Good;

    public static bool IsBad(this QualityCode quality) => quality >= QualityCode.Bad;
}
