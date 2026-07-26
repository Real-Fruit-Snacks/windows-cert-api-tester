namespace ApiTester.Core;

/// <summary>How the connection reaches the server. The distinction matters for mTLS: a proxy
/// tunnels the TLS session, so the client certificate and the handshake details are no longer
/// something this client can see or control.</summary>
public enum ProxyMode
{
    /// <summary>Use the machine's configured proxy (including WPAD/PAC) — the historical behavior.</summary>
    System,
    /// <summary>Ignore any configured proxy and connect directly.</summary>
    None,
    /// <summary>Route through the proxy named by <see cref="TransportOptions.ProxyUrl"/>.</summary>
    Explicit
}

/// <summary>Which HTTP version to insist on. Pinning is a diagnostic tool: a gateway that
/// behaves differently on HTTP/1.1 and HTTP/2 is otherwise very hard to catch.</summary>
public enum HttpVersionMode
{
    /// <summary>Let the handler negotiate — the historical behavior.</summary>
    Auto,
    Http11,
    Http2
}

/// <summary>Pin <paramref name="Host"/>:<paramref name="Port"/> to <paramref name="Address"/> so the
/// connection is dialled at a chosen address while the TLS server name and the Host header still
/// carry the original hostname — the only way to test one node of a load-balanced endpoint.</summary>
public sealed record ResolveOverride(string Host, int Port, string Address);

/// <summary>*How* to send a request, as opposed to <see cref="ApiRequest"/>, which is *what* to send.
/// Every default reproduces the behavior this client had before transport control existed, apart
/// from <see cref="Decompress"/>, which now matches every other HTTP client.</summary>
public sealed record TransportOptions
{
    public ProxyMode Proxy { get; init; } = ProxyMode.System;
    public string? ProxyUrl { get; init; }
    public string? ProxyUser { get; init; }
    public string? ProxyPassword { get; init; }

    public bool FollowRedirects { get; init; } = true;
    public int MaxRedirects { get; init; } = 20;

    /// <summary>Decode gzip/deflate/brotli responses. Turn it off to relay the bytes exactly as the
    /// server sent them.</summary>
    public bool Decompress { get; init; } = true;
    public HttpVersionMode Version { get; init; } = HttpVersionMode.Auto;

    /// <summary>Accept any server certificate. A TLS concern, so it lives with the other transport
    /// settings rather than with the request content.</summary>
    public bool IgnoreServerCertificateErrors { get; init; }

    public IReadOnlyList<ResolveOverride> Resolve { get; init; } = Array.Empty<ResolveOverride>();

    /// <summary>How many times to retry a failed request. 0 = off, which is the behavior this client
    /// had before retry existed.</summary>
    public int Retries { get; init; }

    /// <summary>Response statuses that earn a retry. The default set is the four a load balancer or
    /// gateway returns when the answer is "not now" rather than "no".</summary>
    public IReadOnlyList<int> RetryOn { get; init; } = new[] { 429, 502, 503, 504 };

    /// <summary>The first backoff delay; each further attempt doubles it, with jitter, capped at 30s.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Retry connection refused/reset, DNS failures, and timeouts.</summary>
    public bool RetryOnTransportError { get; init; } = true;

    /// <summary>Let a Retry-After header override the computed backoff. The server knows better than
    /// the client's guess when it bothers to say.</summary>
    public bool HonorRetryAfter { get; init; } = true;

    /// <summary>Also retry POST and PATCH. Off by default: re-sending a POST nobody confirmed can
    /// charge a card twice, and a client cannot tell a lost response from a lost request.</summary>
    public bool RetryUnsafeMethods { get; init; }
}
