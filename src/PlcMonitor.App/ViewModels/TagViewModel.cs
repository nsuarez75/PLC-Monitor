using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Charts;

namespace PlcMonitor.App.ViewModels;

/// <summary>Un tag configurado con su último valor.</summary>
public sealed partial class TagViewModel : ObservableObject
{
    private readonly IAgentContext _context;

    public TagViewModel(IAgentContext context, Tag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        _context = context;
        Device = tag.Device;
        Name = tag.Name;
        Address = tag.Address;
        Group = $"{tag.Group} ({tag.IntervalMs} ms)";
        Writable = tag.Writable;
        Historized = tag.Historized;
        if (tag.Value is not null)
            Apply(tag.Value);
    }

    public string Device { get; }

    public string Name { get; }

    public string Address { get; }

    public string Group { get; }

    public bool Writable { get; }

    /// <summary>El agente guarda este tag en el histórico.</summary>
    public bool Historized { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrue), nameof(IsFalse))]
    public partial string ValueText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGood), nameof(IsUncertain), nameof(IsBad), nameof(IsTrue), nameof(IsFalse))]
    public partial Quality Quality { get; set; } = Quality.BadNotConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBoolean), nameof(ShowTextWrite), nameof(ShowBoolWrite), nameof(IsChartable))]
    public partial string DataType { get; set; } = "";

    [ObservableProperty]
    public partial string Time { get; set; } = "";

    /// <summary>Valor que el usuario quiere escribir.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    public partial string NewValue { get; set; } = "";

    public bool IsGood => Quality == Quality.Good;

    public bool IsUncertain => Quality == Quality.Uncertain;

    public bool IsBad => !IsGood && !IsUncertain;

    public bool IsBoolean => DataType == nameof(Agent.Contracts.DataType.Boolean);

    /// <summary>Se puede dibujar (números y booleanos; si aún no se conoce el tipo, también).</summary>
    public bool IsChartable => DataType is not (nameof(Agent.Contracts.DataType.String) or nameof(Agent.Contracts.DataType.DateTime)
        or nameof(Agent.Contracts.DataType.TimeSpan) or nameof(Agent.Contracts.DataType.ByteArray));

    /// <summary>Ventana que se guarda de cada tag para la tendencia en vivo.</summary>
    public static readonly TimeSpan TrendWindow = TimeSpan.FromHours(1);

    public const int MaxTrendPoints = 20_000;

    /// <summary>Valores recientes (desde que la app recibe el tag, como mucho <see cref="TrendWindow"/>) para la tendencia.</summary>
    public List<ChartPoint> TrendPoints { get; } = [];

    /// <summary>Hereda los puntos de la tendencia de la versión anterior del tag (tras reconfigurar el dispositivo).</summary>
    internal void AdoptTrend(TagViewModel previous)
    {
        var first = TrendPoints.Count > 0 ? TrendPoints[0].Time : DateTimeOffset.MaxValue;
        TrendPoints.InsertRange(0, previous.TrendPoints.Where(p => p.Time <= first));
    }

    /// <summary>Los booleanos se escriben con dos botones (true/false); el resto, con un campo de texto.</summary>
    public bool ShowTextWrite => Writable && !IsBoolean;

    public bool ShowBoolWrite => Writable && IsBoolean;

    /// <summary>El valor actual es true (se marca su botón).</summary>
    public bool IsTrue => HasValue && string.Equals(ValueText, "true", StringComparison.OrdinalIgnoreCase);

    public bool IsFalse => HasValue && string.Equals(ValueText, "false", StringComparison.OrdinalIgnoreCase);

    private bool HasValue => Quality is Quality.Good or Quality.Uncertain;

    public void Apply(TagValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Quality = value.Quality;
        ValueText = value.Quality is Quality.Good or Quality.Uncertain ? value.Text : value.Error;
        DataType = value.DataType == Agent.Contracts.DataType.Unknown ? "" : value.DataType.ToString();
        Time = value.Timestamp?.ToDateTime().ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "";
        AddTrendPoint(value);
    }

    private void AddTrendPoint(TagValue value)
    {
        var time = value.Timestamp?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        double? number = value.Quality is not (Quality.Good or Quality.Uncertain) ? null : value.TypedCase switch
        {
            TagValue.TypedOneofCase.BoolValue => value.BoolValue ? 1 : 0,
            TagValue.TypedOneofCase.IntValue => value.IntValue,
            TagValue.TypedOneofCase.UintValue => value.UintValue,
            TagValue.TypedOneofCase.DoubleValue => value.DoubleValue,
            _ => null,
        };
        if (TrendPoints.Count > 0 && time < TrendPoints[^1].Time)
            time = TrendPoints[^1].Time;
        TrendPoints.Add(new ChartPoint(time, number));

        // Fuera lo anterior a la ventana (menos un punto: el valor que había al empezar) y el exceso de puntos.
        var cutoff = time - TrendWindow;
        var old = 0;
        while (old + 1 < TrendPoints.Count && TrendPoints[old + 1].Time < cutoff)
            old++;
        old = Math.Max(old, TrendPoints.Count - MaxTrendPoints);
        if (old > 0)
            TrendPoints.RemoveRange(0, old);
    }

    private bool CanWrite() => Writable && !string.IsNullOrEmpty(NewValue);

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private Task WriteAsync() => WriteValueAsync(NewValue, clearInput: true);

    /// <summary>Escribe true o false (botones de los booleanos).</summary>
    [RelayCommand]
    private Task WriteBoolAsync(string value) => Writable ? WriteValueAsync(value, clearInput: false) : Task.CompletedTask;

    private async Task WriteValueAsync(string value, bool clearInput)
    {
        if (_context.Client is not { } client)
            return;
        if (!await _context.Dialogs.ConfirmAsync("Escribir en el PLC", $"¿Escribir «{value}» en {Device}/{Name} ({Address})?\nActual: {ValueText}"))
            return;
        try
        {
            var reply = await client.WriteAsync(new WriteRequest { Device = Device, Tag = Name, Value = value, User = Environment.UserName });
            if (reply.Success)
            {
                if (clearInput)
                    NewValue = "";
                _context.Report($"{Device}/{Name} = {value}");
            }
            else
            {
                _context.Report($"No se escribió {Device}/{Name} ({reply.Quality}): {reply.Error}", isError: true);
            }
        }
        catch (RpcException ex)
        {
            _context.Report(_context.Describe(ex), isError: true);
        }
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (_context.Client is not { } client)
            return;
        if (!await _context.Dialogs.ConfirmAsync("Quitar tag", $"¿Quitar el tag {Name} ({Address}) de {Device}?"))
            return;
        try
        {
            var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = Device });
            foreach (var group in device.ScanGroups.ToArray())
            {
                foreach (var t in group.Tags.Where(t => (string.IsNullOrEmpty(t.Name) ? t.Address : t.Name) == Name).ToArray())
                    group.Tags.Remove(t);
                if (group.Tags.Count == 0)
                    device.ScanGroups.Remove(group);
            }
            await client.PutDeviceAsync(new PutDeviceRequest { Device = device, Persist = true });
            _context.Report($"Tag {Device}/{Name} quitado");
        }
        catch (RpcException ex)
        {
            _context.Report(_context.Describe(ex), isError: true);
        }
    }
}
