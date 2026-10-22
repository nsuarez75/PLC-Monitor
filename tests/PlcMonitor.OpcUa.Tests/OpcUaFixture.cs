using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using PlcMonitor.Core;
using PlcMonitor.Engine;
using PlcMonitor.Engine.Configuration;
using PlcMonitor.Engine.Tests;
using PlcMonitor.Historian;

namespace PlcMonitor.OpcUa.Tests;

/// <summary>
/// Motor con driver simulado, historiador opcional y servidor OPC UA en un puerto libre de localhost, con su PKI en una
/// carpeta temporal. Incluye un cliente OPC UA real (de la misma pila) para conectarse como lo haría Ignition o UaExpert.
/// </summary>
internal sealed class OpcUaFixture : IAsyncDisposable
{
    public const string Reader = "lector", Writer = "operador", Password = "clave-de-prueba";

    private readonly List<ISession> _sessions = [];

    public OpcUaFixture(bool historian = false, params DeviceConfiguration[] devices)
    {
        Configuration = new AgentConfiguration { Devices = [.. devices] };
        Engine = new TagEngine(Configuration, new DriverRegistry([Driver]), NullLoggerFactory.Instance);
        if (historian)
            Historian = new HistorianService(Engine, new HistorianSettings { Path = "history", FlushIntervalMs = 200 }, Directory, NullLogger<HistorianService>.Instance);
        Host = new OpcUaServerHost(Engine, Historian, Directory, NullLoggerFactory.Instance) { HostName = "localhost" };
        Port = FreePort();
    }

    public FakeDriverFactory Driver { get; } = new();

    public AgentConfiguration Configuration { get; }

    public TagEngine Engine { get; }

    public HistorianService? Historian { get; }

    public OpcUaServerHost Host { get; }

    public int Port { get; }

    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("plcmon-opcua-").FullName;

    public string Url => $"opc.tcp://localhost:{Port}";

    public static DeviceConfiguration Device(string name, bool writesEnabled = false, params TagConfiguration[] tags) => new()
    {
        Name = name,
        Driver = FakeDriverFactory.Id,
        Enabled = true,
        WritesEnabled = writesEnabled,
        Settings = FakeDriverFactory.Settings(),
        Reconnect = new ReconnectSettings { InitialDelayMs = 100, MaxDelayMs = 200 },
        ScanGroups = { new ScanGroupConfiguration { Name = "g", IntervalMs = 50, Tags = [.. tags] } },
    };

    public static TagConfiguration Tag(string address, string name, bool writable = false, string? folder = null, bool historized = false) => new()
    {
        Address = address,
        Name = name,
        Writable = writable,
        Folder = folder,
        History = historized ? new TagHistorySettings { Enabled = true } : null,
    };

    /// <summary>Ajustes con un usuario de lectura y otro de escritura.</summary>
    public OpcUaSettings Settings(bool enabled = true, bool allowWrites = false, int? port = null) => new()
    {
        Enabled = enabled,
        Port = port ?? Port,
        AllowWrites = allowWrites,
        Users =
        [
            new OpcUaUser { Name = Reader, Role = OpcUaRole.Read, PasswordHash = OpcUaPassword.Hash(Password) },
            new OpcUaUser { Name = Writer, Role = OpcUaRole.Write, PasswordHash = OpcUaPassword.Hash(Password) },
        ],
    };

    /// <summary>Arranca historiador, motor y servidor (con los ajustes indicados, por defecto activo y sin escritura).</summary>
    public async Task StartAsync(OpcUaSettings? settings = null)
    {
        if (Historian is not null)
            await Historian.StartAsync();
        await Engine.StartAsync();
        await Host.ApplyAsync(settings ?? Settings(), TestContext.Current.CancellationToken);
        Host.Status.State.ShouldBe(OpcUaState.Running, Host.Status.Detail);
    }

    /// <summary>
    /// Conecta como un cliente nuevo: la primera vez el servidor rechaza su certificado (queda en «rechazados») y se acepta
    /// como haría el usuario desde la app.
    /// </summary>
    public async Task<ISession> ConnectAsync(string user = Reader, string password = Password)
    {
        // El servidor guarda el certificado rechazado justo después de responder, y vuelve a leer la lista de confianza al
        // ver el cambio en la carpeta: se acepta lo pendiente y se reintenta un poco.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return Track(await Client.ConnectAsync(Url, user, password));
            }
            catch (ServiceResultException ex) when (ex.StatusCode == StatusCodes.BadSecurityChecksFailed && attempt < 40)
            {
                foreach (var pending in Host.ListClientCertificates().Where(c => !c.Trusted))
                    Host.TrustClientCertificate(pending.Thumbprint);
                await Task.Delay(250);
            }
        }
    }

    private ISession Track(ISession session)
    {
        _sessions.Add(session);
        return session;
    }

    public static NodeId Node(ISession session, string id) => new(id, (ushort)session.NamespaceUris.GetIndex(PlcNodeManager.NamespaceUri));

    /// <summary>Repite hasta que se cumple la condición (máximo 10 s).</summary>
    public static async Task<T> EventuallyAsync<T>(Func<Task<T>> call, Func<T, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var result = await call();
            if (condition(result))
                return result;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"No se cumplió la condición. Último resultado: {result}");
            await Task.Delay(50);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions)
        {
            try
            {
                await session.CloseAsync();
            }
            catch (ServiceResultException)
            {
                // El servidor ya puede estar parado.
            }
            session.Dispose();
        }
        await Host.DisposeAsync();
        if (Historian is not null)
            await Historian.DisposeAsync();
        await Engine.DisposeAsync();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Carpeta temporal: puede quedar algún fichero abierto un instante.
        }
    }

    /// <summary>Cliente OPC UA de prueba (uno para todas las pruebas: crear sus certificados lleva su tiempo).</summary>
    internal static class Client
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static ApplicationConfiguration? _configuration;

        public static ITelemetryContext Telemetry { get; } = DefaultTelemetry.Create(l => l.SetMinimumLevel(LogLevel.Warning));

        public static async Task<ISession> ConnectAsync(string url, string user, string password)
        {
            var configuration = await ConfigurationAsync();
            var description = await CoreClientUtils.SelectEndpointAsync(configuration, url, useSecurity: true, telemetry: Telemetry);
            var endpoint = new ConfiguredEndpoint(null, description!, EndpointConfiguration.Create(configuration));
            var identity = new UserIdentity(user, System.Text.Encoding.UTF8.GetBytes(password));
            return await new DefaultSessionFactory(Telemetry).CreateAsync(configuration, endpoint, false, false, "plcmon-tests", 60_000, identity, default);
        }

        private static async Task<ApplicationConfiguration> ConfigurationAsync()
        {
            await Gate.WaitAsync();
            try
            {
                if (_configuration is not null)
                    return _configuration;
                var pki = Path.Combine(Path.GetTempPath(), "plcmon-opcua-client-pki");
                var application = new ApplicationInstance(Telemetry) { ApplicationName = "PlcMonitorTests" };
                var certificates = ApplicationConfigurationBuilder.CreateDefaultApplicationCertificates("CN=PlcMonitorTests, DC=localhost", CertificateStoreType.Directory, pki);
                _configuration = await application.Build("urn:localhost:plcmon-tests", "urn:plcmon-tests").AsClient()
                    .AddSecurityConfiguration(certificates, pki)
                    // El cliente de prueba confía en cualquier servidor: lo que se prueba es la confianza del servidor.
                    .SetAutoAcceptUntrustedCertificates(true)
                    .CreateAsync();
                await application.CheckApplicationInstanceCertificatesAsync(true);
                return _configuration;
            }
            finally
            {
                Gate.Release();
            }
        }
    }
}
