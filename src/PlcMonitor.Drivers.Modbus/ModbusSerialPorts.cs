using System.IO.Ports;

namespace PlcMonitor.Drivers.Modbus;

/// <summary>Puertos serie del equipo, para elegir el de un dispositivo Modbus RTU.</summary>
public static class ModbusSerialPorts
{
    /// <summary>
    /// En Linux, primero los nombres estables de /dev/serial/by-id (no cambian al reconectar el adaptador USB), después los
    /// USB (ttyUSB, ttyACM) y por último los de la placa (ttyS, ttyAMA). En Windows, COM1, COM2... en orden numérico.
    /// </summary>
    public static IReadOnlyList<string> List()
    {
        var ports = new List<string>();
        try
        {
            ports.AddRange(SerialPort.GetPortNames());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Sin acceso a la lista: el usuario puede escribir el puerto a mano.
        }
        if (OperatingSystem.IsLinux() && Directory.Exists("/dev/serial/by-id"))
        {
            try
            {
                ports.AddRange(Directory.GetFiles("/dev/serial/by-id"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return Sort(ports);
    }

    internal static IReadOnlyList<string> Sort(IEnumerable<string> ports) =>
        ports.Distinct(StringComparer.Ordinal)
            .OrderBy(Rank)
            .ThenBy(p => p.TrimEnd("0123456789".ToCharArray()), StringComparer.Ordinal)
            .ThenBy(Number)
            .ThenBy(p => p, StringComparer.Ordinal)
            .ToList();

    private static int Rank(string port) =>
        port.StartsWith("/dev/serial/", StringComparison.Ordinal) ? 0
        : port.Contains("ttyUSB", StringComparison.Ordinal) || port.Contains("ttyACM", StringComparison.Ordinal) ? 1
        : 2;

    private static int Number(string port)
    {
        var digits = port[(port.TrimEnd("0123456789".ToCharArray()).Length)..];
        return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
}
