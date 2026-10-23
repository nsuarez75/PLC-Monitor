using PlcMonitor.Agent.Contracts;
using PlcMonitor.App.Localization;

namespace PlcMonitor.App.ViewModels;

/// <summary>Añade tags a un dispositivo del agente (desde el explorador o por dirección) y guarda la configuración.</summary>
internal static class TagAdder
{
    /// <summary>Resultado: si se añadió y el mensaje para la barra de estado.</summary>
    public readonly record struct Result(bool Added, string Message);

    /// <param name="name">Nombre del tag; vacío = la dirección.</param>
    /// <param name="intervalMs">Periodo del grupo si hay que crearlo.</param>
    public static Task<Result> AddAsync(AgentService.AgentServiceClient client, string device, string address, string? name, string groupName, int intervalMs, bool writable) =>
        AddAsync(client, device, [(address, name)], groupName, intervalMs, writable);

    /// <summary>Añade varios tags de una vez (un solo guardado); los que ya existen se saltan.</summary>
    public static async Task<Result> AddAsync(
        AgentService.AgentServiceClient client, string device, IReadOnlyList<(string Address, string? Name)> tags, string groupName, int intervalMs, bool writable)
    {
        var config = await client.GetDeviceConfigAsync(new DeviceRequest { Device = device });
        var existing = config.ScanGroups.SelectMany(g => g.Tags).Select(t => string.IsNullOrEmpty(t.Name) ? t.Address : t.Name).ToHashSet(StringComparer.Ordinal);
        groupName = string.IsNullOrWhiteSpace(groupName) ? "default" : groupName.Trim();
        var group = config.ScanGroups.FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
        var added = new List<string>();
        var skipped = new List<string>();
        foreach (var (rawAddress, rawName) in tags)
        {
            var address = rawAddress.Trim();
            var name = string.IsNullOrWhiteSpace(rawName) || rawName.Trim() == address ? null : rawName.Trim();
            var tagName = name ?? address;
            if (!existing.Add(tagName))
            {
                skipped.Add(tagName);
                continue;
            }
            if (group is null)
                config.ScanGroups.Add(group = new ScanGroupConfig { Name = groupName, IntervalMs = intervalMs });
            group.Tags.Add(new TagConfig { Name = name ?? "", Address = address, Writable = writable });
            added.Add(tagName);
        }
        if (added.Count == 0)
        {
            return new Result(false, skipped.Count == 1
                ? Loc.F("{0} ya es un tag de {1}", skipped[0], device)
                : Loc.F("Los {0} tags ya existen en {1}", skipped.Count, device));
        }
        await client.PutDeviceAsync(new PutDeviceRequest { Device = config, Persist = true });
        var what = added.Count == 1 ? Loc.F("Añadido {0}", added[0]) : Loc.F("Añadidos {0} tags ({1} … {2})", added.Count, added[0], added[^1]);
        var message = Loc.F("{0} a {1} (grupo {2}, {3} ms)", what, device, group!.Name, group.IntervalMs);
        if (skipped.Count > 0)
            message += Loc.F("; {0} ya existían", skipped.Count);
        return new Result(true, message);
    }
}
