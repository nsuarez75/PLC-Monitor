using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Identity;
using Opc.Ua.Server;
using Opc.Ua.Server.Hosting;
using PlcMonitor.Engine;
using PlcMonitor.Engine.Configuration;
using PlcMonitor.Historian;

namespace PlcMonitor.OpcUa;

public enum OpcUaState
{
    Stopped,
    Starting,
    Running,
    Error,
}

/// <summary>Estado del servidor OPC UA.</summary>
/// <param name="EndpointUrl">URL para los clientes (con el nombre del equipo).</param>
/// <param name="CertificateThumbprint">Huella SHA-1 del certificado del servidor (la que muestran Ignition y UaExpert).</param>
public sealed record OpcUaStatus(OpcUaState State, string? Detail = null, string? EndpointUrl = null, string? CertificateThumbprint = null);

/// <summary>Certificado de un cliente OPC UA, rechazado (pendiente de aceptar) o de confianza.</summary>
public sealed record OpcUaClientCertificate(string Thumbprint, string Subject, string? ApplicationUri, DateTimeOffset NotAfter, bool Trusted);

/// <summary>
/// Aloja el servidor OPC UA del agente y lo arranca, para o reinicia según los ajustes. Los usuarios y la tercera llave
/// se aplican en caliente; el puerto, reiniciando. La PKI (certificado propio y de los clientes) va en
/// <c>opcua/pki</c> junto a la configuración, solo accesible para la cuenta del servicio.
/// </summary>
public sealed class OpcUaServerHost : IAsyncDisposable
{
    private const string ApplicationName = "PLC-Monitor";
    private const string ProductUri = "urn:plc-monitor";
    /// <summary>El certificado propio dura 10 años, como el del agente: si caducara, los clientes tendrían que volver a confiar en él.</summary>
    private const ushort CertificateLifetimeMonths = 120;

    private readonly TagEngine _engine;
    private readonly HistorianService? _historian;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile OpcUaSettings _settings = new();
    private ServiceProvider? _services;
    private BackgroundService? _server;
    private PlcNodeManager? _manager;
    private int _runningPort;
    private int _disposed;

    public OpcUaServerHost(TagEngine engine, HistorianService? historian, string directory, ILoggerFactory loggerFactory)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _historian = historian;
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<OpcUaServerHost>();
        Directory = Path.Combine(directory, "opcua");
        PkiRoot = Path.Combine(Directory, "pki");
    }

    /// <summary>Carpeta del servidor OPC UA (opcua/ junto a la configuración).</summary>
    public string Directory { get; }

    public string PkiRoot { get; }

    /// <summary>Nombre del equipo en las URL y en el certificado (en las pruebas, localhost).</summary>
    public string HostName { get; init; } = Dns.GetHostName();

    /// <summary>Tiempo máximo de arranque (la primera vez crea los certificados).</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public OpcUaStatus Status { get; private set; } = new(OpcUaState.Stopped);

    private string ApplicationUri => $"urn:{HostName}:plc-monitor";

    private string SubjectName => $"CN={ApplicationName}, DC={HostName}";

    /// <summary>Aplica los ajustes: para, arranca o reinicia el servidor si hace falta.</summary>
    public async Task ApplyAsync(OpcUaSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            _settings = settings;
            var running = _server is not null && Status.State == OpcUaState.Running;
            if (!settings.Enabled)
            {
                await StopCoreAsync();
                Status = new OpcUaStatus(OpcUaState.Stopped);
                return;
            }
            if (running && settings.Port == _runningPort)
            {
                if (_manager is not null)
                    await _manager.RefreshAccessAsync();
                return;
            }
            await StopCoreAsync();
            await StartCoreAsync(settings, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task StartCoreAsync(OpcUaSettings settings, CancellationToken cancellationToken)
    {
        var port = settings.Port;
        var url = $"opc.tcp://{HostName}:{port}";
        Status = new OpcUaStatus(OpcUaState.Starting, null, url);
        if (!IsPortFree(port))
        {
            Status = new OpcUaStatus(OpcUaState.Error, $"El puerto {port} está ocupado por otro programa.", url);
            _logger.LogError("OPC UA: el puerto {Port} está ocupado; el servidor no arranca", port);
            return;
        }
        try
        {
            CreateRestrictedDirectory(Directory);
            await EnsureCertificateAsync(url, cancellationToken);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var services = new ServiceCollection();
            services.AddSingleton(_loggerFactory);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            services.AddOpcUa()
                .AddServer(o =>
                {
                    o.ApplicationName = ApplicationName;
                    o.ApplicationUri = ApplicationUri;
                    o.ProductUri = ProductUri;
                    o.SubjectName = SubjectName;
                    o.PkiRoot = PkiRoot;
                    o.EndpointUrls.Add(url);
                    // Solo canales firmados y cifrados (la pila no ofrece None si no se pide) y solo usuario y contraseña.
                    o.IncludeSignAndEncryptPolicies = true;
                    o.IncludeUnsecurePolicyNone = false;
                    o.AutoAcceptUntrustedCertificates = false;
                    o.UserTokenPolicies.Add(new OpcUaUserTokenPolicy { TokenType = UserTokenType.UserName });
                    o.ConfigureBuilder = builder => builder.SetMaxFailedAuthenticationAttempts(5);
                })
                .ConfigureServerProperties(p =>
                {
                    p.ManufacturerName = "PLC-Monitor";
                    p.ProductName = "PLC-Monitor Agent";
                    p.SoftwareVersion = typeof(OpcUaServerHost).Assembly.GetName().Version?.ToString(3) ?? "";
                })
                .AddNodeManager(new NodeManagerFactory(this))
                .AddIdentityAuthenticator(new UserNamePasswordAuthenticator(VerifyAsync))
                .AddStartupTask((_, _, _) =>
                {
                    ready.TrySetResult();
                    return ValueTask.CompletedTask;
                });
            _services = services.BuildServiceProvider();
            _server = (BackgroundService)_services.GetServices<IHostedService>().Single();
            await _server.StartAsync(cancellationToken);
            var started = await Task.WhenAny(ready.Task, _server.ExecuteTask ?? Task.Delay(Timeout.Infinite, cancellationToken), Task.Delay(StartTimeout, cancellationToken));
            if (started != ready.Task)
            {
                var reason = _server.ExecuteTask?.Exception?.GetBaseException().Message ?? "no arrancó a tiempo";
                await StopCoreAsync();
                Status = new OpcUaStatus(OpcUaState.Error, $"El servidor OPC UA no arrancó: {reason}", url);
                _logger.LogError("OPC UA: el servidor no arrancó: {Reason}", reason);
                return;
            }
            _runningPort = port;
            Status = new OpcUaStatus(OpcUaState.Running, null, url, ServerThumbprint());
            _logger.LogInformation("Servidor OPC UA en {Url} (certificado {Thumbprint})", url, Status.CertificateThumbprint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await StopCoreAsync();
            Status = new OpcUaStatus(OpcUaState.Error, $"El servidor OPC UA no arrancó: {ex.Message}", url);
            _logger.LogError(ex, "OPC UA: el servidor no arrancó");
        }
    }

    private async Task StopCoreAsync()
    {
        var server = _server;
        var services = _services;
        _server = null;
        _services = null;
        _manager = null;
        if (server is not null)
        {
            try
            {
                await server.StopAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "OPC UA: error al parar el servidor");
            }
            _logger.LogInformation("Servidor OPC UA parado");
        }
        if (services is not null)
            await services.DisposeAsync();
        if (Status.State != OpcUaState.Error)
            Status = new OpcUaStatus(OpcUaState.Stopped);
    }

    /// <summary>Usuario y contraseña contra los ajustes vigentes (se leen en cada sesión: los cambios valen al momento).</summary>
    private ValueTask<IUserIdentity> VerifyAsync(UserNameIdentityTokenHandler handler, CancellationToken cancellationToken)
    {
        var password = handler.DecryptedPassword is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;
        var user = _settings.Users.FirstOrDefault(u => string.Equals(u.Name, handler.UserName, StringComparison.OrdinalIgnoreCase));
        if (user is not null && OpcUaPassword.Verify(password, user.PasswordHash))
            return new ValueTask<IUserIdentity>(new UserIdentity(handler));
        _logger.LogWarning("OPC UA: usuario o contraseña incorrectos para '{User}'", handler.UserName);
        throw new ServiceResultException(StatusCodes.BadUserAccessDenied, "Usuario o contraseña incorrectos.");
    }

    // ---------------- Certificados ----------------

    /// <summary>
    /// Crea el certificado RSA propio a 10 años antes de arrancar (el servidor alojado lo reutiliza; si lo creara él,
    /// duraría un año).
    /// </summary>
    private async Task EnsureCertificateAsync(string url, CancellationToken cancellationToken)
    {
        var telemetry = DefaultTelemetry.Create(_ => { });
        await using var application = new ApplicationInstance(telemetry) { ApplicationName = ApplicationName, ApplicationType = ApplicationType.Server };
        CertificateIdentifier[] certificates =
        [
            new() { StoreType = CertificateStoreType.Directory, StorePath = PkiRoot, SubjectName = SubjectName, CertificateType = ObjectTypeIds.RsaSha256ApplicationCertificateType },
        ];
        await application.Build(ApplicationUri, ProductUri).AsServer([url]).AddSecurityConfiguration(certificates, PkiRoot).CreateAsync(cancellationToken);
        await application.CheckApplicationInstanceCertificatesAsync(true, CertificateLifetimeMonths, cancellationToken);
    }

    /// <summary>Huella del certificado RSA del servidor (el de los canales Basic256Sha256 y Aes).</summary>
    private string? ServerThumbprint()
    {
        var folder = Path.Combine(PkiRoot, "certs");
        if (!System.IO.Directory.Exists(folder))
            return null;
        foreach (var file in System.IO.Directory.GetFiles(folder, "*.der"))
        {
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(file);
            using var rsa = certificate.GetRSAPublicKey();
            if (rsa is not null)
                return certificate.Thumbprint;
        }
        return null;
    }

    /// <summary>Certificados de clientes: los rechazados (pendientes de aceptar) y los de confianza.</summary>
    public IReadOnlyList<OpcUaClientCertificate> ListClientCertificates() =>
        [.. Certificates("rejected", trusted: false), .. Certificates("trusted", trusted: true)];

    private IEnumerable<OpcUaClientCertificate> Certificates(string store, bool trusted)
    {
        var folder = Path.Combine(PkiRoot, store, "certs");
        if (!System.IO.Directory.Exists(folder))
            yield break;
        foreach (var file in System.IO.Directory.GetFiles(folder, "*.der").Order(StringComparer.Ordinal))
        {
            X509Certificate2 certificate;
            try
            {
                certificate = X509CertificateLoader.LoadCertificateFromFile(file);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                continue;
            }
            using (certificate)
            {
                var uri = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => UrisOf(e)).FirstOrDefault();
                yield return new OpcUaClientCertificate(certificate.Thumbprint, certificate.Subject, uri, certificate.NotAfter, trusted);
            }
        }
    }

    /// <summary>URI del nombre alternativo (SAN), donde OPC UA pone el ApplicationUri. .NET solo expone DNS e IP: se lee el ASN.1.</summary>
    private static List<string> UrisOf(X509SubjectAlternativeNameExtension extension)
    {
        var uris = new List<string>();
        try
        {
            var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
            var uri = new Asn1Tag(TagClass.ContextSpecific, 6);
            while (reader.HasData)
            {
                if (reader.PeekTag().HasSameClassAndValue(uri))
                    uris.Add(reader.ReadCharacterString(UniversalTagNumber.IA5String, uri));
                else
                    reader.ReadEncodedValue();
            }
        }
        catch (AsnContentException)
        {
            // SAN mal formado: sin URI.
        }
        return uris;
    }

    /// <summary>Acepta un certificado rechazado (pasa a confianza). La pila vuelve a leer la lista sola.</summary>
    public bool TrustClientCertificate(string thumbprint)
    {
        var file = Find("rejected", thumbprint);
        if (file is null)
            return false;
        var target = Path.Combine(PkiRoot, "trusted", "certs");
        System.IO.Directory.CreateDirectory(target);
        File.Move(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        _logger.LogInformation("OPC UA: certificado de cliente {Thumbprint} aceptado", thumbprint);
        return true;
    }

    /// <summary>Quita un certificado de cliente (rechazado o de confianza): ese cliente deja de poder conectar.</summary>
    public bool RemoveClientCertificate(string thumbprint)
    {
        var removed = false;
        foreach (var store in new[] { "rejected", "trusted" })
        {
            if (Find(store, thumbprint) is { } file)
            {
                File.Delete(file);
                removed = true;
            }
        }
        if (removed)
            _logger.LogInformation("OPC UA: certificado de cliente {Thumbprint} quitado", thumbprint);
        return removed;
    }

    private string? Find(string store, string thumbprint)
    {
        var folder = Path.Combine(PkiRoot, store, "certs");
        if (!System.IO.Directory.Exists(folder) || string.IsNullOrWhiteSpace(thumbprint))
            return null;
        var wanted = thumbprint.Replace(":", "", StringComparison.Ordinal).Trim();
        foreach (var file in System.IO.Directory.GetFiles(folder, "*.der"))
        {
            try
            {
                using var certificate = X509CertificateLoader.LoadCertificateFromFile(file);
                if (string.Equals(certificate.Thumbprint, wanted, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // Un fichero que no es un certificado no se toca.
            }
        }
        return null;
    }

    // ---------------- Utilidades ----------------

    /// <summary>
    /// El puerto está libre en IPv4 y en IPv6. Se prueban por separado: en Windows un socket dual se abre aunque otro
    /// programa ya tenga el puerto en IPv4, y la pila arrancaría a medias sin avisar.
    /// </summary>
    private static bool IsPortFree(int port)
    {
        foreach (var address in new[] { IPAddress.Any, IPAddress.IPv6Any })
        {
            try
            {
                using var listener = new TcpListener(address, port);
                if (address.AddressFamily == AddressFamily.InterNetworkV6)
                    listener.Server.DualMode = false;
                listener.Start();
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.AddressNotAvailable)
            {
                // Equipo sin IPv6.
            }
            catch (SocketException)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>La carpeta guarda la clave privada del servidor: solo la cuenta del servicio (Linux 0700; Windows SYSTEM, administradores y el usuario actual).</summary>
    private static void CreateRestrictedDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }
        var directory = System.IO.Directory.CreateDirectory(path);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (WindowsIdentity.GetCurrent().User is { } me)
            acl.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }

    /// <summary>Para el servidor. Idempotente: el agente lo libera al pararse y otra vez al liberar el contenedor.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        await _lock.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Crea el gestor de nodos de cada arranque y lo recuerda (para aplicar en caliente la tercera llave).</summary>
    private sealed class NodeManagerFactory(OpcUaServerHost host) : IAsyncNodeManagerFactory
    {
        public ArrayOf<string> NamespacesUris => new[] { PlcNodeManager.NamespaceUri };

        public ValueTask<IAsyncNodeManager> CreateAsync(IServerInternal server, ApplicationConfiguration configuration, CancellationToken cancellationToken = default)
        {
            var manager = new PlcNodeManager(server, configuration, host._engine, host._historian, () => host._settings,
                host._loggerFactory.CreateLogger<PlcNodeManager>());
            host._manager = manager;
            return new ValueTask<IAsyncNodeManager>(manager);
        }
    }
}
