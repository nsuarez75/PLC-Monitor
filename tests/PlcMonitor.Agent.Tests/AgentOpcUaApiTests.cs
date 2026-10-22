using System.Net;
using System.Net.Sockets;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using PlcMonitor.Agent.Contracts;

namespace PlcMonitor.Agent.Tests;

/// <summary>Servidor OPC UA gestionado por la API del agente.</summary>
public class AgentOpcUaApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static PutOpcUaRequest Enabled(int port, string password = "clave-de-prueba") => new()
    {
        Enabled = true,
        Port = port,
        Users = { new OpcUaUser { Name = "ignition", Role = OpcUaRole.Read, Password = password } },
    };

    [Fact]
    public async Task By_default_the_server_is_stopped_on_port_4840()
    {
        await using var agent = new AgentFactory();

        var info = await agent.CreateGrpcClient().GetOpcUaAsync(new Empty(), cancellationToken: Ct);

        info.Enabled.ShouldBeFalse();
        info.Port.ShouldBe(4840);
        info.State.ShouldBe(OpcUaState.Stopped);
        info.Users.ShouldBeEmpty();
    }

    [Fact]
    public async Task Enabling_needs_a_user_and_starts_the_server_without_exposing_passwords()
    {
        await using var agent = new AgentFactory();
        var client = agent.CreateGrpcClient();
        var port = FreePort();

        var invalid = await Should.ThrowAsync<RpcException>(async () =>
            await client.PutOpcUaAsync(new PutOpcUaRequest { Enabled = true, Port = port }, cancellationToken: Ct));
        invalid.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        invalid.Status.Detail.ShouldContain("al menos un usuario");

        var info = await client.PutOpcUaAsync(Enabled(port), cancellationToken: Ct);

        info.State.ShouldBe(OpcUaState.Running, info.Detail);
        info.EndpointUrl.ShouldEndWith($":{port}");
        info.CertificateThumbprint.ShouldNotBeNullOrEmpty();
        var user = info.Users.ShouldHaveSingleItem();
        (user.Name, user.Role, user.Password).ShouldBe(("ignition", OpcUaRole.Read, ""));
        var json = await File.ReadAllTextAsync(agent.ConfigPath, Ct);
        json.ShouldNotContain("clave-de-prueba");
        json.ShouldContain("pbkdf2-sha256$");
        agent.AuditLog.ShouldContain(l => l.StartsWith("Ajustes OPC UA cambiados por anónimo@", StringComparison.Ordinal) && l.Contains("activo, puerto"));

        var stopped = await client.PutOpcUaAsync(new PutOpcUaRequest { Enabled = false, Port = port, Users = { new OpcUaUser { Name = "ignition" } } }, cancellationToken: Ct);
        stopped.State.ShouldBe(OpcUaState.Stopped);
    }

    [Fact]
    public async Task An_empty_password_keeps_the_current_one()
    {
        await using var agent = new AgentFactory();
        var client = agent.CreateGrpcClient();
        var disabled = Enabled(FreePort());
        disabled.Enabled = false;
        await client.PutOpcUaAsync(disabled, cancellationToken: Ct);
        var before = ConfigurationHash(await File.ReadAllTextAsync(agent.ConfigPath, Ct));

        await client.PutOpcUaAsync(new PutOpcUaRequest { Port = 4840, Users = { new OpcUaUser { Name = "IGNITION", Role = OpcUaRole.Write } } }, cancellationToken: Ct);

        var after = await File.ReadAllTextAsync(agent.ConfigPath, Ct);
        ConfigurationHash(after).ShouldBe(before);
        (await client.GetOpcUaAsync(new Empty(), cancellationToken: Ct)).Users.ShouldHaveSingleItem().Role.ShouldBe(OpcUaRole.Write);
        var unknown = await Should.ThrowAsync<RpcException>(async () =>
            await client.PutOpcUaAsync(new PutOpcUaRequest { Port = 4840, Users = { new OpcUaUser { Name = "nuevo" } } }, cancellationToken: Ct));
        unknown.Status.Detail.ShouldContain("Usuario OPC UA 'nuevo': falta la contraseña.");
    }

    [Fact]
    public async Task Unknown_certificates_are_reported()
    {
        await using var agent = new AgentFactory();
        var client = agent.CreateGrpcClient();

        (await Should.ThrowAsync<RpcException>(async () =>
            await client.TrustOpcUaCertificateAsync(new OpcUaCertificateRequest { Thumbprint = "ABCDEF" }, cancellationToken: Ct))).StatusCode.ShouldBe(StatusCode.NotFound);
        (await Should.ThrowAsync<RpcException>(async () =>
            await client.DeleteOpcUaCertificateAsync(new OpcUaCertificateRequest { Thumbprint = "ABCDEF" }, cancellationToken: Ct))).StatusCode.ShouldBe(StatusCode.NotFound);
        (await client.GetOpcUaAsync(new Empty(), cancellationToken: Ct)).Certificates.ShouldBeEmpty();
    }

    private static string ConfigurationHash(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.GetProperty("opcUa").GetProperty("users")[0].GetProperty("passwordHash").GetString()!;
    }
}
