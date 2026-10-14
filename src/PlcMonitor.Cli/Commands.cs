using System.ComponentModel;
using System.Diagnostics;
using PlcMonitor.Abstractions;
using PlcMonitor.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PlcMonitor.Cli;

/// <summary>plcmon drivers — lista los drivers disponibles.</summary>
public sealed class DriversCommand : Command
{
    public override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        var table = new Table().AddColumns("Id", "Nombre", "Capacidades");
        foreach (var f in CliRuntime.Registry.Factories.OrderBy(f => f.TypeId, StringComparer.Ordinal))
            table.AddRow(f.TypeId, Markup.Escape(f.DisplayName), f.Capabilities.ToString());
        AnsiConsole.Write(table);
        return 0;
    }
}

/// <summary>plcmon info — conecta y muestra la información del dispositivo.</summary>
public sealed class InfoCommand : AsyncCommand<DeviceSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DeviceSettings settings, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var (conn, logging) = await CliRuntime.ConnectAsync(settings, cancellationToken);
        using (logging)
        await using (conn)
        {
            var table = new Table().AddColumns("Propiedad", "Valor");
            table.AddRow("Estado", conn.State.ToString());
            table.AddRow("Tiempo de conexión", $"{sw.ElapsedMilliseconds} ms");
            foreach (var (key, value) in await conn.GetDeviceInfoAsync(cancellationToken))
                table.AddRow(Markup.Escape(key), Markup.Escape(value));
            AnsiConsole.Write(table);
        }
        return 0;
    }
}

public sealed class BrowseSettings : DeviceSettings
{
    [CommandArgument(0, "[FILTRO]")]
    [Description("Texto que debe contener el nombre (sin distinguir mayúsculas).")]
    public string? Filter { get; set; }

    [CommandOption("-n|--max <N>")]
    [Description("Máximo de resultados a mostrar.")]
    [DefaultValue(100)]
    public int MaxResults { get; set; } = 100;
}

/// <summary>plcmon browse — busca variables en la CPU (solo drivers con exploración).</summary>
public sealed class BrowseCommand : AsyncCommand<BrowseSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, BrowseSettings settings, CancellationToken cancellationToken)
    {
        var (conn, logging) = await CliRuntime.ConnectAsync(settings, cancellationToken);
        using (logging)
        await using (conn)
        {
            if (conn is not IBrowsableConnection browsable)
            {
                AnsiConsole.MarkupLine($"[red]El driver '{Markup.Escape(settings.Driver)}' no permite explorar variables.[/]");
                return 2;
            }

            var sw = Stopwatch.StartNew();
            var result = await AnsiConsole.Status().StartAsync(
                "Explorando símbolos de la CPU (puede tardar unos segundos)…",
                _ => browsable.BrowseAsync(new BrowseQuery(settings.Filter, settings.MaxResults), cancellationToken));

            var table = new Table().AddColumns("Símbolo", "Tipo S7", "Tipo");
            foreach (var item in result.Items)
                table.AddRow(Markup.Escape(item.Address), Markup.Escape(item.NativeType), item.DataType.ToString());
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine(
                $"[grey]{result.Items.Count} mostrados de {result.TotalMatches} coincidencias · {result.TotalSymbols} símbolos en la CPU · {sw.ElapsedMilliseconds} ms[/]");
        }
        return 0;
    }
}

public class AddressSettings : DeviceSettings
{
    [CommandArgument(0, "<DIRECCION>")]
    [Description("Una o varias direcciones (símbolos o direcciones absolutas como DB1.DBW10).")]
    public string[] Addresses { get; set; } = [];
}

/// <summary>plcmon read — lee una o varias direcciones una vez.</summary>
public sealed class ReadCommand : AsyncCommand<AddressSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, AddressSettings settings, CancellationToken cancellationToken)
    {
        var (conn, logging) = await CliRuntime.ConnectAsync(settings, cancellationToken);
        using (logging)
        await using (conn)
        {
            var sw = Stopwatch.StartNew();
            var values = await conn.ReadAsync(settings.Addresses, cancellationToken);
            var table = new Table().AddColumns("Dirección", "Valor", "Tipo", "Calidad");
            for (var i = 0; i < values.Count; i++)
            {
                var v = values[i];
                table.AddRow(
                    Markup.Escape(settings.Addresses[i]),
                    Markup.Escape(v.Quality.IsBad() ? v.ErrorDetail ?? string.Empty : ValueFormatter.Format(v.Value)),
                    v.DataType.ToString(),
                    CliRuntime.QualityMarkup(v.Quality));
            }
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[grey]Lectura: {sw.ElapsedMilliseconds} ms[/]");
            return values.All(v => v.Quality.IsGood()) ? 0 : 1;
        }
    }
}

public sealed class WatchSettings : AddressSettings
{
    [CommandOption("-i|--interval <MS>")]
    [Description("Periodo de lectura en milisegundos.")]
    [DefaultValue(1000)]
    public int IntervalMs { get; set; } = 1000;

    public override ValidationResult Validate() =>
        IntervalMs < 50 ? ValidationResult.Error("El intervalo mínimo es 50 ms.") : base.Validate();
}

/// <summary>plcmon watch — lee periódicamente y muestra solo los cambios (Ctrl+C para salir).</summary>
public sealed class WatchCommand : AsyncCommand<WatchSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, WatchSettings settings, CancellationToken cancellationToken)
    {
        var (conn, logging) = await CliRuntime.ConnectAsync(settings, cancellationToken);
        using (logging)
        await using (conn)
        {
            AnsiConsole.MarkupLine($"[grey]Vigilando {settings.Addresses.Length} direcciones cada {settings.IntervalMs} ms. Ctrl+C para salir.[/]");
            try
            {
                await foreach (var changes in TagPoller.WatchAsync(
                    conn, settings.Addresses, TimeSpan.FromMilliseconds(settings.IntervalMs), cancellationToken: cancellationToken))
                {
                    foreach (var change in changes)
                    {
                        var v = change.Value;
                        AnsiConsole.MarkupLine(
                            $"{v.Timestamp.ToLocalTime():HH:mm:ss.fff}  {Markup.Escape(change.Address)} = [bold]{Markup.Escape(ValueFormatter.Format(v))}[/]  {CliRuntime.QualityMarkup(v.Quality)}");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
        return 0;
    }
}

public sealed class WriteSettings : DeviceSettings
{
    [CommandArgument(0, "<DIRECCION>")]
    public string Address { get; set; } = "";

    [CommandArgument(1, "<VALOR>")]
    public string Value { get; set; } = "";

    [CommandOption("--type <TIPO>")]
    [Description("Tipo del valor (Boolean, Int16, Float, String...). Si se omite, se lee la variable para averiguarlo.")]
    public TagDataType? DataType { get; set; }

    [CommandOption("-y|--yes")]
    [Description("No pedir confirmación.")]
    public bool Yes { get; set; }
}

/// <summary>plcmon write — escribe un valor (pide confirmación).</summary>
public sealed class WriteCommand : AsyncCommand<WriteSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, WriteSettings settings, CancellationToken cancellationToken)
    {
        var (conn, logging) = await CliRuntime.ConnectAsync(settings, cancellationToken);
        using (logging)
        await using (conn)
        {
            var current = (await conn.ReadAsync([settings.Address], cancellationToken))[0];
            var type = settings.DataType ?? current.DataType;
            if (type == TagDataType.Unknown)
            {
                AnsiConsole.MarkupLine($"[red]No se pudo determinar el tipo de '{Markup.Escape(settings.Address)}': {Markup.Escape(ValueFormatter.Format(current))}. Indica --type.[/]");
                return 2;
            }
            if (!ValueParser.TryParse(settings.Value, type, out var value, out var error))
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(error!)}[/]");
                return 2;
            }

            AnsiConsole.MarkupLine($"[yellow]ATENCIÓN:[/] se va a escribir en un PLC real.");
            AnsiConsole.MarkupLine($"  {Markup.Escape(settings.Address)}: {Markup.Escape(ValueFormatter.Format(current))} → [bold]{Markup.Escape(ValueFormatter.Format(value))}[/] ({type})");
            if (!settings.Yes && !await AnsiConsole.ConfirmAsync("¿Confirmar escritura?", defaultValue: false, cancellationToken))
            {
                AnsiConsole.MarkupLine("Cancelado.");
                return 3;
            }

            var result = (await conn.WriteAsync([new WriteRequest(settings.Address, value)], cancellationToken))[0];
            if (!result.Success)
            {
                AnsiConsole.MarkupLine($"[red]Escritura fallida: {result.Quality} {Markup.Escape(result.ErrorDetail ?? string.Empty)}[/]");
                return 1;
            }
            var after = (await conn.ReadAsync([settings.Address], cancellationToken))[0];
            AnsiConsole.MarkupLine($"[green]Escrito.[/] Valor actual: [bold]{Markup.Escape(ValueFormatter.Format(after))}[/]");
            return 0;
        }
    }
}
