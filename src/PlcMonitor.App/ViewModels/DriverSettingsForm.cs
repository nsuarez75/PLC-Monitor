using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcMonitor.App.Localization;

namespace PlcMonitor.App.ViewModels;

/// <summary>Opción de un desplegable: el valor que va al JSON y el texto que ve el usuario, que sigue el idioma elegido.</summary>
public sealed class SettingOption(string value, string label) : LocalizedObservableObject
{
    public string Value { get; } = value;

    public string Label => Loc.T(label);

    public override string ToString() => Label;
}

/// <summary>
/// Formulario de los ajustes de un driver conocido. Edita el mismo JSON que se guarda en el agente: las propiedades que el
/// formulario no conoce (de una versión más nueva del driver, por ejemplo) se conservan tal cual.
/// </summary>
public abstract partial class DriverSettingsForm : LocalizedObservableObject
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private JsonObject _json = [];

    /// <summary>Formulario para el driver, o null si no hay (se editan sus ajustes como JSON).</summary>
    /// <param name="serialPorts">Pide al agente sus puertos serie (para Modbus RTU).</param>
    public static DriverSettingsForm? For(string driverId, Func<Task<IReadOnlyList<string>?>> serialPorts) => driverId switch
    {
        S7SymbolicSettingsForm.Driver => new S7SymbolicSettingsForm(),
        S7AbsoluteSettingsForm.Driver => new S7AbsoluteSettingsForm(),
        ModbusSettingsForm.Driver => new ModbusSettingsForm(serialPorts),
        _ => null,
    };

    /// <summary>Carga los ajustes. Lanza <see cref="JsonException"/> si el texto no es un objeto JSON.</summary>
    public void Load(string json)
    {
        _json = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject
            ?? throw new JsonException(Loc.T("Los ajustes deben ser un objeto JSON ({ ... })."));
        Read(_json);
    }

    public string ToJson()
    {
        Write(_json);
        return _json.ToJsonString(Indented);
    }

    protected abstract void Read(JsonObject json);

    protected abstract void Write(JsonObject json);

    // El agente lee los ajustes sin distinguir mayúsculas: se busca la propiedad igual y se reescribe en camelCase.
    private static JsonNode? Get(JsonObject json, string name) =>
        json.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    protected static string Str(JsonObject json, string name, string fallback = "") =>
        Get(json, name) is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    protected static int Int(JsonObject json, string name, int fallback) =>
        Get(json, name) is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;

    protected static bool Bool(JsonObject json, string name, bool fallback = false) =>
        Get(json, name) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    /// <summary>Opción elegida a partir del valor guardado; si no está en la lista, la primera.</summary>
    protected static SettingOption Choice(JsonObject json, string name, IReadOnlyList<SettingOption> options) =>
        options.FirstOrDefault(o => string.Equals(o.Value, Str(json, name), StringComparison.OrdinalIgnoreCase)) ?? options[0];

    protected static void Set(JsonObject json, string name, JsonNode? value)
    {
        foreach (var key in json.Select(p => p.Key).Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList())
            json.Remove(key);
        json[name] = value;
    }
}

/// <summary>Siemens S7 simbólico (S7CommPlus): equipo, contraseña, usuario, seguridad y timeout.</summary>
public sealed partial class S7SymbolicSettingsForm : DriverSettingsForm
{
    public const string Driver = "s7-symbolic";

    public static IReadOnlyList<SettingOption> SecurityOptions { get; } =
    [
        new("Auto", "Automática (TLS si la CPU lo admite)"),
        new("Tls", "Solo TLS"),
        new("Legacy", "Sin TLS (TIA Portal V16 o anterior)"),
    ];

    [ObservableProperty]
    public partial string Host { get; set; } = "";

    /// <summary>La guardada llega como ******** y así se conserva si no se toca.</summary>
    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string Username { get; set; } = "";

    [ObservableProperty]
    public partial int TimeoutMs { get; set; } = 10000;

    [ObservableProperty]
    public partial SettingOption Security { get; set; } = SecurityOptions[0];

    protected override void Read(JsonObject json)
    {
        Host = Str(json, "host");
        Password = Str(json, "password");
        Username = Str(json, "username");
        TimeoutMs = Int(json, "timeoutMs", 10000);
        Security = Choice(json, "security", SecurityOptions);
    }

    protected override void Write(JsonObject json)
    {
        Set(json, "host", Host.Trim());
        Set(json, "password", Password);
        Set(json, "username", Username.Trim());
        Set(json, "timeoutMs", TimeoutMs);
        Set(json, "security", Security.Value);
    }
}

/// <summary>Siemens S7 absoluto (PUT/GET): equipo, puerto, CPU, rack, slot.</summary>
public sealed partial class S7AbsoluteSettingsForm : DriverSettingsForm
{
    public const string Driver = "s7-absolute";

    public static IReadOnlyList<SettingOption> CpuOptions { get; } =
    [
        new("S71500", "S7-1500"),
        new("S71200", "S7-1200"),
        new("S7300", "S7-300"),
        new("S7400", "S7-400"),
    ];

    [ObservableProperty]
    public partial string Host { get; set; } = "";

    [ObservableProperty]
    public partial int Port { get; set; } = 102;

    [ObservableProperty]
    public partial SettingOption Cpu { get; set; } = CpuOptions[0];

    [ObservableProperty]
    public partial int Rack { get; set; }

    [ObservableProperty]
    public partial int Slot { get; set; } = 1;

    [ObservableProperty]
    public partial int TimeoutMs { get; set; } = 5000;

    [ObservableProperty]
    public partial int ReadGapBytes { get; set; } = 32;

    /// <summary>Al elegir la CPU se propone su slot habitual.</summary>
    partial void OnCpuChanged(SettingOption value) => Slot = value.Value switch
    {
        "S7300" => 2,
        "S71200" => 1,
        "S71500" => 1,
        _ => Slot,
    };

    protected override void Read(JsonObject json)
    {
        Host = Str(json, "host");
        Port = Int(json, "port", 102);
        Cpu = Choice(json, "cpu", CpuOptions);
        Rack = Int(json, "rack", 0);
        Slot = Int(json, "slot", 1);
        TimeoutMs = Int(json, "timeoutMs", 5000);
        ReadGapBytes = Int(json, "readGapBytes", 32);
    }

    protected override void Write(JsonObject json)
    {
        Set(json, "host", Host.Trim());
        Set(json, "port", Port);
        Set(json, "cpu", Cpu.Value);
        Set(json, "rack", Rack);
        Set(json, "slot", Slot);
        Set(json, "timeoutMs", TimeoutMs);
        Set(json, "readGapBytes", ReadGapBytes);
    }
}

/// <summary>Modbus TCP, RTU (puerto serie del equipo del agente) o RTU sobre TCP.</summary>
public sealed partial class ModbusSettingsForm : DriverSettingsForm
{
    public const string Driver = "modbus";

    private readonly Func<Task<IReadOnlyList<string>?>> _serialPorts;
    private bool _portsLoaded;

    public ModbusSettingsForm(Func<Task<IReadOnlyList<string>?>> serialPorts)
    {
        _serialPorts = serialPorts;
    }

    public static IReadOnlyList<SettingOption> TransportOptions { get; } =
    [
        new("Tcp", "Modbus TCP"),
        new("Rtu", "Modbus RTU (puerto serie del equipo del colector)"),
        new("RtuOverTcp", "Modbus RTU sobre TCP (convertidor serie-Ethernet)"),
    ];

    public static IReadOnlyList<SettingOption> ParityOptions { get; } = [new("None", "Ninguna"), new("Even", "Par"), new("Odd", "Impar")];

    public static IReadOnlyList<SettingOption> StopBitsOptions { get; } = [new("One", "1"), new("Two", "2")];

    public static IReadOnlyList<SettingOption> ByteOrderOptions { get; } =
    [
        new("ABCD", "ABCD — normal (big-endian)"),
        new("CDAB", "CDAB — palabras intercambiadas"),
        new("BADC", "BADC — bytes intercambiados"),
        new("DCBA", "DCBA — todo al revés"),
    ];

    public ObservableCollection<int> BaudRates { get; } = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];

    public ObservableCollection<string> SerialPorts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSerial), nameof(IsNetwork), nameof(UnitIdHint))]
    public partial SettingOption Transport { get; set; } = TransportOptions[0];

    public bool IsSerial => Transport.Value == "Rtu";

    public bool IsNetwork => !IsSerial;

    public string UnitIdHint => Transport.Value == "Tcp"
        ? Loc.T("En Modbus TCP directo suele ser 1 (o 255); detrás de una pasarela, el número del esclavo serie.")
        : Loc.T("Número del esclavo en el bus (1-247).");

    [ObservableProperty]
    public partial string Host { get; set; } = "";

    [ObservableProperty]
    public partial int Port { get; set; } = 502;

    [ObservableProperty]
    public partial string SerialPort { get; set; } = "";

    [ObservableProperty]
    public partial string PortsHint { get; set; } = "";

    [ObservableProperty]
    public partial int BaudRate { get; set; } = 9600;

    [ObservableProperty]
    public partial SettingOption Parity { get; set; } = ParityOptions[0];

    [ObservableProperty]
    public partial SettingOption StopBits { get; set; } = StopBitsOptions[0];

    [ObservableProperty]
    public partial int UnitId { get; set; } = 1;

    [ObservableProperty]
    public partial int TimeoutMs { get; set; } = 1000;

    [ObservableProperty]
    public partial bool OneBasedAddresses { get; set; }

    [ObservableProperty]
    public partial SettingOption ByteOrder { get; set; } = ByteOrderOptions[0];

    [ObservableProperty]
    public partial int MaxRegistersPerRead { get; set; } = 125;

    [ObservableProperty]
    public partial int MaxBitsPerRead { get; set; } = 2000;

    [ObservableProperty]
    public partial int ReadGap { get; set; } = 8;

    [ObservableProperty]
    public partial bool UseSingleWriteFunctions { get; set; } = true;

    /// <summary>Al pasar a RTU se piden los puertos serie al agente (una vez; luego con «Actualizar»).</summary>
    partial void OnTransportChanged(SettingOption value)
    {
        if (value.Value == "Rtu" && !_portsLoaded)
            _ = RefreshPortsAsync();
    }

    [RelayCommand]
    public async Task RefreshPortsAsync()
    {
        _portsLoaded = true;
        var ports = await _serialPorts();
        SerialPorts.Clear();
        foreach (var port in ports ?? [])
            SerialPorts.Add(port);
        PortsHint = ports switch
        {
            null => Loc.T("No se pudo preguntar al colector por sus puertos serie: escríbelo (COM3, /dev/ttyUSB0...)."),
            { Count: 0 } => Loc.T("El colector no encuentra puertos serie (o es anterior a la 1.15): escríbelo a mano. En Linux suele ser /dev/ttyUSB0."),
            _ => Loc.F("{0} puerto(s) en el equipo del colector.", ports.Count),
        };
        if (string.IsNullOrWhiteSpace(SerialPort) && SerialPorts.Count > 0)
            SerialPort = SerialPorts[0];
    }

    protected override void Read(JsonObject json)
    {
        Transport = Choice(json, "transport", TransportOptions);
        Host = Str(json, "host");
        Port = Int(json, "port", 502);
        SerialPort = Str(json, "serialPort");
        BaudRate = Int(json, "baudRate", 9600);
        if (!BaudRates.Contains(BaudRate))
            BaudRates.Add(BaudRate);
        Parity = Choice(json, "parity", ParityOptions);
        StopBits = Choice(json, "stopBits", StopBitsOptions);
        UnitId = Int(json, "unitId", 1);
        TimeoutMs = Int(json, "timeoutMs", 1000);
        OneBasedAddresses = Bool(json, "oneBasedAddresses");
        ByteOrder = Choice(json, "byteOrder", ByteOrderOptions);
        MaxRegistersPerRead = Int(json, "maxRegistersPerRead", 125);
        MaxBitsPerRead = Int(json, "maxBitsPerRead", 2000);
        ReadGap = Int(json, "readGap", 8);
        UseSingleWriteFunctions = Bool(json, "useSingleWriteFunctions", true);
    }

    protected override void Write(JsonObject json)
    {
        Set(json, "transport", Transport.Value);
        Set(json, "host", Host.Trim());
        Set(json, "port", Port);
        Set(json, "serialPort", SerialPort.Trim());
        Set(json, "baudRate", BaudRate);
        Set(json, "parity", Parity.Value);
        Set(json, "stopBits", StopBits.Value);
        Set(json, "unitId", UnitId);
        Set(json, "timeoutMs", TimeoutMs);
        Set(json, "oneBasedAddresses", OneBasedAddresses);
        Set(json, "byteOrder", ByteOrder.Value);
        Set(json, "maxRegistersPerRead", MaxRegistersPerRead);
        Set(json, "maxBitsPerRead", MaxBitsPerRead);
        Set(json, "readGap", ReadGap);
        Set(json, "useSingleWriteFunctions", UseSingleWriteFunctions);
    }
}
