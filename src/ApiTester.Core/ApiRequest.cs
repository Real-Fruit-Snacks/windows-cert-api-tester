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

    /// <summary>When set (and non-empty), the request is sent as multipart/form-data built from
    /// these parts, and <see cref="Body"/> is ignored.</summary>
    public IReadOnlyList<MultipartPart>? Parts { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(100);
}

/// <summary>One part of a multipart/form-data body: a text field (<see cref="Value"/> set) or a
/// file field (<see cref="FilePath"/> set, read at send time).</summary>
public sealed record MultipartPart(string Name, string? Value, string? FilePath, string? ContentType = null);
