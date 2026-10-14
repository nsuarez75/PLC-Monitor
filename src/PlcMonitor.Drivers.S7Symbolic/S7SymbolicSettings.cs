namespace PlcMonitor.Drivers.S7Symbolic;

/// <summary>Ajustes del driver S7 simbólico (S7CommPlus con TLS).</summary>
public sealed class S7SymbolicSettings
{
    public string Host { get; set; } = "";

    /// <summary>Contraseña del nivel de acceso de la CPU (vacía si la CPU no está protegida).</summary>
    public string Password { get; set; } = "";

    /// <summary>Usuario, solo para CPUs con gestión de usuarios (FW 3.1+). Vacío = legitimación clásica por contraseña.</summary>
    public string Username { get; set; } = "";

    public int TimeoutMs { get; set; } = 10000;

    /// <summary>
    /// Solo para diagnóstico: si se indica, los secretos TLS de cada sesión se guardan en este directorio
    /// (key_*.log) para poder descifrar capturas con Wireshark. No usar en producción.
    /// </summary>
    public string? TlsKeyLogDirectory { get; set; }
}
