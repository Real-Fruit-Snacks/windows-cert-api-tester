using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ApiTester.Core;

public sealed class ApiClient
{
    public async Task<ApiResponse> SendAsync(
        ApiRequest request,
        X509Certificate2? clientCertificate,
        bool ignoreServerCertificateErrors = false,
        Func<X509Certificate2?, bool>? trustServerCertificate = null,
        bool followRedirects = true,
        CancellationToken cancellationToken = default)
    {
        bool serverUntrusted = false;

        // Captured during the TLS handshake for the diagnostics view.
        var negotiatedProtocol = SslProtocols.None;
        TlsCipherSuite cipher = default;
        bool clientCertSent = false;
        string? srvSubject = null, srvIssuer = null, srvThumb = null;
        DateTime? srvNotAfter = null;
        IReadOnlyList<string> chain = Array.Empty<string>();

        // Shared server-certificate validation used by both the direct and proxied paths.
        bool Validate(object _, X509Certificate? cert, X509Chain? certChain, SslPolicyErrors errors)
        {
            if (cert is not null)
            {
                using var c = new X509Certificate2(cert);
                srvSubject = c.Subject;
                srvIssuer = c.Issuer;
                srvThumb = c.Thumbprint;
                srvNotAfter = c.NotAfter;
            }
            if (certChain is not null)
                chain = certChain.ChainElements.Select(e => e.Certificate.Subject).ToList();

            if (errors == SslPolicyErrors.None) return true;
            if (ignoreServerCertificateErrors) return true;
            if (trustServerCertificate is not null)
            {
                using var c = cert is null ? null : new X509Certificate2(cert);
                if (trustServerCertificate(c)) return true;
            }
            serverUntrusted = true;
            return false;
        }

        bool viaProxy = ProxyWillBeUsed(request.Url);

        var handler = new SocketsHttpHandler
        {
            // Use the machine's configured proxy — including "Automatically detect settings"
            // (WPAD) and a "Use automatic configuration script" (PAC) from Internet Options —
            // authenticating with the signed-in user's Windows credentials when required.
            DefaultProxyCredentials = CredentialCache.DefaultCredentials
        };
        handler.AllowAutoRedirect = followRedirects;

        if (viaProxy)
        {
            // Let the handler drive the proxy CONNECT + TLS; capture the server cert in the callback.
            handler.SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Validate };
            if (clientCertificate is not null)
                handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };
        }
        else
        {
            // Establish the transport ourselves so we can read the negotiated TLS details.
            handler.ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await socket.ConnectAsync(context.DnsEndPoint, ct); }
                catch { socket.Dispose(); throw; }

                var network = new NetworkStream(socket, ownsSocket: true);
                if (context.InitialRequestMessage.RequestUri!.Scheme != Uri.UriSchemeHttps)
                    return network;

                var ssl = new SslStream(network, leaveInnerStreamOpen: false, Validate);
                var sslOptions = new SslClientAuthenticationOptions { TargetHost = context.DnsEndPoint.Host };
                if (clientCertificate is not null)
                    sslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };

                try { await ssl.AuthenticateAsClientAsync(sslOptions, ct); }
                catch { await ssl.DisposeAsync(); throw; }

                negotiatedProtocol = ssl.SslProtocol;
                try { cipher = ssl.NegotiatedCipherSuite; } catch { /* not available on all platforms */ }
                clientCertSent = ssl.LocalCertificate is not null;
                return ssl;
            };
        }

        ConnectionInfo BuildConnection() => new()
        {
            ViaProxy = viaProxy,
            TlsProtocol = FormatProtocol(negotiatedProtocol),
            CipherSuite = cipher == default ? null : cipher.ToString(),
            ClientCertificateSent = clientCertSent,
            ClientCertificateSubject = clientCertificate?.Subject,
            ServerCertificateSubject = srvSubject,
            ServerCertificateIssuer = srvIssuer,
            ServerCertificateThumbprint = srvThumb,
            ServerCertificateNotAfter = srvNotAfter,
            ServerCertificateChain = chain
        };

        using var http = new HttpClient(handler, disposeHandler: true) { Timeout = request.Timeout };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var message = new HttpRequestMessage(request.Method, request.Url);

            foreach (var header in request.Headers)
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);

            if (request.Body is not null)
            {
                message.Content = new StringContent(request.Body, Encoding.UTF8);
                if (request.ContentType is not null)
                    message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
            }

            using var response = await http.SendAsync(
                message, HttpCompletionOption.ResponseContentRead, cancellationToken);
            stopwatch.Stop();

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var headers = response.Headers
                .Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v)))
                .ToList();

            return new ApiResponse
            {
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase,
                Headers = headers,
                Body = bytes,
                ContentType = response.Content.Headers.ContentType?.ToString(),
                Elapsed = stopwatch.Elapsed,
                Connection = BuildConnection()
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ApiResponse
            {
                Elapsed = stopwatch.Elapsed,
                Error = new ApiError(ApiErrorKind.Timeout, "The request timed out."),
                Connection = BuildConnection()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ApiResponse
            {
                Elapsed = stopwatch.Elapsed,
                Error = new ApiError(ApiErrorKind.None, "Request cancelled.")
            };
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            var kind =
                serverUntrusted ? ApiErrorKind.ServerCertificateUntrusted
                : ex.InnerException is AuthenticationException ? ApiErrorKind.CertificateRefused
                : ApiErrorKind.Network;
            return new ApiResponse
            {
                Elapsed = stopwatch.Elapsed,
                Error = new ApiError(kind, ex.Message),
                Connection = BuildConnection()
            };
        }
        catch (Exception ex) when (ex is UriFormatException or FormatException or InvalidOperationException)
        {
            stopwatch.Stop();
            return new ApiResponse
            {
                Elapsed = stopwatch.Elapsed,
                Error = new ApiError(ApiErrorKind.Unknown, "Invalid request: " + ex.Message)
            };
        }
    }

    private static bool ProxyWillBeUsed(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme is not ("http" or "https")) return false;
            var proxy = HttpClient.DefaultProxy;
            return proxy is not null && !proxy.IsBypassed(uri) && proxy.GetProxy(uri) is not null;
        }
        catch { return false; }
    }

    private static string? FormatProtocol(SslProtocols p) => p switch
    {
        SslProtocols.Tls13 => "TLS 1.3",
        SslProtocols.Tls12 => "TLS 1.2",
        SslProtocols.None => null,
        _ => p.ToString()
    };
}
