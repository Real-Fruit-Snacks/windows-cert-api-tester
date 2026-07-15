using System.Net.Http;

namespace ApiTester.Core;

public sealed record ApiRequest
{
    public required HttpMethod Method { get; init; }
    public required string Url { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }
        = Array.Empty<KeyValuePair<string, string>>();
    public string? Body { get; init; }
    public string? ContentType { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(100);
}
