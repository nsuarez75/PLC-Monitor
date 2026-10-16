using System.Globalization;
using System.Text;

namespace PlcMonitor.Agent;

/// <summary>
/// Log del agente en ficheros de texto, uno por día (<c>plcmon-agent-AAAAMMDD.log</c>), que se borran pasados
/// <c>retentionDays</c>. En Windows el Visor de eventos solo guarda avisos y errores; esto deja también la información
/// normal donde la app la puede leer. El fichero abierto se puede leer mientras se escribe.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    public const string Prefix = "plcmon-agent-";

    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private StreamWriter? _writer;
    private DateOnly _day;
    private bool _failed;

    public FileLoggerProvider(string directory, int retentionDays = 7, TimeProvider? time = null)
    {
        _directory = directory;
        _retentionDays = Math.Max(1, retentionDays);
        _time = time ?? TimeProvider.System;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var now = _time.GetLocalNow();
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ').Append(Level(level)).Append(' ')
            .Append(category).Append(": ").Append(message);
        if (exception is not null)
            line.AppendLine().Append(exception);
        lock (_lock)
        {
            try
            {
                Open(DateOnly.FromDateTime(now.DateTime))?.WriteLine(line.ToString());
            }
            catch (IOException)
            {
                // El log no puede tumbar el agente: se pierde la línea.
            }
        }
    }

    private StreamWriter? Open(DateOnly day)
    {
        if (_writer is not null && day == _day)
            return _writer;
        _writer?.Dispose();
        _writer = null;
        if (_failed && day == _day)
            return null;
        _day = day;
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, $"{Prefix}{day:yyyyMMdd}.log");
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            _failed = false;
            DeleteOld(day);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin permiso o sin disco: se reintenta al cambiar de día.
            _failed = true;
        }
        return _writer;
    }

    private void DeleteOld(DateOnly today)
    {
        var oldest = today.AddDays(-(_retentionDays - 1));
        foreach (var file in Directory.EnumerateFiles(_directory, $"{Prefix}*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)[Prefix.Length..];
            if (DateOnly.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < oldest)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---",
    };

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // El filtro por nivel lo aplica la configuración de Logging (también a este proveedor).
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}
