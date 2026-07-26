using System.Diagnostics;
using System.Globalization;
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
        TransportOptions? transport = null,
        Func<X509Certificate2?, bool>? trustServerCertificate = null,
        System.Net.CookieContainer? cookies = null,
        CancellationToken cancellationToken = default)
    {
        transport ??= new TransportOptions();
        // Refuse an unusable combination up front rather than opening a socket and quietly
        // ignoring whichever setting lost.
        if (ValidateTransport(transport, request.Url) is { } problem)
            return new ApiResponse { Error = new ApiError(ApiErrorKind.Unknown, problem) };

        int attempt = 0;
        while (true)
        {
            attempt++;
            var response = await SendOnceAsync(request, clientCertificate, transport,
                                               trustServerCertificate, cookies, cancellationToken);
            if (!ShouldRetry(response, transport, request.Method, attempt, cancellationToken))
                return response with { Attempts = attempt };

            var delay = RetryDelayFor(response, transport, attempt);
            // Waiting is where a retrying send spends nearly all of its time, so the wait — not just
            // the request — has to answer to the caller's cancellation.
            try { await Task.Delay(delay, cancellationToken); }
            catch (OperationCanceledException) { return response with { Attempts = attempt }; }
        }
    }

    /// <summary>Whether the result of one attempt earns another. Kept deterministic and free of the
    /// backoff's randomness so the decision itself is exactly what the observable behavior shows.</summary>
    private static bool ShouldRetry(
        ApiResponse response, TransportOptions transport, HttpMethod method, int attempt, CancellationToken ct)
    {
        if (transport.Retries <= 0) return false;
        // Retries counts the *re*-tries, so Retries = 2 allows a third and last attempt.
        if (attempt > transport.Retries) return false;
        if (ct.IsCancellationRequested) return false;

        // The method the user asked for, not whatever a redirect rewrote it to along the way: their
        // intent is what decides whether sending it a second time is safe.
        bool idempotent = method.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
                          method.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ||
                          method.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase) ||
                          method.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ||
                          method.Method.Equals("DELETE", StringComparison.OrdinalIgnoreCase);
        if (!idempotent && !transport.RetryUnsafeMethods) return false;

        if (response.Error is not null)
        {
            if (!transport.RetryOnTransportError) return false;
            // Only the failures a second attempt could plausibly survive. A refused or untrusted
            // certificate will be refused again — retrying it just fails slower — a redirect loop is
            // still a loop on the next pass, Unknown means the request itself was malformed rather
            // than the connection, and None is the cancelled case, which must not be re-sent at all.
            return response.Error.Kind is ApiErrorKind.Network
                or ApiErrorKind.Timeout
                or ApiErrorKind.ConnectionRefused
                or ApiErrorKind.ConnectionReset
                or ApiErrorKind.NameResolution
                or ApiErrorKind.ProxyFailure;
        }

        return response.StatusCode is { } status && transport.RetryOn.Contains(status);
    }

    /// <summary>How long to wait before the next attempt: exponential backoff with jitter, unless the
    /// server said when to come back. Jitter keeps a fleet of clients that all failed at the same
    /// moment from returning in one synchronized wave.</summary>
    private static TimeSpan RetryDelayFor(ApiResponse response, TransportOptions transport, int attempt)
    {
        const double capMs = 30_000;

        // A server that bothers to say when to come back knows better than any computed guess.
        if (transport.HonorRetryAfter && RetryAfterDelay(response) is { } asked)
            return TimeSpan.FromMilliseconds(Math.Clamp(asked.TotalMilliseconds, 0, capMs));

        // No delay asked for is no delay, however many times it is doubled — and taking this out
        // first keeps the multiplication below from being 0 * infinity, which is NaN rather than a
        // number any clamp can rescue.
        double baseMs = transport.RetryDelay.TotalMilliseconds;
        if (baseMs <= 0) return TimeSpan.Zero;

        // Computed in milliseconds as a double and clamped before it becomes a TimeSpan: doubling a
        // TimeSpan for a large retry count overflows long before the cap would have applied.
        double ms = baseMs * Math.Pow(2, attempt - 1);
        ms *= 0.9 + (Random.Shared.NextDouble() * 0.2);
        return TimeSpan.FromMilliseconds(Math.Clamp(ms, 0, capMs));
    }

    /// <summary>The wait a Retry-After header asks for, or null when there is none this client can make
    /// sense of — in which case the computed backoff stands rather than the send failing over a header.</summary>
    private static TimeSpan? RetryAfterDelay(ApiResponse response)
    {
        string? value = response.Headers
            .FirstOrDefault(h => h.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();

        // Delta-seconds is the common form; an HTTP-date is equally legal and some gateways send it.
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return seconds < 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
        {
            var wait = when - DateTimeOffset.UtcNow;
            // A date that has already passed means "now", not a negative wait.
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    /// <summary>One attempt: build the transport, send, and follow any redirect chain to its end.
    /// Everything about a single send lives here so <see cref="SendAsync"/> can be about nothing but
    /// deciding whether to do it again.</summary>
    private async Task<ApiResponse> SendOnceAsync(
        ApiRequest request,
        X509Certificate2? clientCertificate,
        TransportOptions transport,
        Func<X509Certificate2?, bool>? trustServerCertificate,
        System.Net.CookieContainer? cookies,
        CancellationToken cancellationToken)
    {
        bool ignoreServerCertificateErrors = transport.IgnoreServerCertificateErrors;
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

        bool viaProxy = ProxyWillBeUsed(request.Url, transport);

        var handler = new SocketsHttpHandler
        {
            // Use the machine's configured proxy — including "Automatically detect settings"
            // (WPAD) and a "Use automatic configuration script" (PAC) from Internet Options —
            // authenticating with the signed-in user's Windows credentials when required.
            DefaultProxyCredentials = CredentialCache.DefaultCredentials
        };
        switch (transport.Proxy)
        {
            case ProxyMode.None:
                // Bypassing the proxy also restores the ConnectCallback path below, which is the
                // only place the TLS details can be read.
                handler.UseProxy = false;
                break;
            case ProxyMode.Explicit:
                handler.Proxy = new WebProxy(transport.ProxyUrl)
                {
                    Credentials = transport.ProxyUser is null
                        ? CredentialCache.DefaultCredentials
                        : new NetworkCredential(transport.ProxyUser, transport.ProxyPassword)
                };
                handler.UseProxy = true;
                break;
        }
        // Never let the handler follow redirects: it does so internally, so the intermediate
        // responses — and with them every hop, its status, and where the client certificate was
        // presented — are unobservable. SendAsync runs the chain itself instead.
        handler.AllowAutoRedirect = false;
        // Decoding is the default every other HTTP client uses; turning it off relays the bytes
        // exactly as the server framed them, which is what a byte-exact relay test needs.
        handler.AutomaticDecompression = transport.Decompress
            ? DecompressionMethods.All
            : DecompressionMethods.None;
        // A shared cookie jar carries Set-Cookie values across requests (session testing); without
        // one, the per-handler default container means cookies don't persist between calls.
        if (cookies is not null) { handler.CookieContainer = cookies; handler.UseCookies = true; }

        // Windows Integrated Auth (Negotiate/NTLM): set server credentials so the handler runs the
        // challenge/response handshake automatically. Connection-bound, so it needs the pooled
        // connection to persist across the handshake legs — which SocketsHttpHandler does.
        if (request.WindowsAuth is { } wa)
        {
            handler.Credentials = wa.UseDefaultCredentials
                ? CredentialCache.DefaultCredentials
                : new NetworkCredential(wa.Username, wa.Password, wa.Domain);
            handler.PreAuthenticate = true;
        }

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
                // A --resolve override replaces the address we dial and nothing else: TargetHost
                // (SNI) below and the Host header the handler wrote both keep the original name,
                // so the server sees an ordinary request for the hostname the user typed.
                var pinned = transport.Resolve.FirstOrDefault(r =>
                    r.Port == context.DnsEndPoint.Port &&
                    string.Equals(r.Host, context.DnsEndPoint.Host, StringComparison.OrdinalIgnoreCase));
                EndPoint destination = pinned is null
                    ? context.DnsEndPoint
                    : new IPEndPoint(IPAddress.Parse(pinned.Address), context.DnsEndPoint.Port);
                try { await socket.ConnectAsync(destination, ct); }
                catch { socket.Dispose(); throw; }

                var network = new NetworkStream(socket, ownsSocket: true);
                if (context.InitialRequestMessage.RequestUri!.Scheme != Uri.UriSchemeHttps)
                    return network;

                var ssl = new SslStream(network, leaveInnerStreamOpen: false, Validate);
                // TargetHost stays the requested hostname so SNI is unaffected by a --resolve override.
                var sslOptions = new SslClientAuthenticationOptions { TargetHost = context.DnsEndPoint.Host };
                // Without ALPN a hand-driven TLS stream can never negotiate the pinned version.
                if (transport.Version is HttpVersionMode.Http2)
                    sslOptions.ApplicationProtocols = [SslApplicationProtocol.Http2];
                else if (transport.Version is HttpVersionMode.Http11)
                    sslOptions.ApplicationProtocols = [SslApplicationProtocol.Http11];
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
            ProxyUri = viaProxy ? ProxyUriFor(request.Url, transport) : null,
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
        // Declared outside the try so a failure part-way along a chain still reports the hops that
        // had already been taken — that is where the client certificate went.
        var hops = new List<RedirectHop>();
        try
        {
            string currentUrl = request.Url;
            var currentMethod = request.Method;
            var sentHeaders = request.Headers.ToList();
            bool sendBody = true;

            while (true)
            {
                using var message = BuildMessage(
                    request, transport, currentUrl, currentMethod, sentHeaders, sendBody);

                using var response = await http.SendAsync(
                    message, HttpCompletionOption.ResponseContentRead, cancellationToken);

                if (transport.FollowRedirects && RedirectTarget(response, currentUrl) is { } target)
                {
                    // Refuse the hop rather than take it: a redirect loop would otherwise present
                    // the client certificate forever.
                    if (hops.Count >= transport.MaxRedirects)
                    {
                        stopwatch.Stop();
                        return new ApiResponse
                        {
                            Elapsed = stopwatch.Elapsed,
                            Error = new ApiError(ApiErrorKind.TooManyRedirects,
                                $"Stopped after {transport.MaxRedirects} redirect(s): {currentUrl} " +
                                $"redirected to {target} (raise --max-redirs to follow more)."),
                            Redirects = hops,
                            Connection = BuildConnection()
                        };
                    }

                    // A different scheme, host, or port means the client certificate — and any
                    // credential still attached — is about to be presented somewhere the user
                    // never named, which is the fact this whole record exists to expose.
                    var from = new Uri(currentUrl);
                    bool crossOrigin =
                        !string.Equals(from.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(from.Host, target.Host, StringComparison.OrdinalIgnoreCase) ||
                        from.Port != target.Port;
                    bool carriedAuthorization = sentHeaders.Any(h =>
                        h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
                    // https -> http takes the request off TLS entirely: no client certificate, and
                    // whatever is still in the headers travels in the clear.
                    bool schemeDowngrade =
                        string.Equals(from.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(target.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

                    hops.Add(new RedirectHop(
                        (int)response.StatusCode, currentUrl, target.ToString(),
                        crossOrigin && carriedAuthorization, schemeDowngrade));

                    // .NET strips Authorization on its own cross-origin redirects; match that, but
                    // having recorded it, so a 401 from the new origin is explicable.
                    if (crossOrigin)
                        sentHeaders.RemoveAll(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));

                    // Curl and every browser rewrite a redirected POST as a GET on a 301/302; the
                    // entity was for the resource that moved, not for the one it moved to. A 303
                    // says so for every method — it exists to turn a submission into a retrieval.
                    // 307/308 exist precisely to preserve both, so they touch nothing.
                    bool isPost = string.Equals(currentMethod.Method, "POST", StringComparison.OrdinalIgnoreCase);
                    bool isSafe = string.Equals(currentMethod.Method, "GET", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(currentMethod.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
                    if (((int)response.StatusCode is 301 or 302 && isPost) ||
                        ((int)response.StatusCode is 303 && !isSafe))
                    {
                        currentMethod = HttpMethod.Get;
                        sendBody = false;
                    }

                    currentUrl = target.ToString();
                    continue;
                }

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
                    Redirects = hops,
                    Connection = BuildConnection()
                };
            }
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ApiResponse
            {
                Elapsed = stopwatch.Elapsed,
                Error = new ApiError(ApiErrorKind.Timeout, "The request timed out.", Flatten(ex)),
                Redirects = hops,
                Connection = BuildConnection()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ApiResponse
            {
                Elapsed = stopwatch.Elapsed,
                Error = new ApiError(ApiErrorKind.None, "Request cancelled."),
                Redirects = hops
            };
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            var socketError = FirstSocketError(ex);
            var kind =
                serverUntrusted ? ApiErrorKind.ServerCertificateUntrusted
                : HasInner<AuthenticationException>(ex) ? ApiErrorKind.CertificateRefused
                : socketError switch
                {
                    SocketError.ConnectionRefused => ApiErrorKind.ConnectionRefused,
                    SocketError.ConnectionReset => ApiErrorKind.ConnectionReset,
                    SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData
                        => ApiErrorKind.NameResolution,
                    _ => ex.HttpRequestError == HttpRequestError.ProxyTunnelError
                        ? ApiErrorKind.ProxyFailure
                        : ApiErrorKind.Network
                };
            return new ApiResponse
            {
                Elapsed = stopwatch.Elapsed,
                Error = new ApiError(kind, ex.Message, Flatten(ex), socketError),
                Redirects = hops,
                Connection = BuildConnection()
            };
        }
        catch (IOException ex)   // a multipart file part that can't be read
        {
            stopwatch.Stop();
            return new ApiResponse
            {
                Elapsed = stopwatch.Elapsed,
                Error = new ApiError(ApiErrorKind.Unknown, "Could not read a request file: " + ex.Message, Flatten(ex)),
                Redirects = hops
            };
        }
        catch (Exception ex) when (ex is UriFormatException or FormatException or InvalidOperationException)
        {
            stopwatch.Stop();
            return new ApiResponse
            {
                Elapsed = stopwatch.Elapsed,
                Error = new ApiError(ApiErrorKind.Unknown, "Invalid request: " + ex.Message, Flatten(ex)),
                Redirects = hops
            };
        }
    }

    /// <summary>The message for one hop. A chain rebuilds it from the original <see cref="ApiRequest"/>
    /// every time rather than resending the previous message: an HttpContent can only be consumed
    /// once, and rebuilding is what re-reads a multipart file part from disk on a 307/308 replay.</summary>
    private static HttpRequestMessage BuildMessage(
        ApiRequest request, TransportOptions transport, string url, HttpMethod method,
        IEnumerable<KeyValuePair<string, string>> headers, bool sendBody)
    {
        var message = new HttpRequestMessage(method, url);
        // Pinning is exact: a server that cannot speak the chosen version must fail loudly
        // rather than quietly downgrade, which would defeat the point of asking.
        if (transport.Version is HttpVersionMode.Http11)
        {
            message.Version = HttpVersion.Version11;
            message.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }
        else if (transport.Version is HttpVersionMode.Http2)
        {
            message.Version = HttpVersion.Version20;
            message.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        foreach (var header in headers)
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);

        // A redirect that drops the body drops the entity entirely — no content, and so no
        // Content-Type describing one.
        if (!sendBody) return message;

        if (request.Parts is { Count: > 0 } parts)
        {
            var form = new MultipartFormDataContent();
            foreach (var part in parts)
            {
                if (part.FilePath is { Length: > 0 })
                {
                    var fileContent = new ByteArrayContent(File.ReadAllBytes(part.FilePath));
                    if (part.ContentType is { Length: > 0 })
                        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(part.ContentType);
                    form.Add(fileContent, part.Name, Path.GetFileName(part.FilePath));
                }
                else
                {
                    form.Add(new StringContent(part.Value ?? "", Encoding.UTF8), part.Name);
                }
            }
            message.Content = form;   // boundary + Content-Type are set by MultipartFormDataContent
        }
        else if (request.Body is not null)
        {
            message.Content = new StringContent(request.Body, Encoding.UTF8);
            if (request.ContentType is not null)
                message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
        }

        return message;
    }

    /// <summary>Where a response redirects to, resolved against the URL that produced it, or null
    /// when it is not a redirect worth following. A 3xx without a Location is a final answer —
    /// there is nowhere to go.</summary>
    private static Uri? RedirectTarget(HttpResponseMessage response, string currentUrl)
    {
        if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return null;
        if (!response.Headers.TryGetValues("Location", out var values)) return null;
        string? location = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(location)) return null;
        // Relative Locations are legal and common, so the current URL is the base.
        return Uri.TryCreate(new Uri(currentUrl), location, out var target) ? target : null;
    }

    /// <summary>Null when these options can be used for this URL; otherwise the reason they cannot.
    /// Callers that own a user interface (the CLI) turn a non-null result into a usage error before
    /// sending; SendAsync itself refuses the request rather than silently ignoring a setting.</summary>
    public static string? ValidateTransport(TransportOptions transport, string url)
    {
        if (transport.MaxRedirects < 1)
            return "--max-redirs must be at least 1 (use --no-redirect to stop following redirects).";

        if (transport.Proxy is ProxyMode.Explicit &&
            (string.IsNullOrWhiteSpace(transport.ProxyUrl) ||
             !Uri.TryCreate(transport.ProxyUrl, UriKind.Absolute, out var proxyUri) ||
             proxyUri.Scheme is not ("http" or "https")))
            return $"--proxy must be an absolute http(s) URL, got '{transport.ProxyUrl}'.";

        foreach (var pin in transport.Resolve)
        {
            if (string.IsNullOrWhiteSpace(pin.Host) ||
                pin.Port is < 1 or > 65535 ||
                !IPAddress.TryParse(pin.Address, out _))
                return $"--resolve expects host:port:ip, got '{pin.Host}:{pin.Port}:{pin.Address}'.";
        }

        // There is no ConnectCallback on the proxied path, so a pinned address could only be
        // dropped on the floor — say so instead.
        if (transport.Resolve.Count > 0 && ProxyWillBeUsed(url, transport))
            return $"--resolve cannot be used together with a proxy (the connection is tunnelled " +
                   $"through {ProxyUriFor(url, transport) ?? "a proxy"}, so the address cannot be " +
                   $"pinned). Use --no-proxy to bypass it.";

        return null;
    }

    /// <summary>Whether this request will actually be tunnelled through a proxy. The answer decides
    /// whether the ConnectCallback path (and with it the TLS diagnostics and --resolve) is available.</summary>
    public static bool ProxyWillBeUsed(string url, TransportOptions transport)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme is not ("http" or "https")) return false;
            if (transport.Proxy is ProxyMode.None) return false;
            if (transport.Proxy is ProxyMode.Explicit)
            {
                if (string.IsNullOrWhiteSpace(transport.ProxyUrl)) return false;
                var explicitProxy = new WebProxy(transport.ProxyUrl);
                return !explicitProxy.IsBypassed(uri);
            }
            var proxy = HttpClient.DefaultProxy;
            return proxy is not null && !proxy.IsBypassed(uri) && proxy.GetProxy(uri) is not null;
        }
        catch { return false; }
    }

    /// <summary>The proxy that was used, for the diagnostics panel. Only meaningful once
    /// <see cref="ProxyWillBeUsed"/> has said yes.</summary>
    private static string? ProxyUriFor(string url, TransportOptions transport)
    {
        try
        {
            if (transport.Proxy is ProxyMode.Explicit) return transport.ProxyUrl;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            return HttpClient.DefaultProxy?.GetProxy(uri)?.ToString();
        }
        catch { return null; }
    }

    /// <summary>The exception chain, outermost first. For a refused client certificate the useful
    /// facts — the AuthenticationException and the SChannel status code — are several links down,
    /// and the outer message says nothing about them. Capped so a cyclic chain cannot run away.</summary>
    private static IReadOnlyList<ErrorDetail> Flatten(Exception exception)
    {
        var details = new List<ErrorDetail>();
        for (Exception? ex = exception; ex is not null && details.Count < 8; ex = ex.InnerException)
        {
            int? native = ex switch
            {
                SocketException se => se.ErrorCode,
                System.ComponentModel.Win32Exception w => w.NativeErrorCode,
                _ => null
            };
            details.Add(new ErrorDetail(ex.GetType().Name, ex.Message, native));
        }
        return details;
    }

    private static SocketError? FirstSocketError(Exception exception)
    {
        for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
            if (ex is SocketException se) return se.SocketErrorCode;
        return null;
    }

    private static bool HasInner<T>(Exception exception) where T : Exception
    {
        for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
            if (ex is T) return true;
        return false;
    }

    private static string? FormatProtocol(SslProtocols p) => p switch
    {
        SslProtocols.Tls13 => "TLS 1.3",
        SslProtocols.Tls12 => "TLS 1.2",
        SslProtocols.None => null,
        _ => p.ToString()
    };
}
