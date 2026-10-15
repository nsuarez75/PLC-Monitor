using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.Agent.Contracts.Client;

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
        (await agent.CreateGrpcClient().GetInfoAsync(new Empty(), cancellationToken: Ct)).Machine.ShouldBe(Environment.MachineName);
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
