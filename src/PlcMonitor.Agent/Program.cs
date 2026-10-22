using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration.Memory;
using PlcMonitor.Agent;
using PlcMonitor.Core;
using PlcMonitor.Drivers.S7Absolute;
using PlcMonitor.Drivers.S7Symbolic;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Como servicio, el directorio de trabajo es System32 o /: la configuración se busca junto al ejecutable.
    ContentRootPath = AppContext.BaseDirectory,
});
// Valores por defecto (el agente se publica como un único ejecutable, sin appsettings.json al lado): por debajo de
// cualquier otra fuente. Solo local; para aceptar conexiones remotas, agent.settings.json o --Kestrel:Endpoints:Grpc:Url.
builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource
{
    InitialData = new Dictionary<string, string?>
    {
        ["Kestrel:Endpoints:Grpc:Url"] = "http://127.0.0.1:5310",
        ["Logging:LogLevel:Default"] = "Information",
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
        ["Logging:LogLevel:PlcMonitor.Audit"] = "Information",
    },
});
// agent.settings.json: lo escribe el instalador (puerto, certificado TLS, hash del token) y no lo pisan las actualizaciones.
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "agent.settings.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("PLCMON_");
builder.Services.AddWindowsService(o => o.ServiceName = "PLC-Monitor Agent");
// Log a fichero: en Windows siempre (logs\ junto al ejecutable; el Visor de eventos solo guarda avisos y errores); en
// Linux ya está journald, solo si se pide con Logging:File:Path.
if (!string.IsNullOrEmpty(builder.Configuration["Logging:File:Path"]) || OperatingSystem.IsWindows())
{
    var directory = Path.GetFullPath(builder.Configuration["Logging:File:Path"] is { Length: > 0 } configured ? configured : "logs", AppContext.BaseDirectory);
    var retention = builder.Configuration.GetValue("Logging:File:RetentionDays", 7);
    builder.Logging.AddProvider(new FileLoggerProvider(directory, retention));
}
builder.Services.AddSystemd();
// Parada rápida: las suscripciones se cierran al empezar a parar (ApplicationStopping) y, si algo se resiste, el host
// no espera más de 4 s (systemd en algunas distribuciones mata el servicio a los 5 s).
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(4));
builder.WebHost.ConfigureKestrel(k => k.ConfigureEndpointDefaults(e => e.Protocols = HttpProtocols.Http2));

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.Configure<AgentSecurityOptions>(builder.Configuration.GetSection("Agent:Security"));
builder.Services.AddSingleton(new DriverRegistry([new S7SymbolicDriverFactory(), new S7AbsoluteDriverFactory()]));
builder.Services.AddSingleton<EngineHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EngineHost>());
builder.Services.AddGrpc(o => o.Interceptors.Add<TokenAuthInterceptor>());

var app = builder.Build();

// Sin token solo se escucha en este equipo: si la configuración abre la API a la red sin protección, no se arranca.
if (ListenGuard.Problem(app.Configuration, app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentSecurityOptions>>().Value) is { } problem)
{
    app.Logger.LogCritical("{Problem}", problem);
    await app.DisposeAsync();
    return 1;
}

app.MapGrpcService<AgentGrpcService>();
await app.RunAsync();
return 0;

/// <summary>Punto de entrada (público para las pruebas con WebApplicationFactory).</summary>
public partial class Program;
