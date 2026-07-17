using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

/// <summary>Thrown when a forwarded request's target resolves to a host other than the
/// configured upstream — refused so the client certificate can never reach an arbitrary host.</summary>
public sealed class GatewayTargetException(string target)
    : Exception($"Request target '{target}' resolves off the configured upstream host and was refused.");

/// <summary>Hop-by-hop headers that must never be forwarded through a proxy (RFC 7230 §6.1),
/// plus Host/Content-Length which the HTTP client manages itself.</summary>
public static class HopByHop
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host", "Content-Length"
    };

    public static bool Is(string headerName) => Names.Contains(headerName);
}

/// <summary>One incoming request to forward upstream.</summary>
public sealed record GatewayRequest(
    string Method,
    string PathAndQuery,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    Stream? Body,
    string? ContentType);

/// <summary>The upstream response. Copy <see cref="Body"/> to your output, then dispose
/// <see cref="Lifetime"/> (which releases the underlying HttpResponseMessage).</summary>
public sealed record GatewayResponse(
    int StatusCode,
    string? ReasonPhrase,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    Stream Body,
    IDisposable Lifetime);

/// <summary>Forwards HTTP requests to one upstream base URL over mutual TLS with a client
/// certificate. Long-lived: construct once, forward many requests concurrently, dispose at
/// shutdown. Redirects are not followed and bodies are relayed as raw bytes.</summary>
public sealed class MtlsGateway : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _upstreamBase;

    public MtlsGateway(Uri upstreamBase, X509Certificate2? clientCertificate,
                       bool ignoreServerCertificateErrors, TimeSpan timeout)
    {
        _upstreamBase = upstreamBase;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,                       // the caller's app decides on 3xx
            AutomaticDecompression = DecompressionMethods.None, // relay bytes exactly as received
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, errors) =>
                    errors == SslPolicyErrors.None || ignoreServerCertificateErrors
            }
        };
        if (clientCertificate is not null)
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };

        _http = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    public async Task<GatewayResponse> ForwardAsync(GatewayRequest request, CancellationToken ct)
    {
        var uri = new Uri(_upstreamBase, request.PathAndQuery);
        if (uri.GetLeftPart(UriPartial.Authority) != _upstreamBase.GetLeftPart(UriPartial.Authority))
            throw new GatewayTargetException(request.PathAndQuery);
        var message = new HttpRequestMessage(new HttpMethod(request.Method), uri);

        string? contentType = request.ContentType;
        foreach (var h in request.Headers)
        {
            if (HopByHop.Is(h.Key)) continue;
            if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { contentType ??= h.Value; continue; }
            message.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        if (request.Body is not null && MethodAllowsBody(request.Method))
        {
            message.Content = new StreamContent(request.Body);
            if (!string.IsNullOrEmpty(contentType) && MediaTypeHeaderValue.TryParse(contentType, out var mt))
                message.Content.Headers.ContentType = mt;
        }

        // ResponseHeadersRead: return as soon as headers arrive; the body streams lazily.
        var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var h in response.Headers)
            if (!HopByHop.Is(h.Key))
                foreach (var v in h.Value) headers.Add(new(h.Key, v));
        foreach (var h in response.Content.Headers)
            // Content-Length is hop-by-hop for the forwarded *request* (HttpClient sets it), but on
            // the *response* it frames the body the caller receives, so relay it through.
            if (!HopByHop.Is(h.Key) || h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                foreach (var v in h.Value) headers.Add(new(h.Key, v));

        var bodyStream = await response.Content.ReadAsStreamAsync(ct);
        return new GatewayResponse(
            (int)response.StatusCode, response.ReasonPhrase, headers, bodyStream, response);
    }

    private static bool MethodAllowsBody(string method) =>
        !method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
        !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _http.Dispose();
}
