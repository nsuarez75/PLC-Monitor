using System.ComponentModel;
using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.Agent.Contracts.Client;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PlcMonitor.Cli;

/// <summary>Opciones comunes de las órdenes <c>plcmon agent ...</c>: a qué agente conectarse.</summary>
public class AgentSettings : CommandSettings
{
    public const string AgentEnvironmentVariable = "PLCMON_AGENT";
    public const string DefaultAgentUrl = "http://127.0.0.1:5310";

    public const string TokenEnvironmentVariable = "PLCMON_AGENT_TOKEN";
    public const string CertEnvironmentVariable = "PLCMON_AGENT_CERT";

    [CommandOption("-a|--agent <URL>")]
    [Description("URL del agente. Si se omite: variable PLCMON_AGENT o http://127.0.0.1:5310.")]
    public string? Agent { get; set; }

    [CommandOption("--token <TOKEN>")]
    [Description("Token de acceso del agente (agentes instalados con seguridad). Si se omite: variable PLCMON_AGENT_TOKEN.")]
    public string? Token { get; set; }

    [CommandOption("--cert <HUELLA>")]
    [Description("Huella SHA-256 del certificado del agente (https). Si se omite: variable PLCMON_AGENT_CERT.")]
    public string? CertificateThumbprint { get; set; }

    /// <summary>URL, token y huella efectivos.</summary>
    public AgentEndpoint ResolveEndpoint(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        static string? Pick(string? option, string? env) => option is { Length: > 0 } ? option : env is { Length: > 0 } ? env : null;
        return new AgentEndpoint(
            ResolveAgentUrl(environment),
            Pick(Token, environment(TokenEnvironmentVariable)),
            Pick(CertificateThumbprint, environment(CertEnvironmentVariable)));
    }

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
    public static Func<AgentEndpoint, GrpcChannel> ChannelFactory { get; set; } = AgentChannelFactory.Create;

    public static IAnsiConsole Console { get; set; } = AnsiConsole.Console;

    /// <summary>Ejecuta <paramref name="action"/> con un cliente del agente y traduce los errores gRPC a mensajes legibles.</summary>
    public static async Task<int> RunAsync(AgentSettings settings, Func<AgentService.AgentServiceClient, Task<int>> action)
    {
        var endpoint = settings.ResolveEndpoint();
        var url = endpoint.Url;
        using var channel = ChannelFactory(endpoint);
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

    public static string Describe(RpcException ex, string url) => AgentErrors.Describe(ex, url);

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

    /// <summary>Texto del valor con la unidad y los decimales del tag (<paramref name="tag"/> null = tal cual).</summary>
    public static string ValueText(TagValue? value, Tag? tag) =>
        tag is null || value is not { Quality: Quality.Good or Quality.Uncertain } ? ValueText(value)
            : ValueDisplay.Format(value.Text, value.DataType, tag.GetDecimals(), tag.Unit);

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
            table.AddRow("Acceso", info.Access switch
            {
                AccessLevel.Read => "[yellow]solo lectura[/]",
                AccessLevel.Full => "completo",
                _ => "completo (agente anterior a los roles)",
            });
            if (info.Historian is { } h)
            {
                table.AddRow("Historiador", h.Enabled
                    ? Markup.Escape($"{h.HistorizedTags} tags · {h.SizeBytes / 1024.0 / 1024.0:0.0} MB en {h.Files} ficheros · retención {(h.RetentionDays > 0 ? $"{h.RetentionDays} días" : "sin límite")} · {h.Path}")
                    : "[grey]desactivado[/]");
            }
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
                    Markup.Escape(AgentRuntime.ValueText(t.Value, t)),
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
            var tags = new Dictionary<(string Device, string Tag), Tag>();
            while (!cancellationToken.IsCancellationRequested)
            {
                using var call = client.Subscribe(request, cancellationToken: cancellationToken);
                try
                {
                    await foreach (var ev in call.ResponseStream.ReadAllAsync(cancellationToken))
                        Print(console, ev, tags);
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

    /// <param name="tags">Tags conocidos (para su unidad y decimales): se rellena con la foto inicial y las reconfiguraciones.</param>
    internal static void Print(IAnsiConsole console, SubscribeEvent ev, Dictionary<(string Device, string Tag), Tag> tags)
    {
        switch (ev.EventCase)
        {
            case SubscribeEvent.EventOneofCase.Snapshot:
                tags.Clear();
                foreach (var d in ev.Snapshot.Devices)
                    console.MarkupLine($"[grey]{AgentRuntime.Time(d.Since)}[/]  [bold]{Markup.Escape(d.Name)}[/] {AgentRuntime.StateMarkup(d.State)} {Markup.Escape(d.Detail)}");
                foreach (var t in ev.Snapshot.Tags)
                {
                    tags[(t.Device, t.Name)] = t;
                    PrintValue(console, t.Device, t.Name, t.Value, t);
                }
                console.MarkupLine($"[grey]— {ev.Snapshot.Devices.Count} dispositivos, {ev.Snapshot.Tags.Count} tags. Cambios en directo (Ctrl+C para salir) —[/]");
                break;
            case SubscribeEvent.EventOneofCase.TagUpdates:
                foreach (var u in ev.TagUpdates.Updates)
                    PrintValue(console, u.Device, u.Tag, u.Value, tags.GetValueOrDefault((u.Device, u.Tag)));
                break;
            case SubscribeEvent.EventOneofCase.DeviceConfigured:
                foreach (var key in tags.Keys.Where(k => k.Device == ev.DeviceConfigured.Device.Name).ToArray())
                    tags.Remove(key);
                foreach (var t in ev.DeviceConfigured.Tags)
                    tags[(t.Device, t.Name)] = t;
                break;
            case SubscribeEvent.EventOneofCase.DeviceStatus:
                var s = ev.DeviceStatus;
                console.MarkupLine($"[grey]{AgentRuntime.Time(s.Since)}[/]  [bold]{Markup.Escape(s.Name)}[/] {AgentRuntime.StateMarkup(s.State)} {Markup.Escape(s.Detail)}");
                break;
            default:
                break;
        }
    }

    private static void PrintValue(IAnsiConsole console, string device, string tag, TagValue? value, Tag? config) =>
        console.MarkupLine(
            $"[grey]{AgentRuntime.Time(value?.Timestamp)}[/]  {Markup.Escape(device)}/{Markup.Escape(tag)} = {Markup.Escape(AgentRuntime.ValueText(value, config))}  {(value is null ? string.Empty : AgentRuntime.QualityMarkup(value.Quality))}");
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

    [CommandOption("--history")]
    [Description("Guardar el tag en el histórico.")]
    public bool History { get; set; }

    [CommandOption("--history-min <DURACION>")]
    [Description("Histórico: intervalo mínimo entre muestras (p.ej. 1s). Por defecto, cada cambio.")]
    public string? HistoryMin { get; set; }

    [CommandOption("--history-max <DURACION>")]
    [Description("Histórico: guardar al menos cada este tiempo aunque no cambie (p.ej. 1m).")]
    public string? HistoryMax { get; set; }

    [CommandOption("--history-deadband <VALOR>")]
    [Description("Histórico: banda muerta absoluta, o en % si termina en % (p.ej. 0.5 o 2%).")]
    public string? HistoryDeadband { get; set; }

    [CommandOption("--unit <UNIDAD>")]
    [Description("Unidad con la que se muestra el valor (p.ej. bar, °C). \"\" la quita. Si no se indica, se conserva la que tuviera.")]
    public string? Unit { get; set; }

    [CommandOption("--decimals <N>")]
    [Description("Decimales con los que se muestran los reales (0-9); -1 los quita (valor completo). Si no se indica, se conservan.")]
    public int? Decimals { get; set; }

    [CommandOption("--scale <ESCALADO>")]
    [Description("Escalado del bruto a unidades de ingeniería: brutoMín:brutoMáx:ingMín:ingMáx, con :clamp al final para limitar al rango (p.ej. 0:27648:0:10). \"none\" lo quita. Si empieza por '-', usa --scale=-10:10:0:1. Si no se indica, se conserva.")]
    public string? Scale { get; set; }

    public override ValidationResult Validate()
    {
        if (Decimals is < -1 or > 9)
            return ValidationResult.Error("--decimals debe estar entre 0 y 9 (o -1 para quitarlos).");
        if (Scale is not null && !TryParseScale(Scale, out _))
            return ValidationResult.Error($"Escalado no válido: '{Scale}'. Formato: brutoMín:brutoMáx:ingMín:ingMáx[:clamp], o none.");
        if (HistoryMin is not null && Durations.Parse(HistoryMin) is null)
            return ValidationResult.Error($"Duración no válida: '{HistoryMin}'.");
        if (HistoryMax is not null && Durations.Parse(HistoryMax) is null)
            return ValidationResult.Error($"Duración no válida: '{HistoryMax}'.");
        if (HistoryDeadband is not null && !double.TryParse(HistoryDeadband.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return ValidationResult.Error($"Banda muerta no válida: '{HistoryDeadband}'.");
        return base.Validate();
    }

    /// <summary>Configuración del histórico pedida en la línea de órdenes; null si no se pidió.</summary>
    public HistoryConfig? HistoryConfig()
    {
        if (!History && HistoryMin is null && HistoryMax is null && HistoryDeadband is null)
            return null;
        var deadband = HistoryDeadband?.Trim() ?? "0";
        return new HistoryConfig
        {
            Enabled = true,
            MinIntervalMs = HistoryMin is { } min ? (int)Durations.Parse(min)!.Value.TotalMilliseconds : 0,
            MaxIntervalMs = HistoryMax is { } max ? (int)Durations.Parse(max)!.Value.TotalMilliseconds : 0,
            Deadband = double.Parse(deadband.TrimEnd('%'), CultureInfo.InvariantCulture),
            DeadbandPercent = deadband.EndsWith('%'),
        };
    }

    /// <summary>Interpreta --scale; <paramref name="scaling"/> null = quitar el escalado ("none" o vacío).</summary>
    public static bool TryParseScale(string text, out Scaling? scaling)
    {
        scaling = null;
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
            return true;
        var parts = trimmed.Split(':', StringSplitOptions.TrimEntries);
        var clamp = parts.Length == 5 && parts[4].Equals("clamp", StringComparison.OrdinalIgnoreCase);
        if (parts.Length != 4 && !clamp)
            return false;
        var numbers = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) || !double.IsFinite(numbers[i]))
                return false;
        }
        scaling = new Scaling { RawMin = numbers[0], RawMax = numbers[1], EngMin = numbers[2], EngMax = numbers[3], Clamp = clamp };
        return true;
    }
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
            TagConfig? previous = null;
            foreach (var g in device.ScanGroups)
            {
                var existing = g.Tags.FirstOrDefault(t => (string.IsNullOrEmpty(t.Name) ? t.Address : t.Name) == name);
                if (existing is not null)
                {
                    g.Tags.Remove(existing);
                    previous = existing;
                }
            }
            var group = device.ScanGroups.FirstOrDefault(g => string.Equals(g.Name, settings.Group, StringComparison.OrdinalIgnoreCase));
            if (group is null)
                device.ScanGroups.Add(group = new ScanGroupConfig { Name = settings.Group, IntervalMs = settings.IntervalMs });
            var tag = new TagConfig
            {
                Name = settings.Name ?? string.Empty,
                Address = settings.Address,
                Writable = settings.Writable,
                DataType = dataType,
                History = settings.HistoryConfig(),
                // La transformación y el grupo de visualización no se tocan desde aquí: se conservan los que tuviera.
                Transform = previous?.Transform ?? "",
                Folder = previous?.Folder ?? "",
                Unit = settings.Unit?.Trim() ?? previous?.Unit ?? "",
                Scaling = settings.Scale is { } scale && AgentTagAddSettings.TryParseScale(scale, out var scaling) ? scaling : previous?.Scaling,
            };
            if ((settings.Decimals ?? previous?.GetDecimals()) is { } decimals and >= 0)
                tag.Decimals = decimals;
            group.Tags.Add(tag);

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

public sealed class AgentHistorySettings : AgentSettings
{
    [CommandArgument(0, "<DISPOSITIVO>")]
    public string Device { get; set; } = "";

    [CommandArgument(1, "<TAG>")]
    [Description("Uno o varios tags.")]
    public string[] Tags { get; set; } = [];

    [CommandOption("--last <DURACION>")]
    [Description("Periodo hasta ahora: 30s, 15m, 2h, 7d... Por defecto 1h (si no se indica --from).")]
    public string? Last { get; set; }

    [CommandOption("--from <FECHA>")]
    [Description("Inicio (hora local), p.ej. \"2026-10-15 08:00\".")]
    public string? From { get; set; }

    [CommandOption("--to <FECHA>")]
    [Description("Fin (hora local). Por defecto, ahora.")]
    public string? To { get; set; }

    [CommandOption("-i|--interval <DURACION>")]
    [Description("Agrega por intervalos (1m, 15m, 1h...). Sin él, puntos en bruto.")]
    public string? Interval { get; set; }

    [CommandOption("--agg <AGREGADO>")]
    [Description("Agregado con --interval: avg, min, max, first, last.")]
    [DefaultValue("avg")]
    public string Aggregate { get; set; } = "avg";

    [CommandOption("-n|--max <N>")]
    [Description("Máximo de puntos por tag.")]
    [DefaultValue(10000)]
    public int MaxPoints { get; set; } = 10000;

    [CommandOption("--csv <FICHERO>")]
    [Description("Guarda el resultado en CSV (dispositivo, tag, hora UTC ISO 8601, valor, calidad) en vez de mostrarlo.")]
    public string? Csv { get; set; }

    public override ValidationResult Validate()
    {
        if (Last is not null && Durations.Parse(Last) is null)
            return ValidationResult.Error($"Duración no válida: '{Last}'. Ejemplos: 30s, 15m, 2h, 7d.");
        if (Interval is not null && Durations.Parse(Interval) is null)
            return ValidationResult.Error($"Intervalo no válido: '{Interval}'.");
        if (Durations.ParseAggregate(Aggregate) is null)
            return ValidationResult.Error("--agg debe ser avg, min, max, first o last.");
        if (From is not null && !DateTimeOffset.TryParse(From, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out _))
            return ValidationResult.Error($"Fecha no válida: '{From}'.");
        if (To is not null && !DateTimeOffset.TryParse(To, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out _))
            return ValidationResult.Error($"Fecha no válida: '{To}'.");
        return base.Validate();
    }
}

/// <summary>Duraciones cortas (500ms, 30s, 15m, 2h, 7d) y nombres de agregados.</summary>
public static class Durations
{
    public static TimeSpan? Parse(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text.Trim(), @"^(\d+(?:[.,]\d+)?)\s*(ms|s|m|h|d)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;
        var value = double.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        return match.Groups[2].Value.ToLowerInvariant() switch
        {
            "ms" => TimeSpan.FromMilliseconds(value),
            "s" => TimeSpan.FromSeconds(value),
            "m" => TimeSpan.FromMinutes(value),
            "h" => TimeSpan.FromHours(value),
            _ => TimeSpan.FromDays(value),
        };
    }

    public static HistoryAggregate? ParseAggregate(string text) => text.Trim().ToLowerInvariant() switch
    {
        "avg" or "media" => HistoryAggregate.Average,
        "min" => HistoryAggregate.Min,
        "max" => HistoryAggregate.Max,
        "first" => HistoryAggregate.First,
        "last" => HistoryAggregate.Last,
        _ => null,
    };
}

/// <summary>plcmon agent history — histórico de tags guardado por el agente.</summary>
public sealed class AgentHistoryCommand : AsyncCommand<AgentHistorySettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, AgentHistorySettings settings, CancellationToken cancellationToken) =>
        AgentRuntime.RunAsync(settings, async client =>
        {
            var to = settings.To is { } t ? DateTimeOffset.Parse(t, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal) : DateTimeOffset.Now;
            var from = settings.From is { } f ? DateTimeOffset.Parse(f, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal)
                : to - (Durations.Parse(settings.Last ?? "1h") ?? TimeSpan.FromHours(1));
            var request = new HistoryRequest
            {
                Device = settings.Device,
                From = Timestamp.FromDateTimeOffset(from),
                To = Timestamp.FromDateTimeOffset(to),
                IntervalMs = settings.Interval is { } i ? (int)Durations.Parse(i)!.Value.TotalMilliseconds : 0,
                Aggregate = Durations.ParseAggregate(settings.Aggregate)!.Value,
                MaxPoints = settings.MaxPoints,
            };
            request.Tags.AddRange(settings.Tags);
            var reply = await client.QueryHistoryAsync(request, cancellationToken: cancellationToken);
            var console = AgentRuntime.Console;
            // Unidad y decimales de los tags que siguen configurados.
            var tags = (await client.ListTagsAsync(new ListTagsRequest { Device = settings.Device }, cancellationToken: cancellationToken)).Tags
                .ToDictionary(t => t.Name, StringComparer.Ordinal);

            if (settings.Csv is { } path)
            {
                // El CSV guarda el valor completo; la unidad va en su columna.
                await using var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false));
                await writer.WriteLineAsync("device,tag,timestamp,value,quality,unit");
                var rows = 0;
                foreach (var s in reply.Series)
                {
                    var unit = tags.GetValueOrDefault(s.Tag)?.Unit ?? string.Empty;
                    foreach (var p in s.Points)
                    {
                        await writer.WriteLineAsync(string.Join(',', Csv(s.Device), Csv(s.Tag),
                            p.Timestamp.ToDateTimeOffset().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                            Csv(PointText(p)), p.Quality.ToString(), Csv(unit)));
                        rows++;
                    }
                }
                console.MarkupLine($"[green]Guardado[/] {Markup.Escape(path)} ({rows} filas)");
                return 0;
            }

            foreach (var s in reply.Series)
            {
                var table = new Table().Title($"[bold]{Markup.Escape(s.Device)}/{Markup.Escape(s.Tag)}[/]").AddColumns("Hora", "Valor", "Calidad");
                foreach (var p in s.Points)
                {
                    table.AddRow(
                        p.Timestamp.ToDateTimeOffset().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                        Markup.Escape(PointText(p, tags.GetValueOrDefault(s.Tag))),
                        AgentRuntime.QualityMarkup(p.Quality));
                }
                console.Write(table);
                console.MarkupLine($"[grey]{s.Points.Count} puntos{(s.Truncated ? " (hay más: aumenta --max o usa --interval)" : "")}[/]");
            }
            return 0;
        });

    private static string PointText(HistoryPoint p) =>
        p.HasNumber ? p.Number.ToString("G15", CultureInfo.InvariantCulture) : p.Text;

    /// <summary>Punto con la unidad y los decimales del tag (los decimales, si es real o aún no se conoce su tipo).</summary>
    private static string PointText(HistoryPoint p, Tag? tag)
    {
        if (tag is null || !p.HasNumber)
            return PointText(p);
        var type = tag.Value?.DataType is { } t && t != DataType.Unknown ? t : DataType.Double;
        return ValueDisplay.Format(PointText(p), type, tag.GetDecimals(), tag.Unit);
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}

public sealed class AgentHistoryDeleteSettings : AgentSettings
{
    [CommandArgument(0, "<DISPOSITIVO>")]
    public string Device { get; set; } = "";

    [CommandArgument(1, "<TAG>")]
    [Description("Uno o varios tags.")]
    public string[] Tags { get; set; } = [];

    [CommandOption("--last <DURACION>")]
    [Description("Borra desde hace este tiempo hasta ahora: 30s, 15m, 2h, 7d...")]
    public string? Last { get; set; }

    [CommandOption("--from <FECHA>")]
    [Description("Inicio (hora local), p.ej. \"2026-10-15 08:00\".")]
    public string? From { get; set; }

    [CommandOption("--to <FECHA>")]
    [Description("Fin (hora local). Por defecto, ahora.")]
    public string? To { get; set; }

    [CommandOption("--all")]
    [Description("Borra todo lo guardado de esos tags.")]
    public bool All { get; set; }

    [CommandOption("-y|--yes")]
    [Description("No pedir confirmación.")]
    public bool Yes { get; set; }

    public override ValidationResult Validate()
    {
        if (All && (Last is not null || From is not null || To is not null))
            return ValidationResult.Error("--all no se combina con --from, --to ni --last.");
        if (!All && Last is null && From is null)
            return ValidationResult.Error("Indica el intervalo que se borra: --from (y --to), --last o --all.");
        if (Last is not null && From is not null)
            return ValidationResult.Error("Usa --from o --last, no los dos.");
        if (Last is not null && Durations.Parse(Last) is null)
            return ValidationResult.Error($"Duración no válida: '{Last}'. Ejemplos: 30s, 15m, 2h, 7d.");
        if (From is not null && !DateTimeOffset.TryParse(From, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out _))
            return ValidationResult.Error($"Fecha no válida: '{From}'.");
        if (To is not null && !DateTimeOffset.TryParse(To, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out _))
            return ValidationResult.Error($"Fecha no válida: '{To}'.");
        var (from, to) = Range(DateTimeOffset.Now);
        if (to < from)
            return ValidationResult.Error("El final del intervalo es anterior al inicio.");
        return base.Validate();
    }

    /// <summary>
    /// Intervalo que se borra. Con --all, de 1970 a mañana: el agente recorre sus ficheros mes a mes desde el inicio, y
    /// se deja un día de margen por si el reloj del PLC va adelantado.
    /// </summary>
    public (DateTimeOffset From, DateTimeOffset To) Range(DateTimeOffset now)
    {
        if (All)
            return (DateTimeOffset.UnixEpoch, now.AddDays(1));
        var to = To is { } t ? DateTimeOffset.Parse(t, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal) : now;
        var from = From is { } f ? DateTimeOffset.Parse(f, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal)
            : to - (Last is { } last ? Durations.Parse(last) ?? TimeSpan.Zero : TimeSpan.Zero);
        return (from, to);
    }
}

/// <summary>plcmon agent history-delete — borra lo guardado de unos tags en un intervalo (no se puede deshacer).</summary>
public sealed class AgentHistoryDeleteCommand : AsyncCommand<AgentHistoryDeleteSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, AgentHistoryDeleteSettings settings, CancellationToken cancellationToken) =>
        AgentRuntime.RunAsync(settings, async client =>
        {
            var (from, to) = settings.Range(DateTimeOffset.Now);
            var tags = string.Join(", ", settings.Tags.Select(t => $"{settings.Device}/{t}"));
            const string format = "yyyy-MM-dd HH:mm:ss";
            var when = settings.All ? "todo lo guardado"
                : $"lo guardado entre el {from.ToLocalTime().ToString(format, CultureInfo.InvariantCulture)} y el {to.ToLocalTime().ToString(format, CultureInfo.InvariantCulture)}";
            if (!settings.Yes && !await AgentRuntime.Console.ConfirmAsync(
                    $"¿Borrar {when} de [bold]{Markup.Escape(tags)}[/]? No se puede deshacer.", defaultValue: false, cancellationToken))
            {
                AgentRuntime.Console.MarkupLine("[grey]Cancelado.[/]");
                return 2;
            }

            var request = new DeleteHistoryRequest
            {
                Device = settings.Device,
                From = Timestamp.FromDateTimeOffset(from),
                To = Timestamp.FromDateTimeOffset(to),
            };
            request.Tags.AddRange(settings.Tags);
            var reply = await client.DeleteHistoryAsync(request, cancellationToken: cancellationToken);
            AgentRuntime.Console.MarkupLine(reply.Deleted == 0
                ? "[yellow]No había datos en ese intervalo.[/]"
                : $"[green]Borrados[/] {reply.Deleted} datos del histórico de {Markup.Escape(tags)}");
            return 0;
        });
}

public sealed class AgentHistoryRenameSettings : AgentSettings
{
    [CommandArgument(0, "<ORIGEN>")]
    [Description("dispositivo/tag, o solo el dispositivo para llevar todos sus tags.")]
    public string Source { get; set; } = "";

    [CommandArgument(1, "<DESTINO>")]
    [Description("dispositivo/tag, o solo el dispositivo si el origen tampoco lleva tag.")]
    public string Target { get; set; } = "";

    [CommandOption("-y|--yes")]
    [Description("No pedir confirmación.")]
    public bool Yes { get; set; }

    public override ValidationResult Validate()
    {
        if (Rename() is not { } rename)
            return ValidationResult.Error("Indica el tag en los dos (dispositivo/tag) o en ninguno (todo el dispositivo).");
        if (rename.FromDevice == rename.ToDevice && rename.FromTag == rename.ToTag)
            return ValidationResult.Error("El origen y el destino son el mismo.");
        return base.Validate();
    }

    /// <summary>Renombrado pedido; null si falta un dispositivo o solo uno de los dos lleva tag.</summary>
    public HistoryRename? Rename()
    {
        var (fromDevice, fromTag) = Split(Source);
        var (toDevice, toTag) = Split(Target);
        if (fromDevice.Length == 0 || toDevice.Length == 0 || (fromTag is null) != (toTag is null) || fromTag?.Length == 0 || toTag?.Length == 0)
            return null;
        return new HistoryRename { FromDevice = fromDevice, FromTag = fromTag ?? "", ToDevice = toDevice, ToTag = toTag ?? "" };
    }

    /// <summary>«dispositivo/tag» por la primera barra: los dispositivos no llevan '/', los tags sí pueden.</summary>
    private static (string Device, string? Tag) Split(string text)
    {
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? (text.Trim(), null) : (text[..slash].Trim(), text[(slash + 1)..].Trim());
    }
}

/// <summary>plcmon agent history-rename — lleva el histórico guardado con un nombre a otro (tras renombrar un tag o un dispositivo).</summary>
public sealed class AgentHistoryRenameCommand : AsyncCommand<AgentHistoryRenameSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, AgentHistoryRenameSettings settings, CancellationToken cancellationToken) =>
        AgentRuntime.RunAsync(settings, async client =>
        {
            var rename = settings.Rename()!;
            var from = Markup.Escape(settings.Source.Trim());
            var to = Markup.Escape(settings.Target.Trim());
            if (!settings.Yes && !await AgentRuntime.Console.ConfirmAsync(
                    $"¿Llevar el histórico de [bold]{from}[/] a [bold]{to}[/]? Si el destino ya tiene histórico, se juntan.", defaultValue: false, cancellationToken))
            {
                AgentRuntime.Console.MarkupLine("[grey]Cancelado.[/]");
                return 2;
            }

            var reply = await client.RenameHistoryAsync(new RenameHistoryRequest { Renames = { rename } }, cancellationToken: cancellationToken);
            AgentRuntime.Console.MarkupLine(reply.Moved == 0
                ? $"[yellow]No había histórico de {from}.[/]"
                : $"[green]Histórico llevado[/] a {to} ({reply.Moved} {(reply.Moved == 1 ? "tag" : "tags")})");
            return 0;
        });
}
