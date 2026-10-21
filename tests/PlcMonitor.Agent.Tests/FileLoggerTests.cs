using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace PlcMonitor.Agent.Tests;

/// <summary>Log del agente a fichero (lo que «Ver log» lee en Windows).</summary>
public sealed class FileLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"plcmon-logs-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 9, 30, 0, TimeSpan.Zero)) { };

    public FileLoggerTests() => _time.SetLocalTimeZone(TimeZoneInfo.Utc);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string File(string day) => Path.Combine(_dir, $"plcmon-agent-{day}.log");

    // El fichero del día sigue abierto para escribir: en Windows solo se puede leer compartiéndolo (File.ReadAllText no).
    private static string ReadShared(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        return reader.ReadToEnd();
    }

    [Fact]
    public void Lines_go_to_the_file_of_the_day_and_can_be_read_while_open()
    {
        using var provider = new FileLoggerProvider(_dir, time: _time);
        var logger = provider.CreateLogger("PlcMonitor.Engine.TagEngine");

        logger.LogInformation("Motor arrancado: {Count} dispositivos", 2);
        logger.LogError(new InvalidOperationException("boom"), "Fallo");

        // Se lee con el fichero aún abierto (como Get-Content en Windows).
        using var reader = new StreamReader(new FileStream(File("20261017"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        var text = reader.ReadToEnd();
        text.ShouldContain("2026-10-17 09:30:00.000 INF PlcMonitor.Engine.TagEngine: Motor arrancado: 2 dispositivos");
        text.ShouldContain("ERR PlcMonitor.Engine.TagEngine: Fallo");
        text.ShouldContain("System.InvalidOperationException: boom");
    }

    [Fact]
    public void A_new_file_each_day_and_old_ones_are_deleted()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File("20261001"), "viejo");
        System.IO.File.WriteAllText(File("20261011"), "dentro de los 7 días");
        System.IO.File.WriteAllText(Path.Combine(_dir, "otra-cosa.log"), "no es nuestro");
        using var provider = new FileLoggerProvider(_dir, retentionDays: 7, time: _time);
        var logger = provider.CreateLogger("x");

        logger.LogInformation("hoy");
        System.IO.File.Exists(File("20261001")).ShouldBeFalse();
        System.IO.File.Exists(File("20261011")).ShouldBeTrue();
        System.IO.File.Exists(Path.Combine(_dir, "otra-cosa.log")).ShouldBeTrue();

        _time.Advance(TimeSpan.FromDays(1));
        logger.LogInformation("mañana");

        System.IO.File.Exists(File("20261011")).ShouldBeFalse();
        ReadShared(File("20261018")).ShouldContain("mañana");
        ReadShared(File("20261017")).ShouldNotContain("mañana");
    }

    [Fact]
    public void An_unwritable_folder_does_not_break_logging()
    {
        var blocked = Path.Combine(_dir, "fichero");
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(blocked, "no es una carpeta");
        using var provider = new FileLoggerProvider(blocked, time: _time);

        Should.NotThrow(() => provider.CreateLogger("x").LogWarning("se pierde"));
    }
}
