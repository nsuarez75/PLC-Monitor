using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PlcMonitor.Agent.Contracts.Client;

/// <summary>Material de seguridad de un agente: token de acceso y certificado TLS propio.</summary>
public static class AgentSecurity
{
    /// <summary>Cabecera gRPC con el token: <c>authorization: Bearer &lt;token&gt;</c>.</summary>
    public const string AuthorizationHeader = "authorization";

    /// <summary>
    /// Token aleatorio de 256 bits (base64url, 43 caracteres). Nunca empieza por «-»: la CLI tomaría
    /// <c>--token -abc…</c> por otra opción (pasaba con 1 de cada 64 tokens).
    /// </summary>
    public static string NewToken()
    {
        while (true)
        {
            var token = Base64Url(RandomNumberGenerator.GetBytes(32));
            if (token[0] != '-')
                return token;
        }
    }

    /// <summary>Hash SHA-256 del token en hexadecimal: es lo único que guarda el agente.</summary>
    public static string HashToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>Compara en tiempo constante un token con el hash guardado.</summary>
    public static bool Matches(string? token, string expectedHashHex)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(expectedHashHex))
            return false;
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHashHex);
        }
        catch (FormatException)
        {
            return false;
        }
        var actual = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Huella SHA-256 de un certificado en hexadecimal (la que fija el cliente).</summary>
    public static string Thumbprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// Crea un certificado autofirmado para el agente (ECDSA P-256, 10 años) con los nombres y direcciones indicados.
    /// Devuelve el PFX protegido con <paramref name="password"/> y su huella SHA-256.
    /// </summary>
    public static (byte[] Pfx, string Thumbprint) CreateCertificate(string commonName, IEnumerable<string> hostNames, string password)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in hostNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (System.Net.IPAddress.TryParse(name, out var ip))
                san.AddIpAddress(ip);
            else
                san.AddDnsName(name);
        }
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        return (certificate.Export(X509ContentType.Pfx, password), Thumbprint(certificate));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
