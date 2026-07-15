namespace ApiTester.Core;

public enum ApiErrorKind
{
    None,
    CertificateRefused,
    ServerCertificateUntrusted,
    Network,
    Timeout,
    Unknown
}

public sealed record ApiError(ApiErrorKind Kind, string Message);

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

    public bool IsSuccess => Error is null && StatusCode is >= 200 and < 300;
}
