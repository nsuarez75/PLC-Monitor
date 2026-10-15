using System.ComponentModel;
using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using PlcMonitor.Agent.Contracts;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PlcMonitor.Cli;

/// <summary>Opciones comunes de las órdenes <c>plcmon agent ...</c>: a qué agente conectarse.</summary>
public class AgentSettings : CommandSettings
{
    public const string AgentEnvironmentVariable = "PLCMON_AGENT";
    public const string DefaultAgentUrl = "http://127.0.0.1:5310";

    [CommandOption("-a|--agent <URL>")]
    [Description("URL del agente. Si se omite: variable PLCMON_AGENT o http://127.0.0.1:5310.")]
    public string? Agent { get; set; }

    /// <summary>URL efectiva del agente.</summary>
    public string ResolveAgentUrl(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        return Agent is { Length: > 0 } url ? url : environment(AgentEnvironmentVariable) is { Length: > 0 } env ? env : DefaultAgentUrl;
    }

    public override ValidationResult Validate() =>
        Uri.TryCreate(ResolveAgentUrl(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? ValidationResult.Success()
            : ValidationResult.Error($"URL de agente no válida: '{ResolveAgentUrl()}'. Ejemplo: http://192.168.0.5:5310");
}

/// <summary>Canal gRPC y consola usados por las órdenes del agente (sustituibles en los tests).</summary>
public static class AgentRuntime
{
    public static Func<string, GrpcChannel> ChannelFactory { get; set; } = url => GrpcChannel.ForAddress(url);

    public static IAnsiConsole Console { get; set; } = AnsiConsole.Console;

    /// <summary>Ejecuta <paramref name="action"/> con un cliente del agente y traduce los errores gRPC a mensajes legibles.</summary>
    public static async Task<int> RunAsync(AgentSettings settings, Func<AgentService.AgentServiceClient, Task<int>> action)
    {
        var url = settings.ResolveAgentUrl();
        using var channel = ChannelFactory(url);
        try
        {
            return await action(new AgentService.AgentServiceClient(channel));
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            return 0;
        }
        catch (RpcException ex)
        {
            Console.MarkupLine($"[red]Error:[/] {Markup.Escape(Describe(ex, url))}");
            return 1;
        }
    }

    public static string Describe(RpcException ex, string url) => ex.StatusCode switch
    {
        StatusCode.Unavailable => $"No se puede conectar con el agente en {url}. ¿Está arrancado? ({ex.Status.Detail})",
        StatusCode.DeadlineExceeded => $"El agente en {url} no responde a tiempo.",
        StatusCode.Unimplemented => $"{url} no es un agente de PLC-Monitor (o es de otra versión).",
        _ => ex.Status.Detail is { Length: > 0 } detail ? detail : ex.Status.StatusCode.ToString(),
    };

    public static string StateMarkup(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "[green]Connected[/]",
        ConnectionState.Connecting => "[yellow]Connecting[/]",
        ConnectionState.Faulted => "[red]Faulted[/]",
        _ => "[grey]Disconnected[/]",
    };

    public static string QualityMarkup(Quality quality) => quality switch
    {
        Quality.Good => "[green]Good[/]",
        Quality.Uncertain => "[yellow]Uncertain[/]",
        _ => $"[red]{quality}[/]",
    };

    /// <summary>Texto del valor, o el error si la calidad es mala.</summary>
    public static string ValueText(TagValue? value) =>
        value is null ? string.Empty : value.Quality is Quality.Good or Quality.Uncertain ? value.Text : value.Error;

    public static string Time(Timestamp? timestamp) =>
        timestamp is null ? string.Empty : timestamp.ToDateTime().ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public static string DateTimeText(Timestamp? timestamp) =>
        timestamp is null ? string.Empty : timestamp.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>plcmon agent info — versión, máquina y drivers del agente.</summary>
public sealed class AgentInfoCommand : AsyncCommand<AgentSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, AgentSettings settings, CancellationToken cancellationToken) =>
        AgentRuntime.RunAsync(settings, async client =>
        {
            var info = await client.GetInfoAsync(new Empty(), cancellationToken: cancellationToken);
            var table = new Table().AddColumns("Propiedad", "Valor");
            table.AddRow("Agente", Markup.Escape(settings.ResolveAgentUrl()));
            table.AddRow("Versión", Markup.Escape(info.Version));
            table.AddRow("Máquina", Markup.Escape(info.Machine));
            table.AddRow("Sistema", Markup.Escape(info.Os));
            table.AddRow("Arrancado", AgentRuntime.DateTimeText(info.Started));
            table.AddRow("Configuración", Markup.Escape(info.ConfigPath));
            table.AddRow("Drivers", Markup.Escape(string.Join(", ", info.Drivers.Select(d => d.Id))));
            AgentRuntime.Console.Write(table);
            return 0;
        });
}

/// <summary>plcmon agent devices — dispositivos configurados y su estado.</summary>
public sealed class AgentDevicesCommand : AsyncCommand<AgentSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, AgentSettings settings, CancellationToken cancellationToken) =>
        AgentRuntime.RunAsync(settings, async client =>
        {
            var devices = await client.ListDevicesAsync(new Empty(), cancellationToken: cancellationToken);
            var table = new Table().AddColumns("Dispositivo", "Driver", "Activo", "Escritura", "Estado", "Desde", "Tags", "Detalle");
            foreach (var d in devices.Devices)
            {
                var detail = d.Detail;
                if (d.NextRetry is not null)
                    detail = $"{detail} (reintento {AgentRuntime.Time(d.NextRetry)})".Trim();
                table.AddRow(
                    Markup.Escape(d.Name),
                    Markup.Escape(d.Driver),
                    d.Enabled ? "sí" : "[grey]no[/]",
                    d.WritesEnabled ? "[yellow]sí[/]" : "no",
                    AgentRuntime.StateMarkup(d.State),
                    AgentRuntime.DateTimeText(d.Since),
                    d.TagCount.ToString(CultureInfo.InvariantCulture),
                    Markup.Escape(detail));
            }
            AgentRuntime.Console.Write(table);
            return 0;
        });
}

public sealed class AgentTagsSettings : AgentSettings
{
    [CommandArgument(0, "[DISPOSITIVO]")]
    [Description("Solo los tags de este dispositivo.")]
    public string? Device { get; set; }
}

/// <summary>plcmon agent tags — tags configurados con su último valor.</summary>
public sealed class AgentTagsCommand : AsyncCommand<AgentTagsSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, AgentTagsSettings settings, CancellationToken cancellationToken) =>
        AgentRuntime.RunAsync(settings, async client =>
        {
            var tags = await client.ListTagsAsync(new ListTagsRequest { Device = settings.Device ?? string.Empty }, cancellationToken: cancellationToken);
            var table = new Table().AddColumns("Dispositivo", "Tag", "Dirección", "Valor", "Tipo", "Calidad", "Hora", "Grupo");
            foreach (var t in tags.Tags)
            {
                table.AddRow(
                    Markup.Escape(t.Device),
                    Markup.Escape(t.Name) + (t.Writable ? " [yellow]✎[/]" : string.Empty),
                    Markup.Escape(t.Address),
                    Markup.Escape(AgentRuntime.ValueText(t.Value)),
                    t.Value?.DataType.ToString() ?? string.Empty,
                    t.Value is null ? string.Empty : AgentRuntime.QualityMarkup(t.Value.Quality),
                    AgentRuntime.Time(t.Value?.Timestamp),
                    Markup.Escape($"{t.Group} ({t.IntervalMs} ms)"));
            }
            AgentRuntime.Console.Write(table);
            AgentRuntime.Console.MarkupLine($"[grey]{tags.Tags.Count} tags · ✎ = escribible[/]");
            return 0;
        });
}

public sealed class AgentReadSettings : AgentSettings
{
    [CommandArgument(0, "<DISPOSITIVO>")]
    [Description("Dispositivo configurado en el agente.")]
    public string Device { get; set; } = "";

    [CommandArgument(1, "<DIRECCION>")]
    [Description("Una o varias direcciones en la sintaxis del driver (no hace falta que estén configuradas).")]
    public string[] Addresses { get; set; } = [];
}

/// <summary>plcmon agent read — lectura bajo demanda a través del agente.</summary>
public sealed class AgentReadCommand : AsyncCommand<AgentReadSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, AgentReadSettings settings, CancellationToken cancellationToken) =>
        AgentRuntime.RunAsync(settings, async client =>
        {
            var request = new ReadRequest { Device = settings.Device };
            request.Addresses.AddRange(settings.Addresses);
            var reply = await client.ReadAsync(request, cancellationToken: cancellationToken);
            var table = new Table().AddColumns("Dirección", "Valor", "Tipo", "Calidad");
            for (var i = 0; i < reply.Values.Count; i++)
            {
                var v = reply.Values[i];
                table.AddRow(
                    Markup.Escape(i < settings.Addresses.Length ? settings.Addresses[i] : "?"),
                    Markup.Escape(AgentRuntime.ValueText(v)),
                    v.DataType.ToString(),
                    AgentRuntime.QualityMarkup(v.Quality));
            }
            AgentRuntime.Console.Write(table);
            return reply.Values.All(v => v.Quality == Quality.Good) ? 0 : 1;
        });
}

public sealed class AgentWriteSettings : AgentSettings
{
    [CommandArgument(0, "<DISPOSITIVO>")]
    [Description("Dispositivo configurado en el agente.")]
    public string Device { get; set; } = "";

    [CommandArgument(1, "<TAG>")]
    [Description("Nombre del tag configurado (debe ser escribible).")]
    public string Tag { get; set; } = "";

    [CommandArgument(2, "<VALOR>")]
    [Description("Valor en texto: punto decimal, true/false o 1/0.")]
    public string Value { get; set; } = "";

    [CommandOption("-y|--yes")]
    [Description("No pedir confirmación.")]
    public bool Yes { get; set; }

    [CommandOption("--user <USUARIO>")]
    [Description("Usuario para el log de auditoría del agente. Por defecto, el usuario del sistema.")]
    public string? User { get; set; }
}

/// <summary>plcmon agent write — escribe un tag configurado a través del agente (doble llave y auditoría en el agente).</summary>
public sealed class AgentWriteCommand : AsyncCommand<AgentWriteSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, AgentWriteSettings settings, CancellationToken cancellationToken) =>
        AgentRuntime.RunAsync(settings, async client =>
        {
            if (!settings.Yes && !await AgentRuntime.Console.ConfirmAsync(
                    $"Escribir [bold]{Markup.Escape(settings.Value)}[/] en [bold]{Markup.Escape(settings.Device)}/{Markup.Escape(settings.Tag)}[/]?",
                    defaultValue: false, cancellationToken))
            {
                AgentRuntime.Console.MarkupLine("[grey]Cancelado.[/]");
                return 2;
            }

            var reply = await client.WriteAsync(new WriteRequest
            {
                Device = settings.Device,
                Tag = settings.Tag,
                Value = settings.Value,
                User = settings.User ?? Environment.UserName,
            }, cancellationToken: cancellationToken);

            if (reply.Success)
            {
                AgentRuntime.Console.MarkupLine("[green]Escrito.[/]");
                return 0;
            }
            AgentRuntime.Console.MarkupLine($"[red]No se escribió[/] ({AgentRuntime.QualityMarkup(reply.Quality)}): {Markup.Escape(reply.Error)}");
            return 1;
        });
}

public sealed class AgentEnableSettings : AgentSettings
{
    [CommandArgument(0, "<DISPOSITIVO>")]
    [Description("Dispositivo configurado en el agente.")]
    public string Device { get; set; } = "";

    [CommandOption("--persist")]
    [Description("Guarda el cambio en la configuración del agente (agent.json).")]
    public bool Persist { get; set; }
}

/// <summary>plcmon agent enable/disable — activa o desactiva un dispositivo en caliente.</summary>
public abstract class AgentSetEnabledCommand(bool enabled) : AsyncCommand<AgentEnableSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, AgentEnableSettings settings, CancellationToken cancellationToken) =>
        AgentRuntime.RunAsync(settings, async client =>
        {
            var device = await client.SetDeviceEnabledAsync(
                new SetDeviceEnabledRequest { Device = settings.Device, Enabled = enabled, Persist = settings.Persist },
                cancellationToken: cancellationToken);
            AgentRuntime.Console.MarkupLine(
                $"{Markup.Escape(device.Name)}: {(device.Enabled ? "activado" : "desactivado")}{(settings.Persist ? " y guardado" : " (hasta reiniciar el agente)")} · {AgentRuntime.StateMarkup(device.State)}");
            return 0;
        });
}

public sealed class AgentEnableCommand() : AgentSetEnabledCommand(true);

public sealed class AgentDisableCommand() : AgentSetEnabledCommand(false);

public sealed class AgentWatchSettings : AgentSettings
{
    [CommandArgument(0, "[DISPOSITIVO]")]
    [Description("Dispositivos a vigilar. Si se omite, todos.")]
    public string[] Devices { get; set; } = [];
}

/// <summary>plcmon agent watch — se suscribe al agente y muestra los cambios en directo (Ctrl+C para salir).</summary>
public sealed class AgentWatchCommand : AsyncCommand<AgentWatchSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, AgentWatchSettings settings, CancellationToken cancellationToken) =>
        AgentRuntime.RunAsync(settings, async client =>
        {
            var console = AgentRuntime.Console;
            var request = new SubscribeRequest();
            request.Devices.AddRange(settings.Devices);
            while (!cancellationToken.IsCancellationRequested)
            {
                using var call = client.Subscribe(request, cancellationToken: cancellationToken);
                try
                {
                    await foreach (var ev in call.ResponseStream.ReadAllAsync(cancellationToken))
                        Print(console, ev);
                    console.MarkupLine("[yellow]El agente cerró la suscripción.[/]");
                    return 0;
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.ResourceExhausted)
                {
                    // Este cliente no consumió a tiempo: se vuelve a suscribir y llega una foto nueva.
                    console.MarkupLine($"[yellow]Suscripción perdida ({Markup.Escape(ex.Status.Detail)}). Volviendo a suscribir…[/]");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            return 0;
        });

    internal static void Print(IAnsiConsole console, SubscribeEvent ev)
    {
        switch (ev.EventCase)
        {
            case SubscribeEvent.EventOneofCase.Snapshot:
                foreach (var d in ev.Snapshot.Devices)
                    console.MarkupLine($"[grey]{AgentRuntime.Time(d.Since)}[/]  [bold]{Markup.Escape(d.Name)}[/] {AgentRuntime.StateMarkup(d.State)} {Markup.Escape(d.Detail)}");
                foreach (var t in ev.Snapshot.Tags)
                    PrintValue(console, t.Device, t.Name, t.Value);
                console.MarkupLine($"[grey]— {ev.Snapshot.Devices.Count} dispositivos, {ev.Snapshot.Tags.Count} tags. Cambios en directo (Ctrl+C para salir) —[/]");
                break;
            case SubscribeEvent.EventOneofCase.TagUpdates:
                foreach (var u in ev.TagUpdates.Updates)
                    PrintValue(console, u.Device, u.Tag, u.Value);
                break;
            case SubscribeEvent.EventOneofCase.DeviceStatus:
                var s = ev.DeviceStatus;
                console.MarkupLine($"[grey]{AgentRuntime.Time(s.Since)}[/]  [bold]{Markup.Escape(s.Name)}[/] {AgentRuntime.StateMarkup(s.State)} {Markup.Escape(s.Detail)}");
                break;
            default:
                break;
        }
    }

    private static void PrintValue(IAnsiConsole console, string device, string tag, TagValue? value) =>
        console.MarkupLine(
            $"[grey]{AgentRuntime.Time(value?.Timestamp)}[/]  {Markup.Escape(device)}/{Markup.Escape(tag)} = {Markup.Escape(AgentRuntime.ValueText(value))}  {(value is null ? string.Empty : AgentRuntime.QualityMarkup(value.Quality))}");
}

public sealed class AgentBrowseSettings : AgentSettings
{
    [CommandArgument(0, "<DISPOSITIVO>")]
    [Description("Dispositivo configurado en el agente (debe estar conectado).")]
    public string Device { get; set; } = "";

    [CommandArgument(1, "[FILTRO]")]
    [Description("Texto que debe contener el nombre (sin distinguir mayúsculas).")]
    public string? Filter { get; set; }

    [CommandOption("-n|--max <N>")]
    [Description("Máximo de resultados.")]
    [DefaultValue(100)]
    public int MaxResults { get; set; } = 100;

    [CommandOption("--refresh")]
    [Description("Vuelve a explorar la CPU en lugar de usar la caché del agente.")]
    public bool Refresh { get; set; }
}

/// <summary>plcmon agent browse — explora las variables de un dispositivo a través del agente.</summary>
public sealed class AgentBrowseCommand : AsyncCommand<AgentBrowseSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, AgentBrowseSettings settings, CancellationToken cancellationToken) =>
        AgentRuntime.RunAsync(settings, async client =>
        {
            var reply = await client.BrowseAsync(new BrowseRequest
            {
                Device = settings.Device,
                Filter = settings.Filter ?? string.Empty,
                MaxResults = settings.MaxResults,
                Refresh = settings.Refresh,
            }, cancellationToken: cancellationToken);
            var table = new Table().AddColumns("Símbolo", "Tipo PLC", "Tipo");
            foreach (var item in reply.Items)
                table.AddRow(Markup.Escape(item.Address), Markup.Escape(item.NativeType), item.DataType.ToString());
            AgentRuntime.Console.Write(table);
            AgentRuntime.Console.MarkupLine($"[grey]{reply.Items.Count} mostrados de {reply.TotalMatches} coincidencias · {reply.TotalSymbols} símbolos[/]");
            return 0;
        });
}

/// <summary>plcmon agent config — configuración del agente en árbol (dispositivos, grupos y tags).</summary>
public sealed class AgentConfigCommand : AsyncCommand<AgentTagsSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, AgentTagsSettings settings, CancellationToken cancellationToken) =>
        AgentRuntime.RunAsync(settings, async client =>
        {
            IEnumerable<DeviceConfig> devices = string.IsNullOrEmpty(settings.Device)
                ? (await client.GetConfigurationAsync(new Empty(), cancellationToken: cancellationToken)).Devices
                : [await client.GetDeviceConfigAsync(new DeviceRequest { Device = settings.Device }, cancellationToken: cancellationToken)];
            var root = new Tree($"[bold]{Markup.Escape(settings.ResolveAgentUrl())}[/]");
            foreach (var d in devices)
            {
                var flags = $"{(d.Enabled ? "activo" : "[grey]inactivo[/]")}{(d.WritesEnabled ? ", [yellow]escritura[/]" : string.Empty)}";
                var node = root.AddNode($"[bold]{Markup.Escape(d.Name)}[/] ({Markup.Escape(d.Driver)}, {flags}) [grey]{Markup.Escape(d.SettingsJson)}[/]");
                foreach (var g in d.ScanGroups)
                {
                    var group = node.AddNode($"{Markup.Escape(g.Name)} [grey]({g.IntervalMs} ms)[/]");
                    foreach (var t in g.Tags)
                    {
                        var name = string.IsNullOrEmpty(t.Name) ? t.Address : t.Name;
                        var type = t.DataType == DataType.Unknown ? string.Empty : $" [grey]{t.DataType}[/]";
                        group.AddNode($"{Markup.Escape(name)} = {Markup.Escape(t.Address)}{type}{(t.Writable ? " [yellow]✎[/]" : string.Empty)}");
                    }
                }
            }
            AgentRuntime.Console.Write(root);
            return 0;
        });
}

public class AgentPersistSettings : AgentSettings
{
    [CommandOption("--no-persist")]
    [Description("No guardar el cambio en agent.json (se pierde al reiniciar el agente).")]
    public bool NoPersist { get; set; }
}

public sealed class AgentDeviceAddSettings : AgentPersistSettings
{
    [CommandArgument(0, "<NOMBRE>")]
    [Description("Nombre del dispositivo (único, sin '/').")]
    public string Name { get; set; } = "";

    [CommandOption("-d|--driver <DRIVER>")]
    [Description("Driver: s7-symbolic o s7-absolute.")]
    public string Driver { get; set; } = "";

    [CommandOption("-s|--settings <JSON>")]
    [Description("Ajustes del driver en JSON, p.ej. '{\"host\":\"192.168.0.10\",\"security\":\"auto\"}'.")]
    public string Settings { get; set; } = "{}";

    [CommandOption("--disabled")]
    [Description("Crear el dispositivo sin conectar.")]
    public bool Disabled { get; set; }

    [CommandOption("--writes")]
    [Description("Habilitar escrituras (primera llave).")]
    public bool WritesEnabled { get; set; }

    public override ValidationResult Validate() =>
        string.IsNullOrWhiteSpace(Driver) ? ValidationResult.Error("Falta --driver.") : base.Validate();
}

/// <summary>plcmon agent device add — da de alta un dispositivo sin tags (o reemplaza sus ajustes si ya existe).</summary>
public sealed class AgentDeviceAddCommand : AsyncCommand<AgentDeviceAddSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, AgentDeviceAddSettings settings, CancellationToken cancellationToken) =>
        AgentRuntime.RunAsync(settings, async client =>
        {
            DeviceConfig device;
            try
            {
                device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = settings.Name }, cancellationToken: cancellationToken);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                device = new DeviceConfig { Name = settings.Name, Reconnect = new ReconnectConfig { InitialDelayMs = 1000, MaxDelayMs = 30000, Multiplier = 2 } };
            }
            device.Driver = settings.Driver;
            device.SettingsJson = settings.Settings;
            device.Enabled = !settings.Disabled;
            device.WritesEnabled = settings.WritesEnabled;
            var status = await client.PutDeviceAsync(new PutDeviceRequest { Device = device, Persist = !settings.NoPersist }, cancellationToken: cancellationToken);
            AgentRuntime.Console.MarkupLine($"[green]Guardado[/] {Markup.Escape(status.Name)} · {AgentRuntime.StateMarkup(status.State)}");
            return 0;
        });
}

public sealed class AgentDeviceRemoveSettings : AgentPersistSettings
{
    [CommandArgument(0, "<DISPOSITIVO>")]
    public string Device { get; set; } = "";

    [CommandOption("-y|--yes")]
    [Description("No pedir confirmación.")]
    public bool Yes { get; set; }
}

/// <summary>plcmon agent device remove — da de baja un dispositivo.</summary>
public sealed class AgentDeviceRemoveCommand : AsyncCommand<AgentDeviceRemoveSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, AgentDeviceRemoveSettings settings, CancellationToken cancellationToken) =>
        AgentRuntime.RunAsync(settings, async client =>
        {
            if (!settings.Yes && !await AgentRuntime.Console.ConfirmAsync(
                    $"¿Dar de baja [bold]{Markup.Escape(settings.Device)}[/] y todos sus tags?", defaultValue: false, cancellationToken))
            {
                AgentRuntime.Console.MarkupLine("[grey]Cancelado.[/]");
                return 2;
            }
            await client.DeleteDeviceAsync(new DeleteDeviceRequest { Device = settings.Device, Persist = !settings.NoPersist }, cancellationToken: cancellationToken);
            AgentRuntime.Console.MarkupLine($"[green]Eliminado[/] {Markup.Escape(settings.Device)}");
            return 0;
        });
}

public sealed class AgentTagAddSettings : AgentPersistSettings
{
    [CommandArgument(0, "<DISPOSITIVO>")]
    public string Device { get; set; } = "";

    [CommandArgument(1, "<DIRECCION>")]
    [Description("Dirección en la sintaxis del driver.")]
    public string Address { get; set; } = "";

    [CommandOption("--name <NOMBRE>")]
    [Description("Nombre del tag. Por defecto, la dirección.")]
    public string? Name { get; set; }

    [CommandOption("-g|--group <GRUPO>")]
    [Description("Grupo de escaneo. Se crea si no existe.")]
    [DefaultValue("default")]
    public string Group { get; set; } = "default";

    [CommandOption("-i|--interval <MS>")]
    [Description("Periodo del grupo si se crea nuevo.")]
    [DefaultValue(1000)]
    public int IntervalMs { get; set; } = 1000;

    [CommandOption("--writable")]
    [Description("Tag escribible (segunda llave).")]
    public bool Writable { get; set; }

    [CommandOption("--type <TIPO>")]
    [Description("Tipo para interpretar las escrituras si el driver no lo deduce (Boolean, Int16, Float...).")]
    public string? Type { get; set; }
}

/// <summary>plcmon agent tag add — añade (o sustituye) un tag en un grupo de escaneo del dispositivo.</summary>
public sealed class AgentTagAddCommand : AsyncCommand<AgentTagAddSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, AgentTagAddSettings settings, CancellationToken cancellationToken) =>
        AgentRuntime.RunAsync(settings, async client =>
        {
            var dataType = DataType.Unknown;
            if (settings.Type is { Length: > 0 } type && !System.Enum.TryParse(type, ignoreCase: true, out dataType))
            {
                AgentRuntime.Console.MarkupLine($"[red]Error:[/] tipo desconocido '{Markup.Escape(type)}'. Valores: {string.Join(", ", System.Enum.GetNames<DataType>().Skip(1))}");
                return 1;
            }

            var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = settings.Device }, cancellationToken: cancellationToken);
            var name = string.IsNullOrWhiteSpace(settings.Name) ? settings.Address : settings.Name;
            // Si ya había un tag con ese nombre se sustituye (puede cambiar de grupo).
            foreach (var g in device.ScanGroups)
            {
                var existing = g.Tags.FirstOrDefault(t => (string.IsNullOrEmpty(t.Name) ? t.Address : t.Name) == name);
                if (existing is not null)
                    g.Tags.Remove(existing);
            }
            var group = device.ScanGroups.FirstOrDefault(g => string.Equals(g.Name, settings.Group, StringComparison.OrdinalIgnoreCase));
            if (group is null)
                device.ScanGroups.Add(group = new ScanGroupConfig { Name = settings.Group, IntervalMs = settings.IntervalMs });
            group.Tags.Add(new TagConfig
            {
                Name = settings.Name ?? string.Empty,
                Address = settings.Address,
                Writable = settings.Writable,
                DataType = dataType,
            });

            await client.PutDeviceAsync(new PutDeviceRequest { Device = device, Persist = !settings.NoPersist }, cancellationToken: cancellationToken);
            AgentRuntime.Console.MarkupLine($"[green]Tag guardado[/] {Markup.Escape(device.Name)}/{Markup.Escape(name)} en el grupo {Markup.Escape(group.Name)} ({group.IntervalMs} ms)");
            return 0;
        });
}

public sealed class AgentTagRemoveSettings : AgentPersistSettings
{
    [CommandArgument(0, "<DISPOSITIVO>")]
    public string Device { get; set; } = "";

    [CommandArgument(1, "<TAG>")]
    [Description("Nombre del tag.")]
    public string Tag { get; set; } = "";
}

/// <summary>plcmon agent tag remove — quita un tag del dispositivo (los grupos que se quedan vacíos se eliminan).</summary>
public sealed class AgentTagRemoveCommand : AsyncCommand<AgentTagRemoveSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, AgentTagRemoveSettings settings, CancellationToken cancellationToken) =>
        AgentRuntime.RunAsync(settings, async client =>
        {
            var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = settings.Device }, cancellationToken: cancellationToken);
            var removed = 0;
            foreach (var g in device.ScanGroups.ToArray())
            {
                foreach (var t in g.Tags.Where(t => (string.IsNullOrEmpty(t.Name) ? t.Address : t.Name) == settings.Tag).ToArray())
                {
                    g.Tags.Remove(t);
                    removed++;
                }
                if (g.Tags.Count == 0)
                    device.ScanGroups.Remove(g);
            }
            if (removed == 0)
            {
                AgentRuntime.Console.MarkupLine($"[red]Error:[/] el tag '{Markup.Escape(settings.Tag)}' no existe en '{Markup.Escape(device.Name)}'.");
                return 1;
            }
            await client.PutDeviceAsync(new PutDeviceRequest { Device = device, Persist = !settings.NoPersist }, cancellationToken: cancellationToken);
            AgentRuntime.Console.MarkupLine($"[green]Tag eliminado[/] {Markup.Escape(device.Name)}/{Markup.Escape(settings.Tag)}");
            return 0;
        });
}
