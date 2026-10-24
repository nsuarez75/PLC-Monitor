using Microsoft.Extensions.Options;
using PlcMonitor.Core;
using PlcMonitor.Engine;
using PlcMonitor.Engine.Configuration;
using PlcMonitor.Historian;
using PlcMonitor.OpcUa;

namespace PlcMonitor.Agent;

/// <summary>Opciones del agente (sección "Agent" de agent.settings.json, variables PLCMON_Agent__* o --Agent:*).</summary>
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
    private OpcUaServerHost? _opcUa;
    private int _disposed;

    public string ConfigPath { get; } = Path.GetFullPath(options.Value.ConfigPath, AppContext.BaseDirectory);

    /// <summary>Cifrado de las contraseñas de los dispositivos (clave en agent.key junto a la configuración).</summary>
    public ConfigSecrets Secrets { get; } = new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Value.ConfigPath, AppContext.BaseDirectory))!, "agent.key"));

    public DateTimeOffset Started { get; private set; }

    public TagEngine Engine => _engine ?? throw new InvalidOperationException("El motor no está arrancado.");

    /// <summary>Historiador (su carpeta es relativa a la del fichero de configuración).</summary>
    public HistorianService Historian => _historian ?? throw new InvalidOperationException("El motor no está arrancado.");

    /// <summary>Servidor OPC UA (su PKI va en opcua/ junto al fichero de configuración).</summary>
    public OpcUaServerHost OpcUa => _opcUa ?? throw new InvalidOperationException("El motor no está arrancado.");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        AgentConfiguration configuration;
        if (File.Exists(ConfigPath))
        {
            configuration = await ConfigurationFile.LoadAsync(ConfigPath, cancellationToken);
            logger.LogInformation("Configuración cargada de {Path}: {Count} dispositivos", ConfigPath, configuration.Devices.Count);
            var (plain, undecryptable) = Secrets.Unprotect(configuration);
            foreach (var device in undecryptable)
                logger.LogError("No se pudo descifrar la contraseña de {Device} con {Key}: vuelve a introducirla", device, Secrets.KeyPath);
            if (plain.Count > 0)
                await MigrateAsync(configuration, plain, cancellationToken);
        }
        else
        {
            configuration = new AgentConfiguration();
            logger.LogWarning("No existe {Path}: el colector arranca sin dispositivos", ConfigPath);
        }

        _engine = new TagEngine(configuration, registry, loggerFactory);
        // El historiador se suscribe antes de arrancar el motor para no perder los primeros valores.
        _historian = new HistorianService(_engine, configuration.Historian ?? new HistorianSettings(), Path.GetDirectoryName(ConfigPath)!,
            loggerFactory.CreateLogger<HistorianService>());
        await _historian.StartAsync();
        await _engine.StartAsync();
        Started = DateTimeOffset.UtcNow;
        // Si el servidor OPC UA no arranca (puerto ocupado...) el agente sigue: el motivo queda en su estado y en el log.
        _opcUa = new OpcUaServerHost(_engine, _historian, Path.GetDirectoryName(ConfigPath)!, loggerFactory);
        await _opcUa.ApplyAsync(configuration.OpcUa ?? new OpcUaSettings(), cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_opcUa is not null)
            await _opcUa.DisposeAsync();
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
            await WriteAsync(Engine.SnapshotConfiguration(), cancellationToken);
            logger.LogInformation("Configuración guardada en {Path}", ConfigPath);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>Contraseñas en claro (agent.json editado a mano o de una versión anterior): se guardan ya cifradas.</summary>
    private async Task MigrateAsync(AgentConfiguration configuration, List<string> devices, CancellationToken cancellationToken)
    {
        try
        {
            await WriteAsync(ConfigurationFile.Parse(ConfigurationFile.Serialize(configuration)), cancellationToken);
            logger.LogInformation("Contraseñas de {Devices} cifradas en {Path}", string.Join(", ", devices), ConfigPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "No se pudieron cifrar las contraseñas de {Path}: siguen en claro", ConfigPath);
        }
    }

    /// <summary>Escribe una copia de la configuración con las contraseñas cifradas y el fichero solo legible por el servicio.</summary>
    private async Task WriteAsync(AgentConfiguration copy, CancellationToken cancellationToken)
    {
        Secrets.Protect(copy);
        await ConfigurationFile.SaveAsync(ConfigPath, copy, cancellationToken);
        ConfigSecrets.RestrictToOwner(ConfigPath);
    }

    public async ValueTask DisposeAsync()
    {
        // El contenedor lo libera dos veces: como singleton y como servicio alojado.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        if (_opcUa is not null)
            await _opcUa.DisposeAsync();
        if (_historian is not null)
            await _historian.DisposeAsync();
        if (_engine is not null)
            await _engine.DisposeAsync();
        _saveLock.Dispose();
    }
}
