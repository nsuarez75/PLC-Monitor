using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;

namespace PlcMonitor.App.ViewModels;

/// <summary>
/// Alta de un tag escribiendo su dirección: la única forma en drivers sin explorador (absoluto, PUT/GET) y un atajo en
/// los demás. «Probar» lee la dirección del PLC antes de añadirla (el agente la valida al leer).
/// </summary>
public sealed partial class AddTagViewModel(IAgentContext context, Func<DeviceViewModel?> device, Func<string?, Driver?> driverOf) : ObservableObject
{
    /// <summary>Id del driver absoluto (PUT/GET).</summary>
    public const string AbsoluteDriver = "s7-absolute";

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

    /// <summary>Ejemplos de dirección según el driver del dispositivo.</summary>
    public string Hint
    {
        get
        {
            var id = device()?.Driver;
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
    }

    [RelayCommand]
    private void Open()
    {
        Reset();
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    private bool CanUse() => !IsBusy && !string.IsNullOrWhiteSpace(Address);

    /// <summary>Lee la dirección del PLC (sin añadirla) para comprobar que existe y ver su tipo.</summary>
    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task TestAsync()
    {
        if (context.Client is not { } client || device() is not { } target)
            return;
        IsBusy = true;
        try
        {
            var reply = await client.ReadAsync(new ReadRequest { Device = target.Name, Addresses = { Address.Trim() } });
            var value = reply.Values[0];
            TestIsError = value.Quality != Quality.Good;
            TestResult = TestIsError
                ? $"✖ {(string.IsNullOrEmpty(value.Error) ? value.Quality.ToString() : value.Error)}"
                : $"✔ {value.Text} ({value.DataType})";
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
            var result = await TagAdder.AddAsync(client, target.Name, Address, Name, GroupName, IntervalMs, Writable);
            context.Report(result.Message, isError: !result.Added);
            if (result.Added)
            {
                Address = "";
                Name = "";
                Reset();
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
