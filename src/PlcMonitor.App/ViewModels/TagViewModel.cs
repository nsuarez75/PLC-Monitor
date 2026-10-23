using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.Agent.Contracts.Client;
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
        IsDisabled = tag.Disabled;
        // Con el token de lectura el agente rechazaría la escritura: no se ofrece. Tampoco en un tag desactivado.
        Writable = tag.Writable && !context.ReadOnlyAccess && !IsDisabled;
        Historized = tag.Historized;
        Transform = tag.Transform;
        Folder = tag.Folder;
        Unit = tag.Unit;
        Decimals = tag.GetDecimals();
        Scaling = tag.Scaling;
        if (tag.Value is not null)
            Apply(tag.Value);
    }

    public string Device { get; }

    public string Name { get; }

    public string Address { get; }

    public string Group { get; }

    public bool Writable { get; }

    /// <summary>Tag desactivado en el agente: no se lee, ni se historiza, ni se publica en OPC UA.</summary>
    public bool IsDisabled { get; }

    /// <summary>El agente guarda este tag en el histórico.</summary>
    public bool Historized { get; }

    /// <summary>Transformación con la que se muestra el valor; vacío = ninguna.</summary>
    public string Transform { get; }

    /// <summary>Grupo de visualización (carpeta plegable de la tabla); vacío = sin grupo.</summary>
    public string Folder { get; }

    /// <summary>Unidad con la que se muestra el valor; vacío = ninguna.</summary>
    public string Unit { get; }

    /// <summary>Decimales con los que se muestran los reales; null = el valor completo.</summary>
    public int? Decimals { get; }

    /// <summary>Escalado que aplica el agente; null = el valor tal como lo lee el driver.</summary>
    public Scaling? Scaling { get; }

    /// <summary>Nombre con la unidad, para leyendas y cabeceras: «presion [bar]».</summary>
    public string Label => ValueDisplay.Label(Name, Unit);

    /// <summary>Unidad y decimales para las gráficas (los decimales, solo si el tag es real o aún no se sabe).</summary>
    public SeriesFormat SeriesFormat => new(Unit, _type is Agent.Contracts.DataType.Unknown || ValueDisplay.IsReal(_type) ? Decimals : null);

    /// <summary>Tipo del último valor recibido.</summary>
    private DataType _type;

    /// <summary>La tabla muestra el tag debajo de la cabecera de su grupo (va sangrado).</summary>
    [ObservableProperty]
    public partial bool IsInFolder { get; internal set; }

    /// <summary>Valor entero actual (booleanos como 0/1) para la transformación; null si no hay o no es entero.</summary>
    private long? _integer;

    /// <summary>
    /// Valor tal como se muestra: con transformación, «Marcha (1)»; si no, <see cref="ValueText"/> con los decimales y la
    /// unidad del tag («23.45 bar»).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    public partial string DisplayText { get; private set; } = "";

    /// <summary>Valor bruto del PLC en texto si el agente lo escala; vacío si no.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText), nameof(HasRaw))]
    public partial string RawText { get; private set; } = "";

    public bool HasRaw => RawText.Length > 0;

    /// <summary>Valor para la ventana de detalle: con el bruto si está escalado («5.00 bar (bruto 13824)»).</summary>
    public string DetailText => HasRaw ? $"{DisplayText} (bruto {RawText})" : DisplayText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrue), nameof(IsFalse))]
    public partial string ValueText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGood), nameof(IsUncertain), nameof(IsBad), nameof(IsTrue), nameof(IsFalse), nameof(QualityText))]
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

    /// <summary>Con error: calidad mala en un tag activo (uno desactivado no tiene valor, pero no es un error).</summary>
    public bool IsBad => !IsGood && !IsUncertain && !IsDisabled;

    /// <summary>Texto de la columna de calidad.</summary>
    public string QualityText => IsDisabled ? "Desactivado" : Quality.ToString();

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
        _type = value.DataType;
        RawText = value.Quality is Quality.Good or Quality.Uncertain ? value.RawText : "";
        DataType = value.DataType == Agent.Contracts.DataType.Unknown ? "" : value.DataType.ToString();
        Time = value.Timestamp?.ToDateTime().ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "";
        _integer = value.Quality is not (Quality.Good or Quality.Uncertain) ? null : value.TypedCase switch
        {
            TagValue.TypedOneofCase.IntValue => value.IntValue,
            TagValue.TypedOneofCase.UintValue when value.UintValue <= long.MaxValue => (long)value.UintValue,
            TagValue.TypedOneofCase.BoolValue => value.BoolValue ? 1 : 0,
            _ => null,
        };
        RefreshDisplay();
        AddTrendPoint(value);
    }

    /// <summary>Vuelve a calcular <see cref="DisplayText"/> (al recibir un valor o al cambiar las transformaciones del agente).</summary>
    public void RefreshDisplay()
    {
        var entry = _integer is { } n && Transform.Length > 0 ? _context.FindTransform(Transform)?.Entries.FirstOrDefault(e => e.Value == n) : null;
        // Un tag desactivado no tiene valor: la calidad ya dice por qué.
        DisplayText = IsDisabled ? "—" : entry is not null ? $"{entry.Text} ({ValueText})" : HasValue ? FormatText(ValueText) : ValueText;
    }

    /// <summary>Texto de un valor de este tag con sus decimales y su unidad.</summary>
    public string FormatText(string text) => ValueDisplay.Format(text, _type, Decimals, Unit);

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
}
