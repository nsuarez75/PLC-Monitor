using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.Agent.Contracts.Client;
using PlcMonitor.App.Charts;

namespace PlcMonitor.App.ViewModels;

/// <summary>
/// Ventana de detalle de un tag: su valor en vivo y su configuración (nombre, dirección, grupo de escaneo, escritura,
/// tipo). Al guardar se reescribe el dispositivo en el agente, que valida todo antes de aplicarlo.
/// </summary>
public sealed partial class TagDetailsViewModel : ObservableObject
{
    public const string AutomaticType = "Automático";

    /// <summary>Opción de la lista de transformaciones: el valor se muestra tal cual.</summary>
    public const string NoTransform = "Valor sin transformar";

    private readonly IAgentContext _context;
    private readonly string _originalName;
    private readonly Dictionary<string, int> _groupIntervals;

    /// <param name="isBoolean">Si un tag del dispositivo (por nombre) es booleano según sus valores en vivo.</param>
    /// <param name="transforms">Nombres de las transformaciones del agente.</param>
    public TagDetailsViewModel(IAgentContext context, TagViewModel tag, DeviceConfig device, bool historianEnabled = true, Func<string, bool>? isBoolean = null,
        IReadOnlyList<string>? transforms = null)
    {
        HistorianEnabled = historianEnabled;
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(device);
        _context = context;
        Tag = tag;
        Device = device.Name;
        WritesEnabled = device.WritesEnabled;
        _originalName = tag.Name;
        _groupIntervals = device.ScanGroups.ToDictionary(g => g.Name, g => g.IntervalMs, StringComparer.OrdinalIgnoreCase);
        Groups = [.. device.ScanGroups.Select(g => g.Name)];
        DataTypes = [AutomaticType, .. Enum.GetNames<DataType>().Where(n => n != nameof(Agent.Contracts.DataType.Unknown))];

        var (group, config) = Find(device, tag.Name);
        Name = string.IsNullOrEmpty(config?.Name) ? tag.Name : config.Name;
        Address = config?.Address ?? tag.Address;
        Writable = config?.Writable ?? tag.Writable;
        SelectedDataType = config is null || config.DataType == Agent.Contracts.DataType.Unknown ? AutomaticType : config.DataType.ToString();
        GroupName = group?.Name ?? "default";
        IntervalMs = group?.IntervalMs ?? 1000;
        Folder = config?.Folder ?? tag.Folder;
        Folders = [.. device.ScanGroups.SelectMany(g => g.Tags).Select(t => t.Folder).Where(f => f.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];
        Unit = config?.Unit ?? tag.Unit;
        var decimals = config is null ? tag.Decimals : config.GetDecimals();
        SelectedDecimals = decimals is { } d ? d.ToString(CultureInfo.InvariantCulture) : AllDecimals;
        _originalScaling = config is null ? tag.Scaling : config.Scaling;
        if (_originalScaling is { } scaling)
        {
            ScalingEnabled = true;
            ScaleRawMin = Number(scaling.RawMin);
            ScaleRawMax = Number(scaling.RawMax);
            ScaleEngMin = Number(scaling.EngMin);
            ScaleEngMax = Number(scaling.EngMax);
            ScaleClamp = scaling.Clamp;
        }
        var current = config?.Transform ?? tag.Transform;
        Transforms = [NoTransform, .. (transforms ?? []).Order(StringComparer.CurrentCultureIgnoreCase)];
        // Una transformación que ya no existe en el agente se sigue mostrando para no perderla al guardar sin querer.
        if (current.Length > 0 && !Transforms.Contains(current, StringComparer.OrdinalIgnoreCase))
            Transforms.Add(current);
        SelectedTransform = current.Length == 0 ? NoTransform : Transforms.First(t => string.Equals(t, current, StringComparison.OrdinalIgnoreCase));

        // Disparo: cualquier otro tag del dispositivo.
        var others = device.ScanGroups.SelectMany(g => g.Tags).Where(t => t != config).ToArray();
        _booleanTags = new HashSet<string>(
            others.Select(t => EffectiveName(t)).Where(n => isBoolean?.Invoke(n) == true)
                .Concat(others.Where(t => t.DataType == Agent.Contracts.DataType.Boolean).Select(EffectiveName)),
            StringComparer.Ordinal);
        TriggerTags = [.. others.Select(EffectiveName).Order(StringComparer.OrdinalIgnoreCase)];
        TriggerCondition = Conditions[0];
        if (config?.History is { } history)
        {
            HistoryEnabled = history.Enabled;
            HistoryMinIntervalMs = history.MinIntervalMs;
            HistoryMaxIntervalMs = history.MaxIntervalMs;
            HistoryDeadband = history.Deadband;
            HistoryDeadbandPercent = history.DeadbandPercent;
            if (history.Trigger is { } trigger)
            {
                TriggerEnabled = true;
                TriggerTag = trigger.Tag;
                TriggerCondition = Conditions.FirstOrDefault(c => c.Value == trigger.Condition) ?? Conditions[0];
                TriggerValue = trigger.Value;
                TriggerOnTrue = !string.Equals(trigger.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
                TriggerHoldMs = trigger.HoldMs;
            }
        }
    }

    private readonly HashSet<string> _booleanTags;

    private static string EffectiveName(TagConfig tag) => string.IsNullOrEmpty(tag.Name) ? tag.Address : tag.Name;

    public static IReadOnlyList<Choice<TriggerCondition>> Conditions { get; } =
    [
        new("=", Agent.Contracts.TriggerCondition.Equal),
        new("≠", Agent.Contracts.TriggerCondition.NotEqual),
        new(">", Agent.Contracts.TriggerCondition.Greater),
        new("≥", Agent.Contracts.TriggerCondition.GreaterOrEqual),
        new("<", Agent.Contracts.TriggerCondition.Less),
        new("≤", Agent.Contracts.TriggerCondition.LessOrEqual),
    ];

    /// <summary>Historizar solo mientras otro tag cumple una condición.</summary>
    [ObservableProperty]
    public partial bool TriggerEnabled { get; set; }

    /// <summary>Otros tags del dispositivo que pueden disparar.</summary>
    public IReadOnlyList<string> TriggerTags { get; }

    public bool HasTriggerTags => TriggerTags.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBooleanTrigger), nameof(IsValueTrigger))]
    public partial string? TriggerTag { get; set; }

    /// <summary>El tag disparo es booleano: se elige true o false; si no, condición y valor.</summary>
    public bool IsBooleanTrigger => TriggerTag is { } t && _booleanTags.Contains(t);

    public bool IsValueTrigger => !IsBooleanTrigger;

    [ObservableProperty]
    public partial Choice<TriggerCondition> TriggerCondition { get; set; }

    [ObservableProperty]
    public partial string TriggerValue { get; set; } = "";

    /// <summary>Disparo booleano: se historiza mientras vale true (o false).</summary>
    [ObservableProperty]
    public partial bool TriggerOnTrue { get; set; } = true;

    public static IReadOnlyList<Choice<bool>> BoolChoices { get; } = [new("true", true), new("false", false)];

    public Choice<bool> TriggerBool
    {
        get => BoolChoices[TriggerOnTrue ? 0 : 1];
        set
        {
            if (value is not null)
                TriggerOnTrue = value.Value;
        }
    }

    partial void OnTriggerOnTrueChanged(bool value) => OnPropertyChanged(nameof(TriggerBool));

    /// <summary>Tiempo que se sigue guardando después de que la condición deje de cumplirse.</summary>
    [ObservableProperty]
    public partial int TriggerHoldMs { get; set; }

    private HistoryTrigger? BuildTrigger() => TriggerEnabled
        ? new HistoryTrigger
        {
            Tag = TriggerTag ?? "",
            Condition = IsBooleanTrigger ? Agent.Contracts.TriggerCondition.Equal : TriggerCondition.Value,
            Value = IsBooleanTrigger ? (TriggerOnTrue ? "true" : "false") : TriggerValue.Trim(),
            HoldMs = TriggerHoldMs,
        }
        : null;

    /// <summary>El agente tiene el historiador activado.</summary>
    public bool HistorianEnabled { get; }

    [ObservableProperty]
    public partial bool HistoryEnabled { get; set; }

    /// <summary>Intervalo mínimo entre muestras (0 = cada cambio).</summary>
    [ObservableProperty]
    public partial int HistoryMinIntervalMs { get; set; }

    /// <summary>Tiempo máximo sin guardar (0 = nunca).</summary>
    [ObservableProperty]
    public partial int HistoryMaxIntervalMs { get; set; }

    [ObservableProperty]
    public partial double HistoryDeadband { get; set; }

    [ObservableProperty]
    public partial bool HistoryDeadbandPercent { get; set; }

    /// <summary>Tag en vivo (valor, calidad, hora).</summary>
    public TagViewModel Tag { get; }

    /// <summary>Ventana de la mini tendencia.</summary>
    public static readonly TimeSpan TrendWindow = TimeSpan.FromMinutes(5);

    /// <summary>Mini tendencia de los últimos minutos (los valores que ha recibido la app).</summary>
    public IReadOnlyList<ChartSeries> Trend => _trend ??= [new ChartSeries(Tag.Name, Tag.TrendPoints) { IsBoolean = Tag.IsBoolean, Display = Tag.SeriesFormat }];

    private IReadOnlyList<ChartSeries>? _trend;

    [ObservableProperty]
    public partial DateTimeOffset TrendFrom { get; private set; }

    [ObservableProperty]
    public partial DateTimeOffset TrendTo { get; private set; }

    [ObservableProperty]
    public partial int TrendRevision { get; private set; }

    /// <summary>Avanza la mini tendencia hasta <paramref name="now"/> (lo llama un temporizador de la ventana).</summary>
    public void RefreshTrend(DateTimeOffset now)
    {
        TrendFrom = now - TrendWindow;
        TrendTo = now;
        TrendRevision++;
    }

    public string Device { get; }

    /// <summary>Primera llave de escritura del dispositivo.</summary>
    public bool WritesEnabled { get; }

    public ObservableCollection<string> Groups { get; }

    /// <summary>Grupo de visualización (carpeta plegable de la tabla); vacío = sin grupo.</summary>
    [ObservableProperty]
    public partial string Folder { get; set; } = "";

    /// <summary>Grupos de visualización que ya usan otros tags del dispositivo.</summary>
    public IReadOnlyList<string> Folders { get; }

    /// <summary><see cref="NoTransform"/> y las transformaciones del agente.</summary>
    public ObservableCollection<string> Transforms { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransformPreview), nameof(DisplayPreview))]
    public partial string SelectedTransform { get; set; } = NoTransform;

    /// <summary>Los textos de la transformación elegida (p.ej. «0 = Parado · 1 = Marcha»).</summary>
    public string TransformPreview => SelectedTransform == NoTransform
        ? "Las transformaciones se crean en «Transformaciones», en la barra superior."
        : _context.FindTransform(SelectedTransform) is { } transform
            ? transform.Entries.Count == 0 ? "La transformación no tiene valores." : string.Join(" · ", transform.Entries.Select(e => $"{e.Value} = {e.Text}"))
            : "Esta transformación ya no existe en el agente.";

    public ObservableCollection<string> DataTypes { get; }

    /// <summary>Opción de decimales: el valor se muestra completo, como llega del PLC.</summary>
    public const string AllDecimals = "Todos";

    /// <summary>Máximo de decimales que admite el agente.</summary>
    public const int MaxDecimals = 9;

    public static IReadOnlyList<string> DecimalChoices { get; } =
        [AllDecimals, .. Enumerable.Range(0, MaxDecimals + 1).Select(n => n.ToString(CultureInfo.InvariantCulture))];

    /// <summary>Unidad con la que se muestra el valor (bar, °C...); vacío = ninguna.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayPreview), nameof(ScalingPreview))]
    public partial string Unit { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayPreview), nameof(ScalingPreview))]
    public partial string SelectedDecimals { get; set; } = AllDecimals;

    private int? Decimals => int.TryParse(SelectedDecimals, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>Cómo se verá el valor actual con la unidad y los decimales elegidos.</summary>
    public string DisplayPreview
    {
        get
        {
            const string note = "Los decimales se aplican a los reales. Solo cambia cómo se ve: el histórico guarda el valor completo.";
            if (SelectedTransform != NoTransform)
                return "Con transformación se ve su texto; la unidad y los decimales, en los valores que no tienen texto y en las gráficas.";
            if (Tag.IsBad || Tag.ValueText.Length == 0 || !Enum.TryParse<DataType>(Tag.DataType, out var type) || !ValueDisplay.IsNumeric(type))
                return note;
            return $"Se verá como {ValueDisplay.Format(Tag.ValueText, type, Decimals, Unit)}. {note}";
        }
    }

    private readonly Scaling? _originalScaling;

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>El agente convierte el valor bruto a unidades de ingeniería con dos pares de puntos.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScalingPreview))]
    public partial bool ScalingEnabled { get; set; }

    // Por defecto, una entrada analógica de un S7 (0–27648).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScalingPreview))]
    public partial string ScaleRawMin { get; set; } = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScalingPreview))]
    public partial string ScaleRawMax { get; set; } = "27648";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScalingPreview))]
    public partial string ScaleEngMin { get; set; } = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScalingPreview))]
    public partial string ScaleEngMax { get; set; } = "100";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScalingPreview))]
    public partial bool ScaleClamp { get; set; }

    /// <summary>Escalado de los campos; null si está desactivado. <paramref name="error"/> si algún campo no vale.</summary>
    private Scaling? BuildScaling(out string? error)
    {
        error = null;
        if (!ScalingEnabled)
            return null;
        static bool Parse(string text, out double value) =>
            double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        if (!Parse(ScaleRawMin, out var rawMin) || !Parse(ScaleRawMax, out var rawMax) || !Parse(ScaleEngMin, out var engMin) || !Parse(ScaleEngMax, out var engMax))
        {
            error = "Los cuatro puntos del escalado deben ser números.";
            return null;
        }
        if (rawMin == rawMax)
            error = "El bruto mínimo y el máximo no pueden ser iguales.";
        else if (engMin == engMax)
            error = "El mínimo y el máximo de ingeniería no pueden ser iguales.";
        return new Scaling { RawMin = rawMin, RawMax = rawMax, EngMin = engMin, EngMax = engMax, Clamp = ScaleClamp };
    }

    /// <summary>Qué hará el escalado con el valor actual, o por qué no vale.</summary>
    public string ScalingPreview
    {
        get
        {
            const string note = "Cambia los números: el histórico, las bandas muertas, los disparos y las escrituras usan el valor escalado.";
            var scaling = BuildScaling(out var error);
            if (error is not null)
                return error;
            if (scaling is null)
                return "El valor se usa tal como lo lee el PLC.";
            // El bruto actual: el que manda el agente si ya escala, o el valor tal cual si aún no.
            var rawText = Tag.HasRaw ? Tag.RawText : _originalScaling is null ? Tag.ValueText : "";
            if (Tag.IsBad || !double.TryParse(rawText, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
                return note;
            var engineering = (raw - scaling.RawMin) / (scaling.RawMax - scaling.RawMin) * (scaling.EngMax - scaling.EngMin) + scaling.EngMin;
            if (scaling.Clamp)
                engineering = Math.Clamp(engineering, Math.Min(scaling.EngMin, scaling.EngMax), Math.Max(scaling.EngMin, scaling.EngMax));
            return $"Bruto {rawText} → {ValueDisplay.Format(Number(engineering), Agent.Contracts.DataType.Double, Decimals, Unit)}. {note}";
        }
    }

    /// <summary>Se pone a true al guardar: la vista cierra la ventana.</summary>
    [ObservableProperty]
    public partial bool Saved { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Address { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupNote))]
    public partial string GroupName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupNote))]
    public partial int IntervalMs { get; set; }

    [ObservableProperty]
    public partial bool Writable { get; set; }

    [ObservableProperty]
    public partial string SelectedDataType { get; set; } = AutomaticType;

    [ObservableProperty]
    public partial string Errors { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Aviso cuando el periodo cambia un grupo que comparten otros tags, o cuando el grupo es nuevo.</summary>
    public string GroupNote
    {
        get
        {
            var name = GroupName.Trim();
            if (!_groupIntervals.TryGetValue(name, out var current))
                return $"Grupo nuevo: se creará con un periodo de {IntervalMs} ms.";
            return current == IntervalMs
                ? $"El grupo «{name}» se lee cada {current} ms."
                : $"Cambia el periodo de todo el grupo «{name}» (de {current} a {IntervalMs} ms).";
        }
    }

    /// <summary>Al elegir un grupo existente se muestra su periodo.</summary>
    partial void OnGroupNameChanged(string value)
    {
        if (_groupIntervals.TryGetValue(value.Trim(), out var interval))
            IntervalMs = interval;
    }

    /// <summary>
    /// Un agente anterior descarta sin dar error los campos que no conoce (la unidad y los decimales antes de 1.8, el
    /// escalado antes de 1.9): se avisa al releer el tag.
    /// </summary>
    private async Task WarnIfIgnoredAsync(AgentService.AgentServiceClient client, TagConfig saved, string name)
    {
        var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = Device });
        var (_, stored) = Find(device, name);
        if (stored is null)
            return;
        if (saved.Scaling is not null && stored.Scaling is null)
            _context.Report("El agente no aplica el escalado: actualízalo a la versión 1.9 o posterior.", isError: true);
        else if ((saved.Unit.Length > 0 || saved.HasDecimals) && stored.Unit.Length == 0 && !stored.HasDecimals)
            _context.Report("El agente no guarda la unidad ni los decimales: actualízalo a la versión 1.8 o posterior.", isError: true);
    }

    private bool CanSave() => !IsBusy && !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Address);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_context.Client is not { } client)
            return;
        var scaling = BuildScaling(out var scalingError);
        if (scalingError is not null)
        {
            Errors = scalingError;
            return;
        }
        // Lo ya guardado se queda con la escala anterior: las muestras viejas y las nuevas no se pueden comparar.
        if (!Equals(scaling, _originalScaling) && HistorianEnabled && (Tag.Historized || HistoryEnabled)
            && !await _context.Dialogs.ConfirmAsync("Escalado",
                $"El histórico de «{_originalName}» ya guardado seguirá con la escala anterior: las muestras viejas y las nuevas no serán comparables.\n¿Cambiar el escalado?"))
        {
            return;
        }
        IsBusy = true;
        Errors = "";
        try
        {
            // Se parte de la configuración actual del agente por si ha cambiado mientras la ventana estaba abierta.
            var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = Device });
            var (oldGroup, tag) = Find(device, _originalName);
            if (tag is null)
            {
                Errors = $"El tag «{_originalName}» ya no existe en {Device}.";
                return;
            }
            oldGroup!.Tags.Remove(tag);

            var groupName = string.IsNullOrWhiteSpace(GroupName) ? "default" : GroupName.Trim();
            var group = device.ScanGroups.FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
            if (group is null)
                device.ScanGroups.Add(group = new ScanGroupConfig { Name = groupName });
            group.IntervalMs = IntervalMs;
            var name = Name.Trim();
            var address = Address.Trim();
            var saved = new TagConfig
            {
                // Si el nombre es la dirección se guarda vacío (el agente usa la dirección).
                Name = name == address ? "" : name,
                Address = address,
                Writable = Writable,
                DataType = SelectedDataType == AutomaticType ? Agent.Contracts.DataType.Unknown : Enum.Parse<DataType>(SelectedDataType),
                History = HistoryEnabled || HistoryMinIntervalMs > 0 || HistoryMaxIntervalMs > 0 || HistoryDeadband > 0 || TriggerEnabled
                    ? new HistoryConfig
                    {
                        Enabled = HistoryEnabled,
                        MinIntervalMs = HistoryMinIntervalMs,
                        MaxIntervalMs = HistoryMaxIntervalMs,
                        Deadband = HistoryDeadband,
                        DeadbandPercent = HistoryDeadbandPercent,
                        Trigger = BuildTrigger(),
                    }
                    : null,
                Transform = SelectedTransform == NoTransform ? "" : SelectedTransform,
                Folder = Folder?.Trim() ?? "",
                Unit = Unit?.Trim() ?? "",
                Scaling = scaling,
            };
            if (Decimals is { } decimals)
                saved.Decimals = decimals;
            group.Tags.Add(saved);
            foreach (var empty in device.ScanGroups.Where(g => g.Tags.Count == 0).ToArray())
                device.ScanGroups.Remove(empty);
            // Los tags que historizan con este como disparo lo siguen usando con el nombre nuevo.
            var retargeted = 0;
            if (name != _originalName)
            {
                foreach (var other in device.ScanGroups.SelectMany(g => g.Tags).Where(t => t.History?.Trigger?.Tag == _originalName && EffectiveName(t) != name))
                {
                    other.History.Trigger.Tag = name;
                    retargeted++;
                }
            }

            await client.PutDeviceAsync(new PutDeviceRequest { Device = device, Persist = true });
            _context.Report($"Tag {Device}/{name} guardado" + retargeted switch
            {
                0 => "",
                1 => " (1 tag sigue disparándose con él)",
                _ => $" ({retargeted} tags siguen disparándose con él)",
            });
            if (saved.Unit.Length > 0 || saved.HasDecimals || saved.Scaling is not null)
                await WarnIfIgnoredAsync(client, saved, name);
            if (name != _originalName && HistorianEnabled && (Tag.Historized || HistoryEnabled))
            {
                await HistoryMover.OfferAsync(_context, client,
                    new HistoryRename { FromDevice = Device, FromTag = _originalName, ToDevice = Device, ToTag = name }, $"el tag «{_originalName}»", name);
            }
            Saved = true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        {
            Errors = ex.Status.Detail;
        }
        catch (RpcException ex)
        {
            Errors = _context.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static (ScanGroupConfig? Group, TagConfig? Tag) Find(DeviceConfig device, string tagName)
    {
        foreach (var group in device.ScanGroups)
        {
            var tag = group.Tags.FirstOrDefault(t => (string.IsNullOrEmpty(t.Name) ? t.Address : t.Name) == tagName);
            if (tag is not null)
                return (group, tag);
        }
        return (null, null);
    }
}
