namespace PlcMonitor.Abstractions;

public enum ConnectionState
{
    Disconnected = 0,
    Connecting,
    Connected,

    /// <summary>Error de conexión.</summary>
    Faulted,
}

public sealed class ConnectionStateChangedEventArgs(ConnectionState state, string? detail) : EventArgs
{
    public ConnectionState State { get; } = state;

    public string? Detail { get; } = detail;
}

[Flags]
public enum DriverCapabilities
{
    None = 0,
    Read = 1,
    Write = 2,

    /// <summary>La conexión implementa <see cref="IBrowsableConnection"/>.</summary>
    Browse = 4,
}
