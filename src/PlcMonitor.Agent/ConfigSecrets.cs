using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlcMonitor.Engine.Configuration;

namespace PlcMonitor.Agent;

/// <summary>
/// Contraseñas de los dispositivos (propiedad <c>password</c> de los ajustes del driver): cifradas con AES-GCM en
/// agent.json, con la clave en <c>agent.key</c> junto a él (solo legible por la cuenta del servicio), y ocultas en la API.
/// </summary>
public sealed class ConfigSecrets(string keyPath)
{
    /// <summary>Lo que ve el cliente en lugar de la contraseña. Si vuelve tal cual, se conserva la guardada.</summary>
    public const string Masked = "********";

    private const string Prefix = "enc:v1:";
    private const int NonceSize = 12, TagSize = 16;
    private byte[]? _key;

    public string KeyPath { get; } = keyPath;

    /// <summary>Descifra en el sitio. Devuelve los dispositivos con contraseñas en claro (para migrarlas) o que no se pudieron descifrar.</summary>
    public (List<string> Plain, List<string> Undecryptable) Unprotect(AgentConfiguration configuration)
    {
        List<string> plain = [], failed = [];
        foreach (var device in configuration.Devices)
        {
            device.Settings = Transform(device.Settings, value =>
            {
                if (!value.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    plain.Add(device.Name);
                    return value;
                }
                try
                {
                    return Decrypt(value);
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException or IOException or UnauthorizedAccessException)
                {
                    // Se deja cifrada: al guardar se conserva y vuelve a valer si aparece la clave buena.
                    failed.Add(device.Name);
                    return value;
                }
            });
        }
        return (plain, failed);
    }

    /// <summary>Cifra en el sitio (sobre una copia de la configuración del motor).</summary>
    public void Protect(AgentConfiguration configuration)
    {
        foreach (var device in configuration.Devices)
            device.Settings = Transform(device.Settings, value => value.StartsWith(Prefix, StringComparison.Ordinal) ? value : Encrypt(value));
    }

    /// <summary>Sustituye las contraseñas por <see cref="Masked"/> antes de mandarlas al cliente.</summary>
    public static void Mask(DeviceConfiguration device) => device.Settings = Transform(device.Settings, _ => Masked);

    /// <summary>Las contraseñas que llegan como <see cref="Masked"/> recuperan el valor actual (null = dispositivo nuevo: quedan vacías).</summary>
    public static void Restore(DeviceConfiguration device, DeviceConfiguration? current)
    {
        device.Settings = Transform(device.Settings, (name, value) =>
        {
            if (value != Masked)
                return value;
            return current is not null && TryGetString(current.Settings, name, out var old) ? old : "";
        });
    }

    private static bool IsSecret(string name) => string.Equals(name, "password", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetString(JsonElement settings, string name, out string value)
    {
        value = "";
        if (settings.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var p in settings.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
            {
                value = p.Value.GetString()!;
                return true;
            }
        }
        return false;
    }

    private static JsonElement Transform(JsonElement settings, Func<string, string> change) => Transform(settings, (_, value) => change(value));

    /// <summary>Aplica el cambio a las contraseñas no vacías de primer nivel de los ajustes.</summary>
    private static JsonElement Transform(JsonElement settings, Func<string, string, string> change)
    {
        if (settings.ValueKind != JsonValueKind.Object)
            return settings;
        var node = JsonNode.Parse(settings.GetRawText())!.AsObject();
        var changed = false;
        foreach (var (name, value) in node.ToList())
        {
            if (!IsSecret(name) || value?.GetValueKind() != JsonValueKind.String || value.GetValue<string>() is not { Length: > 0 } text)
                continue;
            var result = change(name, text);
            if (result == text)
                continue;
            node[name] = result;
            changed = true;
        }
        return changed ? JsonSerializer.SerializeToElement(node) : settings;
    }

    private string Encrypt(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        var buffer = new byte[NonceSize + TagSize + data.Length];
        var nonce = buffer.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(Key, TagSize);
        aes.Encrypt(nonce, data, buffer.AsSpan(NonceSize + TagSize), buffer.AsSpan(NonceSize, TagSize));
        return Prefix + Convert.ToBase64String(buffer);
    }

    private string Decrypt(string stored)
    {
        var buffer = Convert.FromBase64String(stored[Prefix.Length..]);
        if (buffer.Length < NonceSize + TagSize)
            throw new CryptographicException("Contraseña cifrada truncada.");
        var data = new byte[buffer.Length - NonceSize - TagSize];
        using var aes = new AesGcm(Key, TagSize);
        aes.Decrypt(buffer.AsSpan(0, NonceSize), buffer.AsSpan(NonceSize + TagSize), buffer.AsSpan(NonceSize, TagSize), data);
        return Encoding.UTF8.GetString(data);
    }

    /// <summary>Clave de 256 bits; se crea la primera vez que hace falta.</summary>
    private byte[] Key => _key ??= File.Exists(KeyPath) ? ReadKey() : CreateKey();

    private byte[] ReadKey()
    {
        var key = Convert.FromBase64String(File.ReadAllText(KeyPath).Trim());
        return key.Length == 32 ? key : throw new CryptographicException($"{KeyPath} no contiene una clave válida.");
    }

    private byte[] CreateKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var temp = KeyPath + ".tmp";
        File.WriteAllText(temp, Convert.ToBase64String(key));
        RestrictToOwner(temp);
        File.Move(temp, KeyPath, overwrite: false);
        return key;
    }

    /// <summary>Solo la cuenta del servicio (Linux: 0600; Windows: SYSTEM y administradores, sin herencia).</summary>
    public static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return;
        }
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        if (WindowsIdentity.GetCurrent().User is { } me)
            acl.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }
}
