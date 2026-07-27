using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;
using Grpc.Net.Client;

namespace ApiTester.Grpc;

/// <summary>Builds the <see cref="GrpcChannel"/> a <see cref="GrpcCaller"/> talks through. The gRPC-
/// shaped sibling of the handler-configuration idiom in ApiClient.SendOnceAsync (ApiTester.Core), but
/// narrower: gRPC is HTTP/2 end to end, so several of ApiClient's transport knobs simply do not apply
/// here — see the per-member notes below rather than guessing which ones are silently ignored.</summary>
internal static class GrpcChannelFactory
{
    /// <summary>Which <see cref="TransportOptions"/> members this channel honors, and why the rest
    /// do not apply:
    /// <list type="bullet">
    /// <item><description><see cref="TransportOptions.Proxy"/>, <see cref="TransportOptions.ProxyUrl"/>,
    /// <see cref="TransportOptions.ProxyUser"/>, <see cref="TransportOptions.ProxyPassword"/>, and
    /// <see cref="TransportOptions.NoProxy"/> — honored, via <c>ProxyConfiguration.Apply</c>, literally
    /// the same code <c>ApiClient</c> uses.</description></item>
    /// <item><description><see cref="TransportOptions.IgnoreServerCertificateErrors"/> — honored, via
    /// the same server-certificate validation callback shape <c>ApiClient</c> uses.</description></item>
    /// <item><description><see cref="TransportOptions.Version"/> — not honored. gRPC *is* HTTP/2;
    /// pinning HTTP/1.1 would only break the call, and there is no lower version to negotiate up
    /// from.</description></item>
    /// <item><description><see cref="TransportOptions.Resolve"/> — not honored in this phase. Doing so
    /// would mean driving the TLS handshake by hand (the way ApiClient's ConnectCallback does) purely
    /// to negotiate ALPN h2 ourselves, which is needless risk for a testing tool; the handler is left
    /// to dial and negotiate on its own.</description></item>
    /// <item><description><see cref="TransportOptions.Retries"/>, <see cref="TransportOptions.FollowRedirects"/>,
    /// <see cref="TransportOptions.Decompress"/> — not channel concerns: gRPC has no HTTP redirects to
    /// follow, and gRPC message framing is not HTTP content-encoding, so there is nothing here for them
    /// to control.</description></item>
    /// </list>
    /// </summary>
    /// <param name="trustServerCertificate">Consulted when the server's certificate fails ordinary
    /// validation, so a host pinned with `certapi trust add` is reachable without --insecure — the
    /// same seam ApiClient already uses.</param>
    internal static GrpcChannel Create(Uri address, X509Certificate2? clientCertificate,
        TransportOptions transport, Func<X509Certificate2?, bool>? trustServerCertificate)
    {
        if (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                $"Address scheme '{address.Scheme}' is not supported for gRPC; use http or https.",
                nameof(address));
        }

        bool Validate(object _, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None) return true;
            if (transport.IgnoreServerCertificateErrors) return true;
            if (trustServerCertificate is not null)
            {
                using var c = cert is null ? null : new X509Certificate2(cert);
                if (trustServerCertificate(c)) return true;
            }
            return false;
        }

        var handler = new SocketsHttpHandler
        {
            // Use the machine's configured proxy — including WPAD/PAC — authenticating with the
            // signed-in user's Windows credentials when required. Mirrors ApiClient exactly.
            DefaultProxyCredentials = CredentialCache.DefaultCredentials
        };
        ProxyConfiguration.Apply(handler, transport);

        // Let the handler drive the TLS handshake (and, for https, ALPN h2 negotiation) itself. A
        // hand-driven SslStream — the way ApiClient reads TLS diagnostics for the direct-connection
        // path — would have to negotiate ALPN on its own, which is needless risk here: unlike ApiClient,
        // nothing in this phase reads back negotiated-protocol/cipher details.
        handler.SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Validate };
        if (clientCertificate is not null)
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };

        return GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
            // A testing tool that silently truncated a large response would be reporting a
            // falsehood; removing the library's 4 MB default cap is deliberate.
            MaxReceiveMessageSize = null,
            // Deliberately left at its default (false): true converts a call's failure into a bare
            // OperationCanceledException, discarding the RpcException status and the cause chain
            // that make a refused certificate identifiable — a diagnostic tool cannot afford that.
            // The caller's own cancellation is still handled explicitly, at the call sites.
            ThrowOperationCanceledOnCancellation = false
        });
    }
}
