using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PlcMonitor.Abstractions;

/// <summary>Base para factorías con ajustes fuertemente tipados.</summary>
public abstract class DriverFactoryBase<TSettings> : IDriverFactory
    where TSettings : class, new()
{
    public abstract string TypeId { get; }

    public abstract string DisplayName { get; }

    public abstract DriverCapabilities Capabilities { get; }

    public Type SettingsType => typeof(TSettings);

    public JsonElement DefaultSettings() => JsonSerializer.SerializeToElement(new TSettings(), DriverJson.Options);

    public IReadOnlyList<string> ValidateSettings(JsonElement settings)
    {
        TSettings? parsed;
        try
        {
            parsed = settings.Deserialize<TSettings>(DriverJson.Options);
        }
        catch (JsonException ex)
        {
            return [$"Ajustes no válidos: {ex.Message}"];
        }
        return parsed is null ? ["Ajustes vacíos"] : Validate(parsed);
    }

    public IDeviceConnection CreateConnection(DeviceDefinition device, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        if (!string.Equals(device.DriverTypeId, TypeId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"El dispositivo '{device.Name}' usa el driver '{device.DriverTypeId}', no '{TypeId}'.", nameof(device));

        var errors = ValidateSettings(device.Settings);
        if (errors.Count > 0)
            throw new ArgumentException($"Ajustes no válidos para '{device.Name}': {string.Join("; ", errors)}", nameof(device));

        var settings = device.Settings.Deserialize<TSettings>(DriverJson.Options)!;
        return CreateConnection(device.Name, settings, loggerFactory);
    }

    protected abstract IReadOnlyList<string> Validate(TSettings settings);

    protected abstract IDeviceConnection CreateConnection(string deviceName, TSettings settings, ILoggerFactory loggerFactory);
}
