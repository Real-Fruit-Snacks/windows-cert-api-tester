namespace ApiTester.Grpc;

/// <summary>A service the server advertises via reflection, with the methods it exposes. Discovery
/// output is a plain snapshot rather than a live handle, so a caller can print or filter it without
/// holding the channel open.</summary>
public sealed record GrpcServiceInfo(string Name, IReadOnlyList<GrpcMethodInfo> Methods);

/// <summary>One method on a service. InputType/OutputType are fully qualified Protobuf message
/// names with no leading dot, as a descriptor reports them — matching the wire format rather than
/// the C# generated-code name, so what is printed lines up with what `.proto` authors wrote.</summary>
public sealed record GrpcMethodInfo(string Name, bool ClientStreaming, bool ServerStreaming,
                                    string InputType, string OutputType);

/// <summary>The outcome of one call. StatusCode is the numeric gRPC status (0 = OK) and StatusName
/// its name, so a caller can report "PermissionDenied (7)" without this assembly's callers needing
/// to reference Grpc.Core themselves — the whole point of keeping the dependency contained here.
/// ResponseJson is empty when the status is not OK: there is no message to render.</summary>
public sealed record GrpcCallResult(
    int StatusCode, string StatusName, string StatusDetail, string ResponseJson,
    IReadOnlyList<KeyValuePair<string, string>> Trailers, TimeSpan Elapsed);
