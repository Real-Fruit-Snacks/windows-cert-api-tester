using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

/// <summary>Thrown when a forwarded request's target resolves to a host other than the
/// configured upstream — refused so the client certificate can never reach an arbitrary host.</summary>
public sealed class GatewayTargetException(string target)
    : Exception($"Request target '{target}' resolves off the configured upstream host and was refused.");

/// <summary>Thrown when a forwarded request's path matches no configured route — nothing is
/// contacted, because there is no upstream to contact.</summary>
public sealed class GatewayRouteNotFoundException(string target)
    : Exception($"Request target '{target}' matches no configured upstream route.");

/// <summary>Hop-by-hop headers that must never be forwarded through a proxy (RFC 7230 §6.1),
/// plus Host/Content-Length which the HTTP client manages itself.</summary>
public static class HopByHop
{
    private static readonly string[] AllNames =
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host", "Content-Length"
    };

    private static readonly HashSet<string> Lookup = new(AllNames, StringComparer.OrdinalIgnoreCase);

    /// <summary>The same ten names <see cref="Is"/> matches, exposed read-only so a pinning test can
    /// assert every one of them is also a name <see cref="HeaderRules"/> refuses to let a user
    /// manage: <see cref="MtlsGateway"/>.ForwardAsync strips these names from the outgoing request
    /// only after HeaderRules has already applied its rules, so a name added here but not to
    /// HeaderRules' Refused set would be a rule `certapi serve` accepts on the command line and then
    /// silently discards.</summary>
    public static IReadOnlyCollection<string> Names { get; } = Array.AsReadOnly(AllNames);

    public static bool Is(string headerName) => Lookup.Contains(headerName);
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

/// <summary>Forwards HTTP requests to the upstreams of a <see cref="GatewayRoutes"/> table over
/// mutual TLS with a client certificate. Long-lived: construct once, forward many requests
/// concurrently, dispose at shutdown. Redirects are not followed and bodies are relayed as raw
/// bytes.</summary>
public sealed class MtlsGateway : IDisposable
{
    private readonly GatewayRoutes _routes;
    /// <summary>One client per distinct upstream authority, so routes that share a host share a
    /// connection pool and a TLS session rather than opening a second one each.</summary>
    private readonly Dictionary<string, HttpClient> _clients;

    /// <summary>A single upstream mounted at the root — the shape the gateway had before it could
    /// carry several upstreams at once.</summary>
    public MtlsGateway(Uri upstreamBase, X509Certificate2? clientCertificate,
                       bool ignoreServerCertificateErrors, TimeSpan timeout)
        : this(new GatewayRoutes(new[] { new GatewayRoute("/", upstreamBase) }),
               clientCertificate, ignoreServerCertificateErrors, timeout)
    {
    }

    public MtlsGateway(GatewayRoutes routes, X509Certificate2? clientCertificate,
                       bool ignoreServerCertificateErrors, TimeSpan timeout,
                       TransportOptions? transport = null)
    {
        _routes = routes;
        _clients = new Dictionary<string, HttpClient>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes.All)
        {
            string authority = Authority(route.Upstream);
            if (!_clients.ContainsKey(authority))
                _clients[authority] = CreateClient(
                    clientCertificate, ignoreServerCertificateErrors, timeout, transport);
        }
    }

    private static HttpClient CreateClient(X509Certificate2? clientCertificate,
        bool ignoreServerCertificateErrors, TimeSpan timeout, TransportOptions? transport)
    {
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

        if (transport is not null) ApplyTransport(handler, transport);

        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    /// <summary>The transport controls that make sense for a relay: how to reach the upstream.
    /// Decompression and redirects are deliberately not among them — the gateway hands the browser
    /// the bytes and the 3xx the upstream sent.</summary>
    private static void ApplyTransport(SocketsHttpHandler handler, TransportOptions transport)
    {
        ProxyConfiguration.Apply(handler, transport);

        // Only install the callback when something is actually pinned, so the ordinary path stays
        // the handler's own connect logic.
        if (transport.Resolve.Count == 0) return;
        handler.ConnectCallback = async (context, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            // A --resolve override replaces the address dialled and nothing else: the TLS server
            // name and the Host header still carry the hostname the route names.
            var pinned = transport.Resolve.FirstOrDefault(r =>
                r.Port == context.DnsEndPoint.Port &&
                string.Equals(r.Host, context.DnsEndPoint.Host, StringComparison.OrdinalIgnoreCase));
            EndPoint destination = pinned is null
                ? context.DnsEndPoint
                : new IPEndPoint(IPAddress.Parse(pinned.Address), context.DnsEndPoint.Port);
            try
            {
                await socket.ConnectAsync(destination, ct);
                // Hand back the plain stream: the handler still applies its own SslOptions — the
                // client certificate and the validation callback — on top of it, so mutual TLS is
                // exactly what it is without a pinned address.
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }

    private static string Authority(Uri uri) => uri.GetLeftPart(UriPartial.Authority);

    public async Task<GatewayResponse> ForwardAsync(GatewayRequest request, CancellationToken ct)
    {
        var route = _routes.Resolve(request.PathAndQuery, out var forwardedPathAndQuery)
                    ?? throw new GatewayRouteNotFoundException(request.PathAndQuery);

        var uri = new Uri(route.Upstream, forwardedPathAndQuery);
        // Re-applied per route, and the reason a target like "//evil.com/x" cannot walk the client
        // certificate to a host the user never configured: it resolves off this route's upstream.
        if (Authority(uri) != Authority(route.Upstream))
            throw new GatewayTargetException(request.PathAndQuery);
        var http = _clients[Authority(route.Upstream)];
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
        var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);

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

    public void Dispose()
    {
        foreach (var client in _clients.Values) client.Dispose();
    }
}
