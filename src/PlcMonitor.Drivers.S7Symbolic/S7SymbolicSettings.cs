namespace PlcMonitor.Drivers.S7Symbolic;

/// <summary>Seguridad de la sesión S7CommPlus.</summary>
public enum S7SymbolicSecurity
{
    /// <summary>Intenta TLS y, si la CPU lo rechaza, autenticación clásica por challenge (equivale a "Preferred" en Ignition).</summary>
    Auto,

    /// <summary>Solo TLS (S7-1200 FW ≥ 4.5 / S7-1500 FW ≥ 2.9 con comunicación PG/HMI segura).</summary>
    Tls,

    /// <summary>Solo autenticación clásica por challenge, sin TLS (proyectos TIA ≤ V16 o comunicación segura desactivada).</summary>
    Legacy,
}

/// <summary>Ajustes del driver S7 simbólico (S7CommPlus).</summary>
public sealed class S7SymbolicSettings
{
    public string Host { get; set; } = "";

    /// <summary>Contraseña del nivel de acceso de la CPU (vacía si la CPU no está protegida).</summary>
    public string Password { get; set; } = "";

    /// <summary>Usuario, solo para CPUs con gestión de usuarios (FW 3.1+). Vacío = legitimación clásica por contraseña.</summary>
    public string Username { get; set; } = "";

    public int TimeoutMs { get; set; } = 10000;

    public S7SymbolicSecurity Security { get; set; } = S7SymbolicSecurity.Auto;
}
