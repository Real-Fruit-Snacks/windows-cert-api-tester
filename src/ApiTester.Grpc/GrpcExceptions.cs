namespace ApiTester.Grpc;

/// <summary>The server does not implement server reflection, so services and methods cannot be
/// discovered → exit 3.</summary>
public sealed class GrpcReflectionUnavailableException(string message) : Exception(message);

/// <summary>The named service or method is not in what the server advertises → exit 3.</summary>
public sealed class GrpcMethodNotFoundException(string message) : Exception(message);

/// <summary>The method exists but its kind is out of scope (client-streaming or bidirectional) → exit 2.</summary>
public sealed class GrpcUnsupportedMethodException(string message) : Exception(message);

/// <summary>The request JSON does not match the discovered input type; the message names the
/// offending field → exit 3.</summary>
public sealed class GrpcJsonException(string message) : Exception(message);

/// <summary>A call ended with a status other than OK, on a path whose return shape cannot carry one
/// (discovery, or mid-stream) → exit 1.</summary>
public sealed class GrpcStatusException(int statusCode, string statusName, string statusDetail,
                                        IReadOnlyList<KeyValuePair<string, string>> trailers)
    : Exception($"{statusName} ({statusCode}): {statusDetail}")
{
    public int StatusCode { get; } = statusCode;
    public string StatusName { get; } = statusName;
    public string StatusDetail { get; } = statusDetail;
    public IReadOnlyList<KeyValuePair<string, string>> Trailers { get; } = trailers;
}

/// <summary>The supplied descriptor set file is missing, unreadable, not a parseable
/// FileDescriptorSet, or does not contain what the call needs -> exit 3.</summary>
public sealed class GrpcDescriptorSetException(string message) : Exception(message);
