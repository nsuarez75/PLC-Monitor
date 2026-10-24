using Spectre.Console;
using Spectre.Console.Cli;

namespace PlcMonitor.Cli;

/// <summary>Órdenes de <c>plcmon</c> (compartido por el programa y los tests).</summary>
public static class CliApp
{
    public static void Configure(IConfigurator config)
    {
        config.SetApplicationName("plcmon");
        config.SetExceptionHandler((ex, _) =>
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        });

        config.AddCommand<DriversCommand>("drivers").WithDescription("Lista los drivers disponibles.");
        config.AddCommand<InfoCommand>("info")
            .WithDescription("Conecta y muestra información del dispositivo.")
            .WithExample("info", "-H", "192.168.2.1", "-p", "secreto");
        config.AddCommand<BrowseCommand>("browse")
            .WithDescription("Busca variables en la CPU (s7-symbolic).")
            .WithExample("browse", "Motor", "-H", "192.168.2.1", "-n", "50");
        config.AddCommand<ReadCommand>("read")
            .WithDescription("Lee una o varias direcciones.")
            .WithExample("read", "\"MiDB\".Motor.Velocidad", "-H", "192.168.2.1")
            .WithExample("read", "DB1.DBW10", "DB1.REAL20", "-d", "s7-absolute", "-H", "192.168.0.10", "--cpu", "S7300", "--slot", "2")
            .WithExample("read", "HR0", "HR10:FLOAT", "C3", "-d", "modbus", "-H", "192.168.0.20", "--unit-id", "1")
            .WithExample("read", "40001", "30005:UINT16", "-d", "modbus", "--serial", "/dev/ttyUSB0", "--baud", "19200", "--parity", "even", "--unit-id", "3");
        config.AddCommand<WatchCommand>("watch")
            .WithDescription("Lee periódicamente y muestra los cambios (Ctrl+C para salir).")
            .WithExample("watch", "\"MiDB\".Contador", "-H", "192.168.2.1", "-i", "500");
        config.AddCommand<WriteCommand>("write")
            .WithDescription("Escribe un valor (pide confirmación).")
            .WithExample("write", "\"MiDB\".Consigna", "12.5", "-H", "192.168.2.1");

        config.AddBranch<AgentSettings>("agent", agent =>
        {
            agent.SetDescription("Habla con un colector plcmon-agent (local o remoto). URL con -a o PLCMON_AGENT.");
            agent.AddCommand<AgentInfoCommand>("info").WithDescription("Versión, máquina y drivers del colector.");
            agent.AddCommand<AgentDevicesCommand>("devices").WithDescription("Dispositivos configurados y su estado.");
            agent.AddCommand<AgentTagsCommand>("tags")
                .WithDescription("Tags configurados con su último valor.")
                .WithExample("agent", "tags", "prensa1");
            agent.AddCommand<AgentReadCommand>("read")
                .WithDescription("Lee direcciones cualesquiera a través del colector.")
                .WithExample("agent", "read", "prensa1", "DB10.DBW0", "-a", "http://192.168.0.5:5310");
            agent.AddCommand<AgentWriteCommand>("write")
                .WithDescription("Escribe un tag configurado (pide confirmación; doble llave en el colector).")
                .WithExample("agent", "write", "prensa1", "marcha", "true");
            agent.AddCommand<AgentEnableCommand>("enable").WithDescription("Activa un dispositivo.");
            agent.AddCommand<AgentDisableCommand>("disable").WithDescription("Desactiva un dispositivo.");
            agent.AddCommand<AgentBrowseCommand>("browse")
                .WithDescription("Explora las variables de un dispositivo conectado.")
                .WithExample("agent", "browse", "prensa1", "Motor", "-n", "50");
            agent.AddCommand<AgentConfigCommand>("config").WithDescription("Configuración en árbol: dispositivos, grupos y tags.");
            agent.AddBranch<AgentSettings>("device", device =>
            {
                device.SetDescription("Alta y baja de dispositivos (se guarda en agent.json salvo --no-persist).");
                device.AddCommand<AgentDeviceAddCommand>("add")
                    .WithDescription("Da de alta un dispositivo o cambia sus ajustes (conserva sus tags).")
                    .WithExample("agent", "device", "add", "prensa1", "-d", "s7-symbolic", "-s", "{\"host\":\"192.168.0.10\"}");
                device.AddCommand<AgentDeviceRemoveCommand>("remove").WithDescription("Da de baja un dispositivo y sus tags.");
                device.AddCommand<AgentDeviceOrderCommand>("order")
                    .WithDescription("Cambia el orden de los dispositivos: los indicados primero, en ese orden; los demás, detrás.")
                    .WithExample("agent", "device", "order", "prensa1", "horno");
            });
            agent.AddBranch<AgentSettings>("tag", tag =>
            {
                tag.SetDescription("Alta y baja de tags (se guarda en agent.json salvo --no-persist).");
                tag.AddCommand<AgentTagAddCommand>("add")
                    .WithDescription("Añade un tag a un grupo de escaneo (se crea si no existe).")
                    .WithExample("agent", "tag", "add", "prensa1", "\"MiDB\".Velocidad", "--name", "velocidad", "-g", "rapido", "-i", "250");
                tag.AddCommand<AgentTagRemoveCommand>("remove").WithDescription("Quita un tag.");
                tag.AddCommand<AgentTagEnableCommand>("enable").WithDescription("Activa uno o varios tags (vuelven a leerse).");
                tag.AddCommand<AgentTagDisableCommand>("disable").WithDescription("Desactiva uno o varios tags (no se leen, ni se historizan, ni se publican en OPC UA).");
            });
            agent.AddCommand<AgentHistoryCommand>("history")
                .WithDescription("Histórico de tags guardado por el colector (tabla o CSV).")
                .WithExample("agent", "history", "prensa1", "velocidad", "--last", "2h", "-i", "1m", "--agg", "max")
                .WithExample("agent", "history", "prensa1", "velocidad", "presion", "--from", "2026-10-15 08:00", "--csv", "turno.csv");
            agent.AddCommand<AgentHistoryDeleteCommand>("history-delete")
                .WithDescription("Borra el histórico de unos tags en un intervalo (pide confirmación; no se puede deshacer).")
                .WithExample("agent", "history-delete", "prensa1", "velocidad", "--from", "2026-10-15 08:00", "--to", "2026-10-15 14:00")
                .WithExample("agent", "history-delete", "prensa1", "velocidad", "presion", "--all", "-y");
            agent.AddCommand<AgentHistoryRenameCommand>("history-rename")
                .WithDescription("Lleva el histórico guardado con un nombre a otro (pide confirmación).")
                .WithExample("agent", "history-rename", "prensa1/vel", "prensa1/velocidad")
                .WithExample("agent", "history-rename", "prensa1", "prensa2");
            agent.AddCommand<AgentWatchCommand>("watch")
                .WithDescription("Cambios de valores y de estado en directo (Ctrl+C para salir).")
                .WithExample("agent", "watch", "prensa1");
        });
    }
}
