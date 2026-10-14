using PlcMonitor.Core;

namespace PlcMonitor.Engine.Configuration;

/// <summary>Comprueba la configuración antes de arrancar el motor. No accede a los dispositivos.</summary>
public static class ConfigurationValidator
{
    /// <summary>Intervalo mínimo de un grupo de escaneo, para no saturar los PLC.</summary>
    public const int MinIntervalMs = 50;

    public static IReadOnlyList<string> Validate(AgentConfiguration configuration, DriverRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(registry);

        var errors = new List<string>();
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
            ValidateGroups(device, where, errors);
        }
        return errors;
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

    private static void ValidateGroups(DeviceConfiguration device, string where, List<string> errors)
    {
        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tagNames = new HashSet<string>(StringComparer.Ordinal);
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
            }
        }
    }
}
