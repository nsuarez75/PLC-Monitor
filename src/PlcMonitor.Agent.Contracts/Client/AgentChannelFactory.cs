using System.Net.Security;
using Grpc.Core;
using Grpc.Net.Client;

namespace PlcMonitor.Agent.Contracts.Client;

/// <summary>Cómo conectar con un agente: URL y, si está protegido, token y huella del certificado.</summary>
/// <param name="Url">http://equipo:5310 o https://equipo:5311.</param>
/// <param name="Token">Token de acceso; null si el agente no lo exige.</param>
/// <param name="CertificateThumbprint">Huella SHA-256 del certificado del agente (https). Si se indica, solo se acepta ese certificado.</param>
public sealed record AgentEndpoint(string Url, string? Token = null, string? CertificateThumbprint = null);

/// <summary>Crea canales gRPC hacia un agente con su token y el certificado fijado (pinning).</summary>
public static class AgentChannelFactory
{
    public static GrpcChannel Create(AgentEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var handler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true };
        if (!string.IsNullOrWhiteSpace(endpoint.CertificateThumbprint))
        {
            var expected = endpoint.CertificateThumbprint.Replace(":", "", StringComparison.Ordinal).Trim();
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                // Certificado propio del agente: se acepta exactamente el que se generó al instalarlo.
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null
                    && string.Equals(
                        AgentSecurity.Thumbprint(new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate)),
                        expected,
                        StringComparison.OrdinalIgnoreCase),
            };
        }

        var options = new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true };
        if (!string.IsNullOrEmpty(endpoint.Token))
        {
            var header = $"Bearer {endpoint.Token}";
            var credentials = CallCredentials.FromInterceptor((_, metadata) =>
            {
                metadata.Add(AgentSecurity.AuthorizationHeader, header);
                return Task.CompletedTask;
            });
            options.Credentials = endpoint.Url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
                ? ChannelCredentials.Create(ChannelCredentials.SecureSsl, credentials)
                : ChannelCredentials.Create(ChannelCredentials.Insecure, credentials);
            // Token sobre http: solo tiene sentido en pruebas o redes aisladas, pero se permite.
            options.UnsafeUseInsecureChannelCallCredentials = true;
        }
        return GrpcChannel.ForAddress(endpoint.Url, options);
    }
}
