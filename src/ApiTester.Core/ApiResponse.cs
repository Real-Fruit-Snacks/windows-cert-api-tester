namespace ApiTester.Core;

public enum ApiErrorKind
{
    None,
    CertificateRefused,
    ServerCertificateUntrusted,
    Network,
    Timeout,
    Unknown,
    // Appended so the string values already written into saved workspaces keep their meaning.
    TooManyRedirects,
    ProxyFailure,
    NameResolution,
    ConnectionRefused,
    ConnectionReset
}

/// <summary>One link of a failure's InnerException chain. The useful facts about a refused client
/// certificate — the AuthenticationException and the SChannel status code — are never in the
/// outermost message, so the chain is kept rather than flattened to a single string.</summary>
public sealed record ErrorDetail(string ExceptionType, string Message, int? NativeErrorCode);

public sealed record ApiError(
    ApiErrorKind Kind,
    string Message,
    IReadOnlyList<ErrorDetail>? Detail = null,
    System.Net.Sockets.SocketError? SocketErrorCode = null);

/// <summary>One hop of a redirect chain. <paramref name="AuthorizationDropped"/> and
/// <paramref name="SchemeDowngrade"/> are the facts a user cannot otherwise see: a cross-origin
/// redirect strips the Authorization header and presents the client certificate somewhere the
/// user never named.</summary>
public sealed record RedirectHop(
    int StatusCode,
    string From,
    string To,
    bool AuthorizationDropped,
    bool SchemeDowngrade);

public sealed record ApiResponse
{
    public int? StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }
        = Array.Empty<KeyValuePair<string, string>>();
    public byte[] Body { get; init; } = Array.Empty<byte>();
    public string? ContentType { get; init; }
    public TimeSpan Elapsed { get; init; }
    public ApiError? Error { get; init; }
    public ConnectionInfo? Connection { get; init; }

    /// <summary>The redirects followed to reach this response, oldest first. Empty when the first
    /// response was final.</summary>
    public IReadOnlyList<RedirectHop> Redirects { get; init; } = Array.Empty<RedirectHop>();

    public bool IsSuccess => Error is null && StatusCode is >= 200 and < 300;
}

/// <summary>Transport-level diagnostics captured during the request.</summary>
public sealed record ConnectionInfo
{
    public bool ViaProxy { get; init; }

    /// <summary>The proxy the request actually went through, null when the connection was direct.
    /// Naming it is what lets a user work out where a client certificate got stripped.</summary>
    public string? ProxyUri { get; init; }
    public string? TlsProtocol { get; init; }
    public string? CipherSuite { get; init; }
    public bool ClientCertificateSent { get; init; }
    public string? ClientCertificateSubject { get; init; }
    public string? ServerCertificateSubject { get; init; }
    public string? ServerCertificateIssuer { get; init; }
    public string? ServerCertificateThumbprint { get; init; }
    public DateTime? ServerCertificateNotAfter { get; init; }
    public IReadOnlyList<string> ServerCertificateChain { get; init; } = Array.Empty<string>();
}
