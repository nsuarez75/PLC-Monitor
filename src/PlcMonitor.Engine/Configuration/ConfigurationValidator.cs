using PlcMonitor.Core;

namespace PlcMonitor.Engine.Configuration;

/// <summary>Comprueba la configuración antes de arrancar el motor. No accede a los dispositivos.</summary>
public static class ConfigurationValidator
{
    /// <summary>Intervalo mínimo de un grupo de escaneo, para no saturar los PLC.</summary>
    public const int MinIntervalMs = 50;

    /// <summary>Decimales máximos con los que se puede mostrar un tag.</summary>
    public const int MaxDecimals = 9;

    public const int MaxUnitLength = 16;

    public static IReadOnlyList<string> Validate(AgentConfiguration configuration, DriverRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(registry);

        var errors = new List<string>();
        var historian = configuration.Historian ?? new HistorianSettings();
        if (historian.RetentionDays < 0)
            errors.Add("historian.retentionDays no puede ser negativo.");
        if (historian.FlushIntervalMs < 100)
            errors.Add("historian.flushIntervalMs debe ser al menos 100.");
        if (string.IsNullOrWhiteSpace(historian.Path))
            errors.Add("historian.path está vacío.");
        var transforms = ValidateTransforms(configuration.Transforms ?? [], errors);
        var deviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < configuration.Devices.Count; i++)
        {
            var device = configuration.Devices[i];
            var where = string.IsNullOrWhiteSpace(device.Name) ? $"Dispositivo #{i + 1}" : $"Dispositivo '{device.Name}'";

            if (string.IsNullOrWhiteSpace(device.Name))
                errors.Add($"{where}: falta el nombre.");
            else if (device.Name.Contains('/', StringComparison.Ordinal))
                errors.Add($"{where}: el nombre no puede contener '/'.");
            else if (!deviceNames.Add(device.Name))
                errors.Add($"{where}: nombre repetido.");

            if (!registry.TryGet(device.Driver, out var factory))
                errors.Add($"{where}: driver desconocido '{device.Driver}'.");
            else
                errors.AddRange(factory.ValidateSettings(device.Settings).Select(e => $"{where}: {e}"));

            ValidateReconnect(device.Reconnect, where, errors);
            ValidateGroups(device, where, transforms, errors);
        }
        return errors;
    }

    /// <summary>Comprueba las transformaciones y devuelve sus nombres.</summary>
    private static HashSet<string> ValidateTransforms(List<ValueTransform> transforms, List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < transforms.Count; i++)
        {
            var transform = transforms[i];
            var where = string.IsNullOrWhiteSpace(transform.Name) ? $"Transformación #{i + 1}" : $"Transformación '{transform.Name}'";
            if (string.IsNullOrWhiteSpace(transform.Name))
                errors.Add($"{where}: falta el nombre.");
            else if (!names.Add(transform.Name))
                errors.Add($"{where}: nombre repetido.");
            var values = new HashSet<long>();
            foreach (var entry in transform.Entries ?? [])
            {
                if (!values.Add(entry.Value))
                    errors.Add($"{where}: el valor {entry.Value} está repetido.");
                if (string.IsNullOrWhiteSpace(entry.Text))
                    errors.Add($"{where}: falta el texto del valor {entry.Value}.");
            }
        }
        return names;
    }

    private static void ValidateReconnect(ReconnectSettings? reconnect, string where, List<string> errors)
    {
        if (reconnect is null)
        {
            errors.Add($"{where}: falta la sección reconnect.");
            return;
        }
        if (reconnect.InitialDelayMs <= 0)
            errors.Add($"{where}: reconnect.initialDelayMs debe ser mayor que 0.");
        if (reconnect.MaxDelayMs < reconnect.InitialDelayMs)
            errors.Add($"{where}: reconnect.maxDelayMs no puede ser menor que initialDelayMs.");
        if (reconnect.Multiplier < 1)
            errors.Add($"{where}: reconnect.multiplier debe ser 1 o mayor.");
    }

    private static void ValidateGroups(DeviceConfiguration device, string where, HashSet<string> transforms, List<string> errors)
    {
        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tagNames = new HashSet<string>(StringComparer.Ordinal);
        var triggers = new List<(string Where, string Tag, HistoryTrigger Trigger)>();
        foreach (var group in device.ScanGroups)
        {
            var groupWhere = $"{where}, grupo '{group.Name}'";
            if (string.IsNullOrWhiteSpace(group.Name))
                errors.Add($"{where}: hay un grupo sin nombre.");
            else if (!groupNames.Add(group.Name))
                errors.Add($"{groupWhere}: nombre de grupo repetido.");
            if (group.IntervalMs < MinIntervalMs)
                errors.Add($"{groupWhere}: intervalMs debe ser al menos {MinIntervalMs}.");

            foreach (var tag in group.Tags)
            {
                if (string.IsNullOrWhiteSpace(tag.Address))
                {
                    errors.Add($"{groupWhere}: hay un tag sin dirección.");
                    continue;
                }
                if (!tagNames.Add(tag.EffectiveName))
                    errors.Add($"{groupWhere}: tag '{tag.EffectiveName}' repetido en el dispositivo.");
                if (!string.IsNullOrWhiteSpace(tag.Transform) && !transforms.Contains(tag.Transform))
                    errors.Add($"{groupWhere}, tag '{tag.EffectiveName}': la transformación '{tag.Transform}' no existe.");
                if (tag.Decimals is < 0 or > MaxDecimals)
                    errors.Add($"{groupWhere}, tag '{tag.EffectiveName}': los decimales deben estar entre 0 y {MaxDecimals}.");
                if (tag.Unit is { Length: > MaxUnitLength })
                    errors.Add($"{groupWhere}, tag '{tag.EffectiveName}': la unidad no puede tener más de {MaxUnitLength} caracteres.");
                if (tag.History is { } h)
                {
                    var tagWhere = $"{groupWhere}, tag '{tag.EffectiveName}'";
                    if (h.MinIntervalMs < 0 || h.MaxIntervalMs < 0)
                        errors.Add($"{tagWhere}: los intervalos del histórico no pueden ser negativos.");
                    if (h.MaxIntervalMs > 0 && h.MaxIntervalMs < h.MinIntervalMs)
                        errors.Add($"{tagWhere}: history.maxIntervalMs no puede ser menor que minIntervalMs.");
                    if (h.Deadband < 0)
                        errors.Add($"{tagWhere}: la banda muerta no puede ser negativa.");
                    if (h.Trigger is { } trigger)
                        triggers.Add((tagWhere, tag.EffectiveName, trigger));
                }
            }
        }
        // Los disparos se comprueban con todos los tags del dispositivo ya vistos (pueden estar en otro grupo).
        foreach (var (tagWhere, tag, trigger) in triggers)
        {
            if (string.IsNullOrWhiteSpace(trigger.Tag))
                errors.Add($"{tagWhere}: falta el tag que dispara el histórico.");
            else if (trigger.Tag == tag)
                errors.Add($"{tagWhere}: un tag no puede dispararse a sí mismo.");
            else if (!tagNames.Contains(trigger.Tag))
                errors.Add($"{tagWhere}: el tag de disparo '{trigger.Tag}' no existe en el dispositivo.");
            if (trigger.HoldMs < 0)
                errors.Add($"{tagWhere}: el tiempo tras el disparo no puede ser negativo.");
            if (string.IsNullOrWhiteSpace(trigger.Value))
                errors.Add($"{tagWhere}: falta el valor del disparo.");
            else if (trigger.Condition is not (TriggerCondition.Equal or TriggerCondition.NotEqual) && !HistoryTrigger.TryNumber(trigger.Value.Trim(), out _))
                errors.Add($"{tagWhere}: el valor del disparo debe ser un número para comparar con >, ≥, < o ≤.");
        }
    }
}
