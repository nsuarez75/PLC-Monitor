using PlcMonitor.Agent.Contracts;

namespace PlcMonitor.App.ViewModels;

/// <summary>Añade un tag a un dispositivo del agente (desde el explorador o por dirección) y guarda la configuración.</summary>
internal static class TagAdder
{
    /// <summary>Resultado: si se añadió y el mensaje para la barra de estado.</summary>
    public readonly record struct Result(bool Added, string Message);

    /// <param name="name">Nombre del tag; vacío = la dirección.</param>
    /// <param name="intervalMs">Periodo del grupo si hay que crearlo.</param>
    public static async Task<Result> AddAsync(AgentService.AgentServiceClient client, string device, string address, string? name, string groupName, int intervalMs, bool writable)
    {
        address = address.Trim();
        name = string.IsNullOrWhiteSpace(name) || name.Trim() == address ? null : name.Trim();
        var tagName = name ?? address;
        var config = await client.GetDeviceConfigAsync(new DeviceRequest { Device = device });
        if (config.ScanGroups.SelectMany(g => g.Tags).Any(t => (string.IsNullOrEmpty(t.Name) ? t.Address : t.Name) == tagName))
            return new Result(false, $"{tagName} ya es un tag de {device}");
        groupName = string.IsNullOrWhiteSpace(groupName) ? "default" : groupName.Trim();
        var group = config.ScanGroups.FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
        if (group is null)
            config.ScanGroups.Add(group = new ScanGroupConfig { Name = groupName, IntervalMs = intervalMs });
        group.Tags.Add(new TagConfig { Name = name ?? "", Address = address, Writable = writable });
        await client.PutDeviceAsync(new PutDeviceRequest { Device = config, Persist = true });
        return new Result(true, $"Añadido {tagName} a {device} (grupo {group.Name}, {group.IntervalMs} ms)");
    }
}
