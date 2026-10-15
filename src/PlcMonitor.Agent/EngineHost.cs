using Microsoft.Extensions.Options;
using PlcMonitor.Core;
using PlcMonitor.Engine;
using PlcMonitor.Engine.Configuration;
using PlcMonitor.Historian;

namespace PlcMonitor.Agent;

/// <summary>Opciones del agente (sección "Agent" de appsettings.json, variables PLCMON_Agent__* o --Agent:*).</summary>
public sealed class AgentOptions
{
    /// <summary>Fichero con los dispositivos y tags. Relativo a la carpeta del ejecutable.</summary>
    public string ConfigPath { get; set; } = "agent.json";
}

/// <summary>Carga la configuración, arranca el motor con el servicio y lo para al salir.</summary>
public sealed class EngineHost(IOptions<AgentOptions> options, DriverRegistry registry, ILoggerFactory loggerFactory, ILogger<EngineHost> logger)
    : IHostedService, IAsyncDisposable
{
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private TagEngine? _engine;
    private HistorianService? _historian;
    private int _disposed;

    public string ConfigPath { get; } = Path.GetFullPath(options.Value.ConfigPath, AppContext.BaseDirectory);

    public DateTimeOffset Started { get; private set; }

    public TagEngine Engine => _engine ?? throw new InvalidOperationException("El motor no está arrancado.");

    /// <summary>Historiador (su carpeta es relativa a la del fichero de configuración).</summary>
    public HistorianService Historian => _historian ?? throw new InvalidOperationException("El motor no está arrancado.");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        AgentConfiguration configuration;
        if (File.Exists(ConfigPath))
        {
            configuration = await ConfigurationFile.LoadAsync(ConfigPath, cancellationToken);
            logger.LogInformation("Configuración cargada de {Path}: {Count} dispositivos", ConfigPath, configuration.Devices.Count);
        }
        else
        {
            configuration = new AgentConfiguration();
            logger.LogWarning("No existe {Path}: el agente arranca sin dispositivos", ConfigPath);
        }

        _engine = new TagEngine(configuration, registry, loggerFactory);
        // El historiador se suscribe antes de arrancar el motor para no perder los primeros valores.
        _historian = new HistorianService(_engine, configuration.Historian ?? new HistorianSettings(), Path.GetDirectoryName(ConfigPath)!,
            loggerFactory.CreateLogger<HistorianService>());
        await _historian.StartAsync();
        await _engine.StartAsync();
        Started = DateTimeOffset.UtcNow;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_engine is not null)
            await _engine.StopAsync();
        if (_historian is not null)
            await _historian.StopAsync();
    }

    /// <summary>Guarda en disco la configuración actual del motor (con los cambios hechos en caliente).</summary>
    public async Task SaveConfigurationAsync(CancellationToken cancellationToken)
    {
        await _saveLock.WaitAsync(cancellationToken);
        try
        {
            await ConfigurationFile.SaveAsync(ConfigPath, Engine.SnapshotConfiguration(), cancellationToken);
            logger.LogInformation("Configuración guardada en {Path}", ConfigPath);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // El contenedor lo libera dos veces: como singleton y como servicio alojado.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        if (_historian is not null)
            await _historian.DisposeAsync();
        if (_engine is not null)
            await _engine.DisposeAsync();
        _saveLock.Dispose();
    }
}
