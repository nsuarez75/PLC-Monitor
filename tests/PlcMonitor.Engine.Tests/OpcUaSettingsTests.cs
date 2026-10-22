using PlcMonitor.Engine.Configuration;
using static PlcMonitor.Engine.Tests.EngineHarness;

namespace PlcMonitor.Engine.Tests;

/// <summary>Ajustes del servidor OPC UA en la configuración del agente.</summary>
public class OpcUaSettingsTests
{
    private static OpcUaUser User(string name, OpcUaRole role = OpcUaRole.Read, string hash = "pbkdf2$x") => new() { Name = name, Role = role, PasswordHash = hash };

    [Fact]
    public void By_default_the_server_is_off_on_the_standard_port()
    {
        var settings = new AgentConfiguration().OpcUa;

        settings.Enabled.ShouldBeFalse();
        settings.Port.ShouldBe(4840);
        settings.AllowWrites.ShouldBeFalse();
        settings.Users.ShouldBeEmpty();
    }

    [Fact]
    public void Settings_are_validated()
    {
        var registry = new PlcMonitor.Core.DriverRegistry([new FakeDriverFactory()]);
        var configuration = new AgentConfiguration
        {
            OpcUa = new OpcUaSettings { Enabled = true, Port = 70000, Users = [User("ana"), User("ANA"), User(" "), User("luis", hash: "")] },
        };

        var errors = ConfigurationValidator.Validate(configuration, registry);

        errors.ShouldContain("opcUa.port debe estar entre 1 y 65535.");
        errors.ShouldContain("Usuario OPC UA 'ANA': nombre repetido.");
        errors.ShouldContain("Usuario OPC UA sin nombre.");
        errors.ShouldContain("Usuario OPC UA 'luis': falta la contraseña.");
        ConfigurationValidator.Validate(new AgentConfiguration { OpcUa = new OpcUaSettings { Enabled = true } }, registry)
            .ShouldContain(e => e.Contains("al menos un usuario", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settings_are_replaced_and_saved_with_the_configuration()
    {
        await using var h = new EngineHarness(Device("plc1", enabled: false));

        var saved = await h.Engine.SetOpcUaAsync(new OpcUaSettings { Enabled = true, Port = 4841, AllowWrites = true, Users = [User(" ana ", OpcUaRole.Write)] });

        saved.Users.ShouldHaveSingleItem().Name.ShouldBe("ana");
        h.Engine.GetOpcUa().Port.ShouldBe(4841);
        var json = ConfigurationFile.Serialize(h.Engine.SnapshotConfiguration());
        var reloaded = ConfigurationFile.Parse(json).OpcUa;
        (reloaded.Enabled, reloaded.Port, reloaded.AllowWrites).ShouldBe((true, 4841, true));
        reloaded.Users.ShouldHaveSingleItem().Role.ShouldBe(OpcUaRole.Write);
        json.ShouldContain("\"role\": \"Write\"");
    }

    [Fact]
    public async Task Invalid_settings_change_nothing()
    {
        await using var h = new EngineHarness(Device("plc1", enabled: false));

        var error = await Should.ThrowAsync<EngineConfigurationException>(() => h.Engine.SetOpcUaAsync(new OpcUaSettings { Enabled = true }));

        error.Errors.ShouldHaveSingleItem().ShouldContain("al menos un usuario");
        h.Engine.GetOpcUa().Enabled.ShouldBeFalse();
    }

    [Fact]
    public void Older_configurations_without_opc_ua_load_with_the_defaults()
    {
        var configuration = ConfigurationFile.Parse("""{ "devices": [] }""");

        configuration.OpcUa.Enabled.ShouldBeFalse();
        configuration.OpcUa.Port.ShouldBe(4840);
    }
}
