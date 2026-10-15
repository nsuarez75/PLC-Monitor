using Microsoft.AspNetCore.Server.Kestrel.Core;
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
builder.Configuration.AddEnvironmentVariables("PLCMON_");
builder.Services.AddWindowsService(o => o.ServiceName = "PLC-Monitor Agent");
builder.Services.AddSystemd();
builder.WebHost.ConfigureKestrel(k => k.ConfigureEndpointDefaults(e => e.Protocols = HttpProtocols.Http2));

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddSingleton(new DriverRegistry([new S7SymbolicDriverFactory(), new S7AbsoluteDriverFactory()]));
builder.Services.AddSingleton<EngineHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EngineHost>());
builder.Services.AddGrpc();

var app = builder.Build();

app.MapGrpcService<AgentGrpcService>();
await app.RunAsync();

/// <summary>Punto de entrada (público para las pruebas con WebApplicationFactory).</summary>
public partial class Program;
