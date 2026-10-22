using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.Agent.Contracts.Client;
using PlcMonitor.Engine.Configuration;
using Microsoft.Extensions.Configuration;
using static PlcMonitor.Agent.Tests.AgentFactory;

namespace PlcMonitor.Agent.Tests;

/// <summary>Token de acceso y TLS con certificado fijado.</summary>
public class AgentSecurityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Tokens_are_random_and_only_their_hash_matches()
    {
        var token = AgentSecurity.NewToken();
        var hash = AgentSecurity.HashToken(token);

        token.Length.ShouldBe(43);
        AgentSecurity.NewToken().ShouldNotBe(token);
        AgentSecurity.Matches(token, hash).ShouldBeTrue();
        AgentSecurity.Matches(token + "x", hash).ShouldBeFalse();
        AgentSecurity.Matches(null, hash).ShouldBeFalse();
        AgentSecurity.Matches(token, "no-es-hex").ShouldBeFalse();
        AgentSecurity.Matches(token, "").ShouldBeFalse();
    }

    [Fact]
    public void Certificate_has_the_host_names_and_a_stable_thumbprint()
    {
        var (pfx, thumbprint) = AgentSecurity.CreateCertificate("plcmon-agent", ["pc-linea", "192.168.0.5", "localhost"], "clave");

        using var cert = X509CertificateLoader.LoadPkcs12(pfx, "clave");
        AgentSecurity.Thumbprint(cert).ShouldBe(thumbprint);
        cert.HasPrivateKey.ShouldBeTrue();
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        san.EnumerateDnsNames().ShouldBe(["pc-linea", "localhost"]);
        san.EnumerateIPAddresses().ShouldBe([IPAddress.Parse("192.168.0.5")]);
        cert.NotAfter.ShouldBeGreaterThan(DateTime.Now.AddYears(9));
    }

    [Fact]
    public async Task Agent_with_token_rejects_calls_without_it()
    {
        var token = AgentSecurity.NewToken();
        await using var agent = new AgentFactory();
        agent.Settings["Agent:Security:TokenSha256"] = AgentSecurity.HashToken(token);
        var client = agent.CreateGrpcClient();

        var missing = await Should.ThrowAsync<RpcException>(async () => await client.GetInfoAsync(new Empty(), cancellationToken: Ct));
        var wrong = await Should.ThrowAsync<RpcException>(async () =>
            await client.GetInfoAsync(new Empty(), new Metadata { { "authorization", "Bearer otro" } }, cancellationToken: Ct));
        var info = await client.GetInfoAsync(new Empty(), new Metadata { { "authorization", $"Bearer {token}" } }, cancellationToken: Ct);

        missing.StatusCode.ShouldBe(StatusCode.Unauthenticated);
        wrong.StatusCode.ShouldBe(StatusCode.Unauthenticated);
        info.Machine.ShouldBe(Environment.MachineName);
        using var call = client.Subscribe(new SubscribeRequest(), cancellationToken: Ct);
        (await Should.ThrowAsync<RpcException>(async () => await call.ResponseStream.MoveNext(Ct))).StatusCode.ShouldBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task Agent_without_token_stays_open_for_local_use()
    {
        await using var agent = new AgentFactory();
        var info = await agent.CreateGrpcClient().GetInfoAsync(new Empty(), cancellationToken: Ct);

        info.Machine.ShouldBe(Environment.MachineName);
        info.Access.ShouldBe(AccessLevel.Full);
    }

    [Fact]
    public async Task Read_only_token_can_watch_but_not_change_anything()
    {
        var full = AgentSecurity.NewToken();
        var reader = AgentSecurity.NewToken();
        await using var agent = new AgentFactory(Device("plc1", writesEnabled: true, tags: [new TagConfiguration { Address = "DB1.DBW2", Name = "rw", Writable = true }]));
        agent.Settings["Agent:Security:TokenSha256"] = AgentSecurity.HashToken(full);
        agent.Settings["Agent:Security:ReadOnlyTokenSha256"] = AgentSecurity.HashToken(reader);
        agent.Driver.Plc("plc1").Values["DB1.DBW2"] = 0;
        var client = agent.CreateGrpcClient();
        var asReader = new Metadata { { "authorization", $"Bearer {reader}" } };
        var asFull = new Metadata { { "authorization", $"Bearer {full}" } };

        (await client.GetInfoAsync(new Empty(), asReader, cancellationToken: Ct)).Access.ShouldBe(AccessLevel.Read);
        (await client.GetInfoAsync(new Empty(), asFull, cancellationToken: Ct)).Access.ShouldBe(AccessLevel.Full);
        (await client.ListTagsAsync(new ListTagsRequest(), asReader, cancellationToken: Ct)).Tags.ShouldHaveSingleItem();
        (await client.GetConfigurationAsync(new Empty(), asReader, cancellationToken: Ct)).Devices.ShouldHaveSingleItem();
        using (var call = client.Subscribe(new SubscribeRequest(), asReader, cancellationToken: Ct))
            (await call.ResponseStream.MoveNext(Ct)).ShouldBeTrue();

        async Task Denied(Func<Task> call) => (await Should.ThrowAsync<RpcException>(call)).StatusCode.ShouldBe(StatusCode.PermissionDenied);
        await Denied(async () => await client.WriteAsync(new WriteRequest { Device = "plc1", Tag = "rw", Value = "7" }, asReader, cancellationToken: Ct));
        await Denied(async () => await client.SetDeviceEnabledAsync(new SetDeviceEnabledRequest { Device = "plc1", Enabled = false }, asReader, cancellationToken: Ct));
        await Denied(async () => await client.DeleteDeviceAsync(new DeleteDeviceRequest { Device = "plc1" }, asReader, cancellationToken: Ct));
        await Denied(async () => await client.PutDeviceAsync(new PutDeviceRequest { Device = new DeviceConfig { Name = "otro" } }, asReader, cancellationToken: Ct));
        await Denied(async () => await client.PutTransformsAsync(new PutTransformsRequest(), asReader, cancellationToken: Ct));
        await Denied(async () => await client.RenameHistoryAsync(new RenameHistoryRequest(), asReader, cancellationToken: Ct));
        await Denied(async () => await client.DeleteHistoryAsync(new DeleteHistoryRequest(), asReader, cancellationToken: Ct));
        agent.Driver.Plc("plc1").Writes.ShouldBeEmpty();
        (await client.ListDevicesAsync(new Empty(), asFull, cancellationToken: Ct)).Devices.ShouldHaveSingleItem().Enabled.ShouldBeTrue();
    }

    [Fact]
    public void Only_the_methods_that_change_something_need_full_access()
    {
        var all = AgentService.Descriptor.Methods.Select(m => m.Name).ToList();

        TokenAuthInterceptor.FullAccessMethods.ShouldBeSubsetOf(all);
        all.Except(TokenAuthInterceptor.FullAccessMethods).ShouldBe(
            ["GetInfo", "ListDevices", "GetDeviceInfo", "ListTags", "Read", "Subscribe", "Browse", "GetConfiguration", "GetDeviceConfig", "QueryHistory", "GetTransforms"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Audit_log_names_the_credential_besides_the_declared_user()
    {
        var full = AgentSecurity.NewToken();
        await using var agent = new AgentFactory(Device("plc1", writesEnabled: true, tags: [new TagConfiguration { Address = "DB1.DBW2", Name = "rw", Writable = true }]));
        agent.Settings["Agent:Security:TokenSha256"] = AgentSecurity.HashToken(full);
        agent.Driver.Plc("plc1").Values["DB1.DBW2"] = 0;
        var client = agent.CreateGrpcClient();
        var asFull = new Metadata { { "authorization", $"Bearer {full}" } };
        await EventuallyAsync(async () => await client.ListTagsAsync(new ListTagsRequest(), asFull, cancellationToken: Ct), t => t.Tags.All(x => x.Value.Quality == Quality.Good));

        await client.WriteAsync(new WriteRequest { Device = "plc1", Tag = "rw", Value = "7", User = "nacho" }, asFull, cancellationToken: Ct);
        await client.SetDeviceEnabledAsync(new SetDeviceEnabledRequest { Device = "plc1", Enabled = false }, asFull, cancellationToken: Ct);
        await client.DeleteDeviceAsync(new DeleteDeviceRequest { Device = "plc1" }, asFull, cancellationToken: Ct);

        var log = agent.AuditLog.ToList();
        log.ShouldContain(l => l.StartsWith("Escritura plc1/rw", StringComparison.Ordinal) && l.Contains("por nacho@") && l.Contains("(token completo)"));
        log.ShouldContain(l => l.StartsWith("Dispositivo plc1 desactivado por anónimo@", StringComparison.Ordinal) && l.EndsWith("(token completo)", StringComparison.Ordinal));
        log.ShouldContain(l => l.StartsWith("Dispositivo plc1 dado de baja por anónimo@", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5310", true)]
    [InlineData("https://localhost:5311", true)]
    [InlineData("http://[::1]:5310", true)]
    [InlineData("https://0.0.0.0:5311", false)]
    [InlineData("http://*:5310", false)]
    [InlineData("http://+:5310", false)]
    [InlineData("http://[::]:5310", false)]
    [InlineData("http://192.168.0.5:5310", false)]
    [InlineData("http://pc-linea:5310", false)]
    public void Loopback_urls_are_recognised(string url, bool loopback) => ListenGuard.IsLoopback(url).ShouldBe(loopback);

    [Fact]
    public void Without_token_the_agent_may_only_listen_on_this_machine()
    {
        static IConfiguration Config(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();
        var open = new AgentSecurityOptions();
        var protectedByReader = new AgentSecurityOptions { ReadOnlyTokenSha256 = AgentSecurity.HashToken("x") };

        ListenGuard.Problem(Config(("Kestrel:Endpoints:Grpc:Url", "http://127.0.0.1:5310")), open).ShouldBeNull();
        ListenGuard.Problem(Config(("Kestrel:Endpoints:Grpc:Url", "https://0.0.0.0:5311")), open).ShouldNotBeNull().ShouldContain("https://0.0.0.0:5311");
        ListenGuard.Problem(Config(("urls", "http://127.0.0.1:1;http://*:2")), open).ShouldNotBeNull().ShouldContain("http://*:2");
        ListenGuard.Problem(Config(("Kestrel:Endpoints:Grpc:Url", "https://0.0.0.0:5311")), protectedByReader).ShouldBeNull();
    }

    /// <summary>El agente real se niega a arrancar si se abre a la red sin token.</summary>
    [Fact]
    public async Task Real_agent_refuses_to_listen_on_the_network_without_token()
    {
        var dir = Directory.CreateTempSubdirectory("plcmon-open-").FullName;
        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList =
            {
                Path.Combine(AppContext.BaseDirectory, "plcmon-agent.dll"),
                $"--Agent:ConfigPath={Path.Combine(dir, "agent.json")}",
                "--Agent:Historian:Enabled=false",
                $"--Kestrel:Endpoints:Grpc:Url=http://0.0.0.0:{FreePort()}",
                $"--Logging:File:Path={Path.Combine(dir, "logs")}",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(Ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);

            process.ExitCode.ShouldBe(1);
            (await output).ShouldContain("sin token de acceso");
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>El agente real (proceso) con HTTPS, certificado propio y token, como lo deja el instalador.</summary>
    [Fact]
    public async Task Real_agent_over_tls_requires_the_pinned_certificate_and_the_token()
    {
        var dir = Directory.CreateTempSubdirectory("plcmon-tls-").FullName;
        var token = AgentSecurity.NewToken();
        var (pfx, thumbprint) = AgentSecurity.CreateCertificate("plcmon-agent", ["localhost", "127.0.0.1"], "clave");
        await File.WriteAllBytesAsync(Path.Combine(dir, "agent.pfx"), pfx, Ct);
        var port = FreePort();
        var agentDll = Path.Combine(AppContext.BaseDirectory, "plcmon-agent.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList =
            {
                agentDll,
                $"--Agent:ConfigPath={Path.Combine(dir, "agent.json")}",
                $"--Agent:Security:TokenSha256={AgentSecurity.HashToken(token)}",
                $"--Kestrel:Endpoints:Grpc:Url=https://127.0.0.1:{port}",
                $"--Kestrel:Endpoints:Grpc:Certificate:Path={Path.Combine(dir, "agent.pfx")}",
                "--Kestrel:Endpoints:Grpc:Certificate:Password=clave",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        try
        {
            var url = $"https://127.0.0.1:{port}";
            using var good = AgentChannelFactory.Create(new AgentEndpoint(url, token, thumbprint));
            var client = new AgentService.AgentServiceClient(good);
            AgentInfo? info = null;
            for (var i = 0; i < 100 && info is null; i++)
            {
                try
                {
                    info = await client.GetInfoAsync(new Empty(), cancellationToken: Ct);
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
                {
                    await Task.Delay(100, Ct);
                }
            }
            info.ShouldNotBeNull("el agente no arrancó con TLS");

            using var noToken = AgentChannelFactory.Create(new AgentEndpoint(url, null, thumbprint));
            (await Should.ThrowAsync<RpcException>(async () =>
                await new AgentService.AgentServiceClient(noToken).GetInfoAsync(new Empty(), cancellationToken: Ct))).StatusCode.ShouldBe(StatusCode.Unauthenticated);

            var otherThumbprint = AgentSecurity.CreateCertificate("otro", ["localhost"], "x").Thumbprint;
            using var wrongCert = AgentChannelFactory.Create(new AgentEndpoint(url, token, otherThumbprint));
            var rejected = await Should.ThrowAsync<RpcException>(async () =>
                await new AgentService.AgentServiceClient(wrongCert).GetInfoAsync(new Empty(), cancellationToken: Ct));
            AgentErrors.IsCertificateMismatch(rejected).ShouldBeTrue(rejected.ToString());
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
