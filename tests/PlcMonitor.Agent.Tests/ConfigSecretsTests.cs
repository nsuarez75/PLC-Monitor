using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PlcMonitor.Agent.Contracts;
using PlcMonitor.Engine.Configuration;
using static PlcMonitor.Agent.Tests.AgentFactory;

namespace PlcMonitor.Agent.Tests;

/// <summary>Contraseñas de los dispositivos: cifradas en agent.json y ocultas en la API.</summary>
public class ConfigSecretsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("plcmon-secrets-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static DeviceConfiguration WithPassword(string name, string password)
    {
        var device = Device(name);
        device.Settings = JsonSerializer.SerializeToElement(new { host = "10.0.0.1", password });
        return device;
    }

    private static string? Password(DeviceConfiguration device) =>
        device.Settings.TryGetProperty("password", out var p) ? p.GetString() : null;

    private static string Password(DeviceConfig device) =>
        JsonDocument.Parse(device.SettingsJson).RootElement.GetProperty("password").GetString()!;

    [Fact]
    public void Passwords_round_trip_and_the_key_is_private()
    {
        var secrets = new ConfigSecrets(Path.Combine(_dir, "agent.key"));
        var config = new AgentConfiguration { Devices = [WithPassword("plc1", "s3creta"), WithPassword("plc2", "")] };

        secrets.Protect(config);

        Password(config.Devices[0]).ShouldStartWith("enc:v1:");
        Password(config.Devices[1]).ShouldBe("");
        config.Devices[0].Settings.GetProperty("host").GetString().ShouldBe("10.0.0.1");
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(secrets.KeyPath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var loaded = ConfigurationFile.Parse(ConfigurationFile.Serialize(config));
        var (plain, failed) = new ConfigSecrets(secrets.KeyPath).Unprotect(loaded);
        plain.ShouldBeEmpty();
        failed.ShouldBeEmpty();
        Password(loaded.Devices[0]).ShouldBe("s3creta");
    }

    [Fact]
    public void A_password_from_another_key_is_kept_encrypted_and_reported()
    {
        var config = new AgentConfiguration { Devices = [WithPassword("plc1", "s3creta")] };
        new ConfigSecrets(Path.Combine(_dir, "a.key")).Protect(config);
        var stored = Password(config.Devices[0]);

        var (_, failed) = new ConfigSecrets(Path.Combine(_dir, "b.key")).Unprotect(config);

        failed.ShouldBe(["plc1"]);
        Password(config.Devices[0]).ShouldBe(stored);
    }

    [Fact]
    public async Task Plain_passwords_are_encrypted_on_start_and_never_leave_the_agent()
    {
        await using var agent = new AgentFactory(WithPassword("plc1", "s3creta"));
        var client = agent.CreateGrpcClient();

        var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = "plc1" }, cancellationToken: Ct);
        var config = await client.GetConfigurationAsync(new Google.Protobuf.WellKnownTypes.Empty(), cancellationToken: Ct);

        Password(device).ShouldBe(ConfigSecrets.Masked);
        config.Devices.ShouldHaveSingleItem().SettingsJson.ShouldNotContain("s3creta");
        var onDisk = await File.ReadAllTextAsync(agent.ConfigPath, Ct);
        onDisk.ShouldNotContain("s3creta");
        onDisk.ShouldContain("enc:v1:");
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(agent.ConfigPath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Password(Engine(agent).GetDeviceConfiguration("plc1")).ShouldBe("s3creta");
    }

    [Fact]
    public async Task A_masked_password_keeps_the_stored_one_even_when_renaming()
    {
        await using var agent = new AgentFactory(WithPassword("plc1", "s3creta"));
        var client = agent.CreateGrpcClient();
        var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = "plc1" }, cancellationToken: Ct);
        device.Name = "prensa";

        await client.PutDeviceAsync(new PutDeviceRequest { Device = device, OriginalName = "plc1", Persist = true }, cancellationToken: Ct);

        Password(Engine(agent).GetDeviceConfiguration("prensa")).ShouldBe("s3creta");
        (await File.ReadAllTextAsync(agent.ConfigPath, Ct)).ShouldNotContain("s3creta");
    }

    [Fact]
    public async Task A_new_password_replaces_the_stored_one()
    {
        await using var agent = new AgentFactory(WithPassword("plc1", "s3creta"));
        var client = agent.CreateGrpcClient();
        var device = await client.GetDeviceConfigAsync(new DeviceRequest { Device = "plc1" }, cancellationToken: Ct);
        device.SettingsJson = device.SettingsJson.Replace(ConfigSecrets.Masked, "otra", StringComparison.Ordinal);

        await client.PutDeviceAsync(new PutDeviceRequest { Device = device, Persist = true }, cancellationToken: Ct);

        Password(Engine(agent).GetDeviceConfiguration("plc1")).ShouldBe("otra");
        var saved = await ConfigurationFile.LoadAsync(agent.ConfigPath, Ct);
        Password(saved.Devices[0]).ShouldStartWith("enc:v1:");
    }

    private static Engine.TagEngine Engine(AgentFactory agent) => agent.Services.GetRequiredService<EngineHost>().Engine;
}
