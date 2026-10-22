using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;

namespace PlcMonitor.App.ViewModels;

/// <summary>
/// Alta de un tag escribiendo su dirección: la única forma en drivers sin explorador (absoluto, Modbus) y un atajo en
/// los demás. «Probar» lee la dirección del PLC antes de añadirla (el agente la valida al leer). En Modbus la dirección
/// se compone con desplegables (tabla, número, tipo, orden de bytes) y se puede dar de alta un rango de golpe.
/// </summary>
public sealed partial class AddTagViewModel(IAgentContext context, Func<DeviceViewModel?> device, Func<string?, Driver?> driverOf) : ObservableObject
{
    /// <summary>Id del driver absoluto (PUT/GET).</summary>
    public const string AbsoluteDriver = "s7-absolute";

    public const string ModbusDriver = "modbus";

    /// <summary>Tags como máximo en un rango.</summary>
    public const int MaxRange = 500;

    public static IReadOnlyList<SettingOption> ModbusTables { get; } =
    [
        new("HR", "Registros de retención (HR · 4x)"),
        new("IR", "Registros de entrada (IR · 3x)"),
        new("C", "Coils (C · 0x)"),
        new("DI", "Entradas discretas (DI · 1x)"),
    ];

    public static IReadOnlyList<SettingOption> ModbusTypes { get; } =
    [
        new("INT16", "INT16 — entero"),
        new("UINT16", "UINT16 — entero sin signo"),
        new("INT32", "INT32 — entero de 2 registros"),
        new("UINT32", "UINT32 — sin signo, 2 registros"),
        new("FLOAT", "FLOAT — real de 2 registros"),
        new("DOUBLE", "DOUBLE — real de 4 registros"),
        new("INT64", "INT64 — entero de 4 registros"),
        new("UINT64", "UINT64 — sin signo, 4 registros"),
        new("BIT", "Bit de un registro"),
        new("STRING", "Texto"),
    ];

    public static IReadOnlyList<SettingOption> ModbusOrders { get; } =
    [
        new("", "Orden del dispositivo"),
        new("ABCD", "ABCD"),
        new("CDAB", "CDAB"),
        new("BADC", "BADC"),
        new("DCBA", "DCBA"),
    ];

    /// <summary>Última dirección compuesta con los desplegables (si el usuario la cambia a mano, no hay rango).</summary>
    private string _composed = "";

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand), nameof(TestCommand))]
    public partial string Address { get; set; } = "";

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string GroupName { get; set; } = "default";

    [ObservableProperty]
    public partial int IntervalMs { get; set; } = 1000;

    [ObservableProperty]
    public partial bool Writable { get; set; }

    /// <summary>Resultado de «Probar» (valor, tipo y calidad, o el error).</summary>
    [ObservableProperty]
    public partial string TestResult { get; set; } = "";

    [ObservableProperty]
    public partial bool TestIsError { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand), nameof(TestCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRegisterTable), nameof(IsBitType), nameof(IsStringType), nameof(HasByteOrder), nameof(CanBeWritable))]
    public partial SettingOption ModbusTable { get; set; } = ModbusTables[0];

    /// <summary>Número de registro o bit, con la numeración del dispositivo (desde 0 o desde 1).</summary>
    [ObservableProperty]
    public partial int ModbusNumber { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBitType), nameof(IsStringType), nameof(HasByteOrder))]
    public partial SettingOption ModbusType { get; set; } = ModbusTypes[0];

    [ObservableProperty]
    public partial int ModbusBit { get; set; }

    /// <summary>Caracteres del texto (dos por registro).</summary>
    [ObservableProperty]
    public partial int ModbusStringLength { get; set; } = 10;

    [ObservableProperty]
    public partial SettingOption ModbusOrder { get; set; } = ModbusOrders[0];

    /// <summary>Tags a dar de alta seguidos a partir de la dirección (1 = solo ese).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangePreview))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand), nameof(TestCommand))]
    public partial int RangeCount { get; set; } = 1;

    /// <summary>El dispositivo seleccionado es Modbus: se muestran los desplegables.</summary>
    public bool IsModbus => string.Equals(device()?.Driver, ModbusDriver, StringComparison.OrdinalIgnoreCase);

    public bool IsRegisterTable => ModbusTable.Value is "HR" or "IR";

    public bool IsBitType => IsRegisterTable && ModbusType.Value == "BIT";

    public bool IsStringType => IsRegisterTable && ModbusType.Value == "STRING";

    public bool HasByteOrder => IsRegisterTable && ModbusType.Value is not ("INT16" or "UINT16" or "BIT");

    /// <summary>Las entradas discretas y los registros de entrada son de solo lectura.</summary>
    public bool CanBeWritable => !IsModbus || ModbusTable.Value is "HR" or "C";

    /// <summary>Hay rango: varios tags y la dirección es la de los desplegables.</summary>
    public bool IsRange => IsModbus && RangeCount > 1 && Address.Trim() == _composed;

    public string RangePreview
    {
        get
        {
            if (!IsRange)
                return "";
            var addresses = RangeAddresses();
            return $"{addresses.Count} tags: {addresses[0]} … {addresses[^1]}";
        }
    }

    partial void OnModbusTableChanged(SettingOption value)
    {
        if (!CanBeWritable)
            Writable = false;
        Compose();
    }

    partial void OnModbusNumberChanged(int value) => Compose();

    partial void OnModbusTypeChanged(SettingOption value) => Compose();

    partial void OnModbusBitChanged(int value) => Compose();

    partial void OnModbusStringLengthChanged(int value) => Compose();

    partial void OnModbusOrderChanged(SettingOption value) => Compose();

    partial void OnAddressChanged(string value) => OnPropertyChanged(nameof(RangePreview));

    /// <summary>Compone la dirección (HR100:FLOAT:CDAB...) a partir de los desplegables.</summary>
    private void Compose()
    {
        _composed = ModbusAddressOf(ModbusNumber, ModbusBit);
        Address = _composed;
    }

    private string ModbusAddressOf(int number, int bit)
    {
        var address = $"{ModbusTable.Value}{number}";
        if (!IsRegisterTable)
            return address;
        if (IsBitType)
            return $"{address}.{bit}";
        if (IsStringType)
            address += $":STRING[{ModbusStringLength}]";
        else if (ModbusType.Value != "INT16")
            address += $":{ModbusType.Value}";
        if (HasByteOrder && ModbusOrder.Value.Length > 0)
            address += $":{ModbusOrder.Value}";
        return address;
    }

    /// <summary>Registros (o bits) que ocupa cada tag con el tipo elegido.</summary>
    private int Step => !IsRegisterTable || IsBitType ? 1 : ModbusType.Value switch
    {
        "INT32" or "UINT32" or "FLOAT" => 2,
        "INT64" or "UINT64" or "DOUBLE" => 4,
        "STRING" => (ModbusStringLength + 1) / 2,
        _ => 1,
    };

    /// <summary>Tras añadir, los desplegables pasan a la dirección siguiente a la última añadida.</summary>
    private void MoveAfter(int added)
    {
        if (IsBitType)
        {
            var bit = ModbusBit + added;
            ModbusNumber += bit / 16;
            ModbusBit = bit % 16;
        }
        else
        {
            ModbusNumber += added * Step;
        }
    }

    /// <summary>Direcciones del rango: cada tag empieza donde acaba el anterior (los bits, de uno en uno dentro de los registros).</summary>
    public IReadOnlyList<string> RangeAddresses()
    {
        if (!IsRange)
            return [Address.Trim()];
        var count = Math.Min(RangeCount, MaxRange);
        var step = Step;
        var addresses = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            if (IsBitType)
            {
                var bit = ModbusBit + i;
                addresses.Add(ModbusAddressOf(ModbusNumber + (bit / 16), bit % 16));
            }
            else
            {
                addresses.Add(ModbusAddressOf(ModbusNumber + (i * step), 0));
            }
        }
        return addresses;
    }

    /// <summary>Ejemplos de dirección según el driver del dispositivo.</summary>
    public string Hint
    {
        get
        {
            var id = device()?.Driver;
            if (string.Equals(id, ModbusDriver, StringComparison.OrdinalIgnoreCase))
            {
                return "Modbus: HR0 · HR100:FLOAT · HR100:FLOAT:CDAB · IR5:UINT16 · C0 · DI3 · HR10.3 (bit) · HR20:STRING[10] · 40001 · 30005:INT32. " +
                    "HR, IR, C y DI siguen la numeración del dispositivo (desde 0 o desde 1); 40001, 30001… siempre empiezan en 1.";
            }
            if (string.Equals(id, AbsoluteDriver, StringComparison.OrdinalIgnoreCase))
                return "Dirección absoluta: DB1.DBX0.3 (bit) · DB1.DBW10 (Word) · DB1.INT10 · DB1.DINT12 · DB1.REAL20 · DB5.STRING30[20] · M10.1 · MW20 · IW64 · QX4.0. Los DB deben ser no optimizados.";
            return driverOf(id) is { CanBrowse: true }
                ? "Ruta del símbolo, p.ej. \"Motor_DB\".Velocidad o \"Datos\".Lista[3]. También puedes buscarlo en «Explorar»."
                : "Dirección del tag tal como la entiende el driver del dispositivo.";
        }
    }

    /// <summary>Al cambiar de dispositivo: se borra la prueba y se recalcula la ayuda.</summary>
    public void Reset()
    {
        TestResult = "";
        TestIsError = false;
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(IsModbus));
        OnPropertyChanged(nameof(CanBeWritable));
        OnPropertyChanged(nameof(RangePreview));
    }

    [RelayCommand]
    private void Open()
    {
        Reset();
        if (IsModbus && string.IsNullOrWhiteSpace(Address))
            Compose();
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    private bool CanUse() => !IsBusy && !string.IsNullOrWhiteSpace(Address) && RangeCount is >= 1 and <= MaxRange;

    private static string Error(TagValue value) => string.IsNullOrEmpty(value.Error) ? value.Quality.ToString() : value.Error;

    /// <summary>Lee la dirección del PLC (sin añadirla) para comprobar que existe y ver su tipo.</summary>
    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task TestAsync()
    {
        if (context.Client is not { } client || device() is not { } target)
            return;
        IsBusy = true;
        try
        {
            var addresses = RangeAddresses();
            var reply = await client.ReadAsync(new ReadRequest { Device = target.Name, Addresses = { addresses } });
            if (addresses.Count == 1)
            {
                var value = reply.Values[0];
                TestIsError = value.Quality != Quality.Good;
                TestResult = TestIsError
                    ? $"✖ {Error(value)}"
                    : $"✔ {value.Text} ({value.DataType})";
                return;
            }
            // Rango: los primeros valores y el primer error, si lo hay.
            var bad = reply.Values.Select((v, i) => (Value: v, Address: addresses[i])).FirstOrDefault(x => x.Value.Quality != Quality.Good);
            TestIsError = bad.Value is not null;
            var good = reply.Values.Count(v => v.Quality == Quality.Good);
            var sample = string.Join(" · ", reply.Values.Take(6).Select(v => v.Quality == Quality.Good ? v.Text : "✖")) + (reply.Values.Count > 6 ? " …" : "");
            TestResult = TestIsError
                ? $"✖ {good} de {addresses.Count} leídos; {bad.Address}: {Error(bad.Value!)}"
                : $"✔ {addresses.Count} leídos ({reply.Values[0].DataType}): {sample}";
        }
        catch (RpcException ex)
        {
            TestIsError = true;
            TestResult = $"✖ {context.Describe(ex)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Añade el tag y deja el formulario listo para el siguiente (mismo grupo).</summary>
    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task AddAsync()
    {
        if (context.Client is not { } client || device() is not { } target)
            return;
        IsBusy = true;
        try
        {
            var addresses = RangeAddresses();
            // En un rango con nombre, cada tag lleva el nombre y su posición (nivel_1, nivel_2...).
            var tags = addresses.Select((a, i) => (a, addresses.Count > 1 && !string.IsNullOrWhiteSpace(Name) ? $"{Name.Trim()}_{i + 1}" : (string?)Name)).ToList();
            var composed = Address.Trim() == _composed;
            var result = await TagAdder.AddAsync(client, target.Name, tags, GroupName, IntervalMs, Writable && CanBeWritable);
            context.Report(result.Message, isError: !result.Added);
            if (result.Added)
            {
                Address = "";
                Name = "";
                RangeCount = 1;
                Reset();
                if (IsModbus && composed)
                    MoveAfter(addresses.Count);
                if (IsModbus)
                    Compose();
            }
        }
        catch (RpcException ex)
        {
            context.Report(context.Describe(ex), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
