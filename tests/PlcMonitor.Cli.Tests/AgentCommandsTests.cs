using Grpc.Net.Client;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.Agent.Tests;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using static PlcMonitor.Agent.Tests.AgentFactory;
using Tag = PlcMonitor.Engine.Configuration.TagConfiguration;

namespace PlcMonitor.Cli.Tests;

/// <summary>
/// Órdenes <c>plcmon agent ...</c> contra el agente en memoria con driver simulado.
/// Una sola clase: <see cref="AgentRuntime"/> es estático y los tests no deben ir en paralelo entre sí.
/// </summary>
public sealed class AgentCommandsTests : IDisposable
{
    private readonly TestConsole _console = new();
    private readonly Func<PlcMonitor.Agent.Contracts.Client.AgentEndpoint, GrpcChannel> _originalFactory = AgentRuntime.ChannelFactory;
    private readonly Spectre.Console.IAnsiConsole _originalConsole = AgentRuntime.Console;

    public AgentCommandsTests()
    {
        _console.Profile.Width = 250;
        AgentRuntime.Console = _console;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Tag T(string address, string? name = null, bool writable = false) => new() { Address = address, Name = name, Writable = writable };

    private string Output => _console.Output;

    private static void UseAgent(AgentFactory agent) =>
        AgentRuntime.ChannelFactory = _ => GrpcChannel.ForAddress(agent.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = agent.Server.CreateHandler() });

    private Task<int> RunAsync(params string[] args) => RunAsync(Ct, args);

    private Task<int> RunAsync(CancellationToken cancellationToken, params string[] args)
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            CliApp.Configure(config);
            config.ConfigureConsole(_console);
        });
        return app.RunAsync(args, cancellationToken);
    }

    private static async Task WaitForGoodValuesAsync(AgentFactory agent)
    {
        var client = agent.CreateGrpcClient();
        await EventuallyAsync(
            async () => await client.ListTagsAsync(new ListTagsRequest()),
            t => t.Tags.Count > 0 && t.Tags.All(x => x.Value.Quality == Quality.Good));
    }

    [Fact]
    public async Task Info_shows_agent_and_drivers()
    {
        await using var agent = new AgentFactory();
        UseAgent(agent);

        (await RunAsync("agent", "info")).ShouldBe(0);

        Output.ShouldContain(Environment.MachineName);
        Output.ShouldContain("fake");
        Output.ShouldContain(agent.ConfigPath);
    }

    [Fact]
    public async Task Devices_show_state_and_tag_count()
    {
        await using var agent = new AgentFactory(Device("plc1", tags: [T("DB1.DBW0", "v")]), Device("plc2", enabled: false));
        agent.Driver.Plc("plc1").Values["DB1.DBW0"] = 1;
        UseAgent(agent);
        await WaitForGoodValuesAsync(agent);

        (await RunAsync("agent", "devices")).ShouldBe(0);

        Output.ShouldContain("plc1");
        Output.ShouldContain("Connected");
        Output.ShouldContain("plc2");
        Output.ShouldContain("Disconnected");
    }

    [Fact]
    public async Task Tags_show_last_value_and_can_filter_by_device()
    {
        await using var agent = new AgentFactory(
            Device("plc1", tags: [T("DB1.DBW0", "velocidad", writable: true)]),
            Device("plc2", tags: [T("DB2.DBW0", "presion")]));
        agent.Driver.Plc("plc1").Values["DB1.DBW0"] = 1500;
        agent.Driver.Plc("plc2").Values["DB2.DBW0"] = 7;
        UseAgent(agent);
        await WaitForGoodValuesAsync(agent);

        (await RunAsync("agent", "tags", "plc1")).ShouldBe(0);

        Output.ShouldContain("velocidad");
        Output.ShouldContain("1500");
        Output.ShouldContain("✎");
        Output.ShouldNotContain("presion");
    }

    [Fact]
    public async Task Unit_and_decimals_are_set_with_tag_add_and_shown_by_tags_and_watch()
    {
        await using var agent = new AgentFactory(Device("plc1"));
        var plc = agent.Driver.Plc("plc1");
        plc.Values["REAL0"] = 23.4500007629395;
        UseAgent(agent);
        _console.Profile.Width = 200; // que la tabla de tags no parta las celdas

        (await RunAsync("agent", "tag", "add", "plc1", "REAL0", "--name", "presion", "--unit", "bar", "--decimals", "2")).ShouldBe(0);
        await WaitForGoodValuesAsync(agent);
        (await RunAsync("agent", "tags", "plc1")).ShouldBe(0);
        Output.ShouldContain("23.45 bar");

        // Sin --unit ni --decimals se conservan; -1 quita los decimales.
        (await RunAsync("agent", "tag", "add", "plc1", "REAL0", "--name", "presion", "--decimals", "-1")).ShouldBe(0);
        var tag = (await agent.CreateGrpcClient().GetDeviceConfigAsync(new DeviceRequest { Device = "plc1" })).ScanGroups.SelectMany(g => g.Tags).Single();
        (tag.Unit, tag.HasDecimals).ShouldBe(("bar", false));
        (await RunAsync("agent", "tag", "add", "plc1", "REAL0", "--name", "presion", "--decimals", "12")).ShouldNotBe(0);

        (await RunAsync("agent", "tag", "add", "plc1", "REAL0", "--name", "presion", "--decimals", "1")).ShouldBe(0);
        await WaitForGoodValuesAsync(agent);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = RunAsync(cts.Token, "agent", "watch", "plc1");
        await EventuallyAsync(() => Task.FromResult(Output), o => o.Contains("Cambios en directo", StringComparison.Ordinal));
        plc.Values["REAL0"] = 1.25;
        await EventuallyAsync(() => Task.FromResult(Output), o => o.Contains("plc1/presion = 1.3 bar", StringComparison.Ordinal));
        await cts.CancelAsync();
        (await run).ShouldBe(0);
        Output.ShouldContain("plc1/presion = 23.5 bar");
    }

    [Fact]
    public async Task Read_returns_values_and_fails_on_bad_quality()
    {
        await using var agent = new AgentFactory(Device("plc1"));
        agent.Driver.Plc("plc1").Values["DB1.DBW4"] = 42;
        UseAgent(agent);
        await EventuallyAsync(
            async () => (await agent.CreateGrpcClient().ListDevicesAsync(new())).Devices[0].State,
            s => s == ConnectionState.Connected);

        (await RunAsync("agent", "read", "plc1", "DB1.DBW4")).ShouldBe(0);
        Output.ShouldContain("42");

        (await RunAsync("agent", "read", "plc1", "DB1.DBW4", "DB9.DBW0")).ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_device_is_reported_as_error()
    {
        await using var agent = new AgentFactory();
        UseAgent(agent);

        (await RunAsync("agent", "read", "noexiste", "DB1.DBW0")).ShouldBe(1);

        Output.ShouldContain("Error:");
        Output.ShouldContain("noexiste");
    }

    [Fact]
    public async Task Write_with_yes_goes_through_the_agent_double_key()
    {
        await using var agent = new AgentFactory(Device("plc1", writesEnabled: true, tags: [T("DB1.DBW0", "ro"), T("DB1.DBW2", "rw", writable: true)]));
        agent.Driver.Plc("plc1").Values["DB1.DBW0"] = 0;
        agent.Driver.Plc("plc1").Values["DB1.DBW2"] = 0;
        UseAgent(agent);
        await WaitForGoodValuesAsync(agent);

        (await RunAsync("agent", "write", "plc1", "rw", "7", "-y", "--user", "tester")).ShouldBe(0);
        Output.ShouldContain("Escrito");
        agent.Driver.Plc("plc1").Writes.ShouldHaveSingleItem().Value.ShouldBe(7);

        (await RunAsync("agent", "write", "plc1", "ro", "7", "-y")).ShouldBe(1);
        Output.ShouldContain("BadWriteDisabled");
    }

    [Fact]
    public async Task Write_asks_for_confirmation()
    {
        await using var agent = new AgentFactory(Device("plc1", writesEnabled: true, tags: [T("DB1.DBW2", "rw", writable: true)]));
        agent.Driver.Plc("plc1").Values["DB1.DBW2"] = 0;
        UseAgent(agent);
        await WaitForGoodValuesAsync(agent);
        _console.Interactive();
        _console.Input.PushTextWithEnter("n");

        (await RunAsync("agent", "write", "plc1", "rw", "7")).ShouldBe(2);

        Output.ShouldContain("Cancelado");
        agent.Driver.Plc("plc1").Writes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Enable_and_disable_change_the_device()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false));
        UseAgent(agent);
        var client = agent.CreateGrpcClient();

        (await RunAsync("agent", "enable", "plc1")).ShouldBe(0);
        Output.ShouldContain("activado");
        (await client.ListDevicesAsync(new())).Devices[0].Enabled.ShouldBeTrue();

        (await RunAsync("agent", "disable", "plc1")).ShouldBe(0);
        Output.ShouldContain("desactivado");
        (await client.ListDevicesAsync(new())).Devices[0].Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Watch_prints_snapshot_and_live_changes_until_cancelled()
    {
        await using var agent = new AgentFactory(Device("plc1", tags: [T("DB1.DBW0", "contador")]));
        var plc = agent.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 1;
        UseAgent(agent);
        await WaitForGoodValuesAsync(agent);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = RunAsync(cts.Token, "agent", "watch", "plc1");
        await EventuallyAsync(() => Task.FromResult(Output), o => o.Contains("Cambios en directo", StringComparison.Ordinal));
        plc.Values["DB1.DBW0"] = 99;
        await EventuallyAsync(() => Task.FromResult(Output), o => o.Contains("plc1/contador = 99", StringComparison.Ordinal));
        await cts.CancelAsync();

        (await run).ShouldBe(0);
        Output.ShouldContain("plc1/contador = 1");
    }

    [Fact]
    public async Task Browse_lists_symbols_of_a_connected_device()
    {
        await using var agent = new AgentFactory(Device("plc1"));
        var plc = agent.Driver.Plc("plc1");
        plc.Values["DB1.Motor"] = 1;
        plc.Values["DB1.Bomba"] = 2;
        UseAgent(agent);
        await EventuallyAsync(
            async () => (await agent.CreateGrpcClient().ListDevicesAsync(new())).Devices[0].State,
            s => s == ConnectionState.Connected);

        (await RunAsync("agent", "browse", "plc1", "motor")).ShouldBe(0);

        Output.ShouldContain("DB1.Motor");
        Output.ShouldNotContain("DB1.Bomba");
        Output.ShouldContain("1 mostrados de 1 coincidencias · 2 símbolos");
    }

    [Fact]
    public async Task Device_and_tag_commands_edit_and_persist_the_configuration()
    {
        await using var agent = new AgentFactory(Device("plc1", enabled: false));
        UseAgent(agent);
        var client = agent.CreateGrpcClient();

        (await RunAsync("agent", "device", "add", "prensa", "-d", "fake", "-s", "{\"host\":\"10.1.1.1\"}", "--disabled", "--writes")).ShouldBe(0);
        (await RunAsync("agent", "tag", "add", "prensa", "DB1.DBW0", "--name", "velocidad", "-g", "rapido", "-i", "250", "--writable", "--type", "int16")).ShouldBe(0);
        (await RunAsync("agent", "tag", "add", "prensa", "DB1.DBW2")).ShouldBe(0);
        (await RunAsync("agent", "config", "prensa")).ShouldBe(0);

        Output.ShouldContain("velocidad = DB1.DBW0");
        Output.ShouldContain("rapido");
        var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = "prensa" });
        device.Enabled.ShouldBeFalse();
        device.WritesEnabled.ShouldBeTrue();
        device.ScanGroups.Select(g => (g.Name, g.IntervalMs, g.Tags.Count)).ShouldBe([("rapido", 250, 1), ("default", 1000, 1)]);
        device.ScanGroups[0].Tags[0].DataType.ShouldBe(DataType.Int16);
        device.ScanGroups[0].Tags[0].Writable.ShouldBeTrue();

        (await RunAsync("agent", "tag", "remove", "prensa", "velocidad")).ShouldBe(0);
        (await RunAsync("agent", "tag", "remove", "prensa", "noexiste")).ShouldBe(1);
        (await client.GetDeviceConfigAsync(new DeviceRequest { Device = "prensa" })).ScanGroups.Select(g => g.Name).ShouldBe(["default"]);

        (await RunAsync("agent", "device", "remove", "plc1", "-y")).ShouldBe(0);
        var saved = await PlcMonitor.Engine.Configuration.ConfigurationFile.LoadAsync(agent.ConfigPath);
        saved.Devices.Select(d => d.Name).ShouldBe(["prensa"]);
    }

    [Fact]
    public async Task Invalid_device_shows_the_agent_validation_errors()
    {
        await using var agent = new AgentFactory();
        UseAgent(agent);

        (await RunAsync("agent", "device", "add", "x", "-d", "fake", "-s", "{}")).ShouldBe(1);

        Output.ShouldContain("Falta host");
    }

    [Fact]
    public async Task History_of_a_tag_added_with_history_is_shown_and_exported()
    {
        await using var agent = new AgentFactory(Device("plc1"));
        var plc = agent.Driver.Plc("plc1");
        plc.Values["DB1.DBW0"] = 7;
        UseAgent(agent);
        (await RunAsync("agent", "tag", "add", "plc1", "DB1.DBW0", "--name", "velocidad", "-i", "50", "--history", "--history-max", "1m", "--history-deadband", "2%", "--unit", "km/h")).ShouldBe(0);
        var config = await agent.CreateGrpcClient().GetDeviceConfigAsync(new DeviceRequest { Device = "plc1" });
        var history = config.ScanGroups.SelectMany(g => g.Tags).Single().History;
        (history.Enabled, history.MaxIntervalMs, history.Deadband, history.DeadbandPercent).ShouldBe((true, 60000, 2.0, true));
        await EventuallyAsync(async () => await agent.CreateGrpcClient().ListTagsAsync(new ListTagsRequest()), t => t.Tags.Single().Value.Quality == Quality.Good);

        (await RunAsync("agent", "history", "plc1", "velocidad", "--last", "1h")).ShouldBe(0);
        Output.ShouldContain("plc1/velocidad");
        Output.ShouldContain("7 km/h");
        Output.ShouldContain("Good");

        var csv = Path.Combine(agent.Directory, "historico.csv");
        (await RunAsync("agent", "history", "plc1", "velocidad", "--csv", csv)).ShouldBe(0);
        var lines = await File.ReadAllLinesAsync(csv);
        lines[0].ShouldBe("device,tag,timestamp,value,quality,unit");
        lines.ShouldContain(l => l.StartsWith("plc1,velocidad,", StringComparison.Ordinal) && l.EndsWith(",7,Good,km/h", StringComparison.Ordinal));

        (await RunAsync("agent", "info")).ShouldBe(0);
        Output.ShouldContain("1 tags");
        (await RunAsync("agent", "history", "plc1", "velocidad", "--last", "ayer")).ShouldNotBe(0);
    }

    [Theory]
    [InlineData("500ms", 0.5)]
    [InlineData("30s", 30)]
    [InlineData("15m", 900)]
    [InlineData("2h", 7200)]
    [InlineData("1.5d", 129600)]
    public void Durations_are_parsed(string text, double seconds) => Durations.Parse(text).ShouldBe(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void Invalid_durations_and_aggregates_are_rejected()
    {
        Durations.Parse("10").ShouldBeNull();
        Durations.Parse("h").ShouldBeNull();
        Durations.ParseAggregate("max").ShouldBe(HistoryAggregate.Max);
        Durations.ParseAggregate("mediana").ShouldBeNull();
    }

    [Fact]
    public async Task Unreachable_agent_gives_a_readable_error()
    {
        AgentRuntime.ChannelFactory = e => GrpcChannel.ForAddress(e.Url);

        (await RunAsync("agent", "devices", "-a", "http://127.0.0.1:1")).ShouldBe(1);

        Output.ShouldContain("No se puede conectar con el agente en http://127.0.0.1:1");
    }

    [Fact]
    public async Task Token_option_is_sent_to_a_protected_agent()
    {
        var token = PlcMonitor.Agent.Contracts.Client.AgentSecurity.NewToken();
        await using var agent = new AgentFactory();
        agent.Settings["Agent:Security:TokenSha256"] = PlcMonitor.Agent.Contracts.Client.AgentSecurity.HashToken(token);
        AgentRuntime.ChannelFactory = e => GrpcChannel.ForAddress(agent.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = agent.Server.CreateHandler(),
            Credentials = e.Token is null ? null : Grpc.Core.ChannelCredentials.Create(Grpc.Core.ChannelCredentials.Insecure,
                Grpc.Core.CallCredentials.FromInterceptor((_, m) => { m.Add("authorization", $"Bearer {e.Token}"); return Task.CompletedTask; })),
            UnsafeUseInsecureChannelCallCredentials = true,
        });

        (await RunAsync("agent", "info")).ShouldBe(1);
        Output.ShouldContain("rechaza el token");
        (await RunAsync("agent", "info", "--token", token)).ShouldBe(0, Output);
    }

    [Fact]
    public async Task Generated_tokens_never_start_with_a_dash_and_old_ones_that_do_work_with_equals()
    {
        // Un token que empieza por «-» se tomaba por otra opción de la línea de órdenes.
        Enumerable.Range(0, 2000).Select(_ => PlcMonitor.Agent.Contracts.Client.AgentSecurity.NewToken()).ShouldAllBe(t => t[0] != '-' && t.Length == 43);

        var token = "-" + PlcMonitor.Agent.Contracts.Client.AgentSecurity.NewToken()[1..];
        await using var agent = new AgentFactory();
        agent.Settings["Agent:Security:TokenSha256"] = PlcMonitor.Agent.Contracts.Client.AgentSecurity.HashToken(token);
        AgentRuntime.ChannelFactory = e => GrpcChannel.ForAddress(agent.Server.BaseAddress, new GrpcChannelOptions
        {
            HttpHandler = agent.Server.CreateHandler(),
            Credentials = e.Token is null ? null : Grpc.Core.ChannelCredentials.Create(Grpc.Core.ChannelCredentials.Insecure,
                Grpc.Core.CallCredentials.FromInterceptor((_, m) => { m.Add("authorization", $"Bearer {e.Token}"); return Task.CompletedTask; })),
            UnsafeUseInsecureChannelCallCredentials = true,
        });

        (await RunAsync("agent", "info", $"--token={token}")).ShouldBe(0, Output);
    }

    [Fact]
    public void Agent_url_comes_from_option_environment_or_default()
    {
        new AgentSettings().ResolveAgentUrl(_ => null).ShouldBe("http://127.0.0.1:5310");
        new AgentSettings().ResolveAgentUrl(_ => "http://10.0.0.5:5310").ShouldBe("http://10.0.0.5:5310");
        new AgentSettings { Agent = "http://pc:1" }.ResolveAgentUrl(_ => "http://10.0.0.5:5310").ShouldBe("http://pc:1");
        new AgentSettings { Agent = "ftp://pc" }.Validate().Successful.ShouldBeFalse();
        new AgentSettings { Agent = "http://pc:5310" }.Validate().Successful.ShouldBeTrue();
        var endpoint = new AgentSettings { Token = "t" }.ResolveEndpoint(v => v == AgentSettings.CertEnvironmentVariable ? "AB:CD" : null);
        (endpoint.Token, endpoint.CertificateThumbprint).ShouldBe(("t", "AB:CD"));
    }

    public void Dispose()
    {
        AgentRuntime.ChannelFactory = _originalFactory;
        AgentRuntime.Console = _originalConsole;
        _console.Dispose();
    }
}
