using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Opc.Ua;
using Opc.Ua.Client;
using PlcMonitor.Engine.Configuration;
using static PlcMonitor.OpcUa.Tests.OpcUaFixture;

namespace PlcMonitor.OpcUa.Tests;

/// <summary>Servidor OPC UA de punta a punta con un cliente OPC UA real.</summary>
public class OpcUaServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<DataValue> ReadAsync(ISession session, NodeId node) =>
        (await session.ReadAsync(null, 0, TimestampsToReturn.Both, (ArrayOf<ReadValueId>)new[] { new ReadValueId { NodeId = node, AttributeId = Attributes.Value } }, Ct))
        .Results[0];

    /// <summary>El nodo existe (se lee su nombre: el valor puede ser malo aunque exista).</summary>
    private static async Task<bool> ExistsAsync(ISession session, NodeId node) =>
        (await session.ReadAsync(null, 0, TimestampsToReturn.Neither, (ArrayOf<ReadValueId>)new[] { new ReadValueId { NodeId = node, AttributeId = Attributes.DisplayName } }, Ct))
        .Results[0].StatusCode != StatusCodes.BadNodeIdUnknown;

    private static async Task<StatusCode> WriteAsync(ISession session, NodeId node, Variant value) =>
        (await session.WriteAsync(null, (ArrayOf<WriteValue>)new[] { new WriteValue { NodeId = node, AttributeId = Attributes.Value, Value = new DataValue(value) } }, Ct))
        .Results[0];

    [Fact]
    public async Task Disabled_by_default_and_runs_once_enabled_with_a_long_lived_certificate()
    {
        await using var f = new OpcUaFixture();
        await f.Engine.StartAsync();

        await f.Host.ApplyAsync(f.Settings(enabled: false), Ct);
        f.Host.Status.State.ShouldBe(OpcUaState.Stopped);

        await f.Host.ApplyAsync(f.Settings(), Ct);

        f.Host.Status.State.ShouldBe(OpcUaState.Running, f.Host.Status.Detail);
        f.Host.Status.EndpointUrl.ShouldBe(f.Url);
        var own = Directory.GetFiles(Path.Combine(f.Host.PkiRoot, "certs"), "*.der")
            .Select(X509CertificateLoader.LoadCertificateFromFile)
            .Single(c => c.Thumbprint == f.Host.Status.CertificateThumbprint);
        own.NotAfter.ShouldBeGreaterThan(DateTime.Now.AddYears(9));

        await f.Host.ApplyAsync(f.Settings(enabled: false), Ct);
        f.Host.Status.State.ShouldBe(OpcUaState.Stopped);
    }

    [Fact]
    public async Task Only_encrypted_endpoints_with_user_name_are_offered()
    {
        await using var f = new OpcUaFixture();
        await f.StartAsync();
        var configuration = EndpointConfiguration.Create();

        using var discovery = await DiscoveryClient.CreateAsync(new Uri(f.Url), configuration, Client.Telemetry, ct: Ct);
        var endpoints = await discovery.GetEndpointsAsync(default, Ct);

        endpoints.Count.ShouldBeGreaterThan(0);
        endpoints.ToArray()!.ShouldAllBe(e => e.SecurityMode == MessageSecurityMode.SignAndEncrypt);
        endpoints.ToArray()!.ShouldAllBe(e => e.UserIdentityTokens.ToArray()!.All(t => t.TokenType == UserTokenType.UserName));
    }

    [Fact]
    public async Task A_new_client_is_rejected_until_its_certificate_is_trusted()
    {
        await using var f = new OpcUaFixture();
        await f.StartAsync();

        var rejected = await Should.ThrowAsync<ServiceResultException>(() => Client.ConnectAsync(f.Url, Reader, Password));

        rejected.StatusCode.ShouldBe(StatusCodes.BadSecurityChecksFailed);
        var pending = f.Host.ListClientCertificates().ShouldHaveSingleItem();
        pending.Trusted.ShouldBeFalse();
        pending.Subject.ShouldContain("PlcMonitorTests");
        pending.ApplicationUri.ShouldBe("urn:localhost:plcmon-tests");

        f.Host.TrustClientCertificate(pending.Thumbprint).ShouldBeTrue();
        f.Host.ListClientCertificates().ShouldHaveSingleItem().Trusted.ShouldBeTrue();
        var session = await f.ConnectAsync();
        session.Connected.ShouldBeTrue();

        f.Host.RemoveClientCertificate(pending.Thumbprint).ShouldBeTrue();
        f.Host.ListClientCertificates().ShouldBeEmpty();
    }

    [Fact]
    public async Task Wrong_passwords_and_unknown_users_are_rejected()
    {
        await using var f = new OpcUaFixture();
        await f.StartAsync();
        await f.ConnectAsync();

        (await Should.ThrowAsync<ServiceResultException>(() => Client.ConnectAsync(f.Url, Reader, "mal"))).StatusCode.ShouldBe(StatusCodes.BadUserAccessDenied);
        (await Should.ThrowAsync<ServiceResultException>(() => Client.ConnectAsync(f.Url, "nadie", Password))).StatusCode.ShouldBe(StatusCodes.BadUserAccessDenied);
    }

    [Fact]
    public async Task Tags_and_device_status_are_published_and_follow_the_plc()
    {
        await using var f = new OpcUaFixture(devices: Device("plc1", tags: [Tag("DB1.DBW0", "velocidad"), Tag("DB1.DBW2", "presion", folder: "Bomba/Salida")]));
        f.Driver.Plc("plc1").Values["DB1.DBW0"] = 12;
        f.Driver.Plc("plc1").Values["DB1.DBW2"] = 3;
        await f.StartAsync();
        var session = await f.ConnectAsync();

        var velocidad = await EventuallyAsync(() => ReadAsync(session, Node(session, "plc1/velocidad")), v => StatusCode.IsGood(v.StatusCode));
        velocidad.WrappedValue.ToString().ShouldBe("12");
        (await EventuallyAsync(() => ReadAsync(session, Node(session, "estado:plc1/Conectado")), v => v.WrappedValue.ToString() == "True")).StatusCode.ShouldBe(StatusCodes.Good);
        (await ReadAsync(session, Node(session, "estado:plc1/Estado"))).WrappedValue.ToString().ShouldBe("Conectado");

        f.Driver.Plc("plc1").Values["DB1.DBW0"] = 40;
        await EventuallyAsync(() => ReadAsync(session, Node(session, "plc1/velocidad")), v => v.WrappedValue.ToString() == "40");

        // Carpetas anidadas a partir del grupo de visualización.
        var browser = new Browser(session) { BrowseDirection = BrowseDirection.Forward, ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences, IncludeSubtypes = true };
        var device = (await browser.BrowseAsync(Node(session, "dispositivo:plc1"), Ct)).ToArray()!.Select(r => r.DisplayName.Text).ToList();
        device.ShouldBe(["_Estado", "velocidad", "Bomba"], ignoreOrder: true);
        var salida = (await browser.BrowseAsync(Node(session, "carpeta:plc1/Bomba/Salida"), Ct)).ToArray()!.Select(r => r.DisplayName.Text);
        salida.ShouldBe(["presion"]);
    }

    [Fact]
    public async Task Writes_need_the_third_key_a_writer_and_the_two_keys_of_the_engine()
    {
        await using var f = new OpcUaFixture(devices: Device("plc1", writesEnabled: true, tags: [Tag("DB1.DBW0", "consigna", writable: true), Tag("DB1.DBW2", "fija")]));
        var plc = f.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 0;
        plc.Values["DB1.DBW2"] = 0;
        await f.StartAsync(f.Settings(allowWrites: false));
        var writer = await f.ConnectAsync(Writer);
        var reader = await f.ConnectAsync(Reader);
        var consigna = Node(writer, "plc1/consigna");
        await EventuallyAsync(() => ReadAsync(writer, consigna), v => StatusCode.IsGood(v.StatusCode));

        (await WriteAsync(writer, consigna, Variant.From(5))).ShouldBe(StatusCodes.BadNotWritable);

        await f.Host.ApplyAsync(f.Settings(allowWrites: true), Ct);
        f.Host.Status.State.ShouldBe(OpcUaState.Running);
        (await WriteAsync(reader, Node(reader, "plc1/consigna"), Variant.From(5))).ShouldBe(StatusCodes.BadUserAccessDenied);
        (await WriteAsync(writer, Node(writer, "plc1/fija"), Variant.From(5))).ShouldBe(StatusCodes.BadNotWritable);
        plc.Writes.ShouldBeEmpty();

        (await WriteAsync(writer, consigna, Variant.From(7))).ShouldBe(StatusCodes.Good);

        plc.Writes.ShouldHaveSingleItem().Value.ShouldBe(7);
        await EventuallyAsync(() => ReadAsync(reader, Node(reader, "plc1/consigna")), v => v.WrappedValue.ToString() == "7");
    }

    [Fact]
    public async Task Address_space_follows_reconfigurations_live()
    {
        await using var f = new OpcUaFixture(devices: Device("plc1", tags: [Tag("DB1.DBW0", "a"), Tag("DB1.DBW2", "b")]));
        await f.StartAsync();
        var session = await f.ConnectAsync();
        (await ExistsAsync(session, Node(session, "plc1/a"))).ShouldBeTrue();
        (await ReadAsync(session, Node(session, "plc1/a"))).StatusCode.ShouldBe(StatusCodes.BadConfigurationError); // no existe en el PLC simulado

        await f.Engine.PutDeviceAsync(Device("plc1", tags: [Tag("DB1.DBW0", "a"), Tag("DB1.DBW4", "c")]));
        await f.Engine.PutDeviceAsync(Device("plc2", tags: [Tag("DB1.DBW0", "x")]));

        await EventuallyAsync(() => ExistsAsync(session, Node(session, "plc1/c")), e => e);
        await EventuallyAsync(() => ExistsAsync(session, Node(session, "plc1/b")), e => !e);
        await EventuallyAsync(() => ExistsAsync(session, Node(session, "plc2/x")), e => e);

        await f.Engine.DeleteDeviceAsync("plc2");
        await EventuallyAsync(() => ExistsAsync(session, Node(session, "dispositivo:plc2")), e => !e);
        (await ExistsAsync(session, Node(session, "plc2/x"))).ShouldBeFalse();
        (await ExistsAsync(session, Node(session, "plc1/a"))).ShouldBeTrue();
    }

    [Fact]
    public async Task Only_published_devices_and_tags_are_exposed_and_the_keys_apply_live()
    {
        await using var f = new OpcUaFixture(devices:
        [
            Device("visible", tags: [Tag("DB1.DBW0", "si"), Tag("DB1.DBW2", "oculto", published: false)]),
            Device("privado", published: false, writesEnabled: false, Tag("DB1.DBW0", "x")),
        ]);
        await f.StartAsync();
        var session = await f.ConnectAsync();

        (await ExistsAsync(session, Node(session, "visible/si"))).ShouldBeTrue();
        (await ExistsAsync(session, Node(session, "visible/oculto"))).ShouldBeFalse();
        (await ExistsAsync(session, Node(session, "dispositivo:privado"))).ShouldBeFalse();
        (await ExistsAsync(session, Node(session, "privado/x"))).ShouldBeFalse();

        await f.Engine.PutDeviceAsync(Device("privado", published: true, writesEnabled: false, Tag("DB1.DBW0", "x")));
        await f.Engine.PutDeviceAsync(Device("visible", tags: [Tag("DB1.DBW0", "si", published: false), Tag("DB1.DBW2", "oculto")]));

        await EventuallyAsync(() => ExistsAsync(session, Node(session, "privado/x")), e => e);
        await EventuallyAsync(() => ExistsAsync(session, Node(session, "visible/oculto")), e => e);
        await EventuallyAsync(() => ExistsAsync(session, Node(session, "visible/si")), e => !e);

        await f.Engine.PutDeviceAsync(Device("visible", published: false, writesEnabled: false, Tag("DB1.DBW2", "oculto")));
        await EventuallyAsync(() => ExistsAsync(session, Node(session, "dispositivo:visible")), e => !e);
    }

    [Fact]
    public async Task History_of_historized_tags_can_be_read()
    {
        await using var f = new OpcUaFixture(historian: true, Device("plc1", tags: [Tag("DB1.DBW0", "nivel", historized: true)]));
        var plc = f.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 1;
        await f.StartAsync();
        var session = await f.ConnectAsync();
        var node = Node(session, "plc1/nivel");
        await EventuallyAsync(() => ReadAsync(session, node), v => v.WrappedValue.ToString() == "1");
        plc.Values["DB1.DBW0"] = 2;
        await EventuallyAsync(() => ReadAsync(session, node), v => v.WrappedValue.ToString() == "2");

        var details = new ReadRawModifiedDetails
        {
            StartTime = (DateTimeUtc)DateTime.UtcNow.AddMinutes(-5),
            EndTime = (DateTimeUtc)DateTime.UtcNow.AddMinutes(1),
            NumValuesPerNode = 100,
            ReturnBounds = false,
        };
        var values = await EventuallyAsync(async () =>
        {
            var response = await session.HistoryReadAsync(null, new ExtensionObject(details), TimestampsToReturn.Source, false,
                (ArrayOf<HistoryReadValueId>)new[] { new HistoryReadValueId { NodeId = node } }, Ct);
            var result = response.Results[0];
            StatusCode.IsGood(result.StatusCode).ShouldBeTrue(result.StatusCode.ToString());
            // La primera muestra puede ser mala (antes de conectar): solo cuentan las buenas.
            return result.HistoryData.TryGetValue(out HistoryData? data) && data is not null
                ? data.DataValues.ToArray()!.Where(v => StatusCode.IsGood(v.StatusCode)).Select(v => v.WrappedValue.ToString()).ToList()
                : [];
        }, v => v.Contains("1") && v.Contains("2"));

        values.ShouldBe(["1", "2"]);
    }

    [Fact]
    public async Task A_busy_port_is_reported()
    {
        await using var f = new OpcUaFixture();
        await f.Engine.StartAsync();
        using var blocker = new TcpListener(IPAddress.Any, f.Port);
        blocker.Start();

        await f.Host.ApplyAsync(f.Settings(), Ct);

        f.Host.Status.State.ShouldBe(OpcUaState.Error);
        f.Host.Status.Detail.ShouldBe($"El puerto {f.Port} está ocupado por otro programa.");
    }

    [Fact]
    public async Task Changing_the_port_restarts_the_server()
    {
        await using var f = new OpcUaFixture();
        await f.StartAsync();
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var other = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await f.Host.ApplyAsync(f.Settings(port: other), Ct);

        f.Host.Status.State.ShouldBe(OpcUaState.Running, f.Host.Status.Detail);
        f.Host.Status.EndpointUrl.ShouldBe($"opc.tcp://localhost:{other}");
    }
}
