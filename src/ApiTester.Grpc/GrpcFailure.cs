using Grpc.Core;

namespace ApiTester.Grpc;

/// <summary>Turns a gRPC failure into one of this project's exception types, keeping the cause. The
/// single place every call path (<see cref="ReflectionClient"/>'s reflection round trips,
/// <see cref="GrpcCaller.InvokeAsync"/>, and <see cref="GrpcCaller.InvokeStreamingAsync"/>) goes
/// through, so a refused certificate or a dead connection is reported identically no matter which of
/// them hit it — the gRPC-shaped sibling of <c>ApiClient.Flatten</c> (ApiTester.Core): a testing tool
/// exists to say why something failed, not just that it did.</summary>
internal static class GrpcFailure
{
    /// <summary>The status, name, detail, and trailers of an RpcException, with the underlying cause
    /// chain appended to the detail when the transport (rather than the application) failed.</summary>
    internal static GrpcStatusException FromRpcException(RpcException ex) =>
        new((int)ex.StatusCode, ex.StatusCode.ToString(), DescribeDetail(ex.Status), ToPairs(ex.Trailers));

    /// <summary>The detail text for a status: the gRPC detail, plus "caused by: Type: message" for
    /// each link of Status.DebugException's chain. A refused client certificate's real reason —
    /// the AuthenticationException and the SChannel status — is several links down and never in the
    /// outermost message, so the chain is the whole diagnostic. Capped at 10 links so a
    /// self-referential chain cannot loop. When there is no DebugException — the normal case for a
    /// genuine application status like PermissionDenied "access denied" — the detail is returned
    /// unchanged, so an ordinary non-OK status does not grow noise.</summary>
    internal static string DescribeDetail(Status status)
    {
        if (status.DebugException is null) return status.Detail;

        var parts = new List<string> { status.Detail };
        int count = 0;
        for (Exception? ex = status.DebugException; ex is not null && count < 10; ex = ex.InnerException, count++)
        {
            parts.Add($"caused by: {ex.GetType().Name}: {ex.Message}");
        }
        return string.Join(" ", parts);
    }

    /// <summary>A binary metadata/trailer entry renders as base64; one bad entry never crashes the
    /// whole conversion.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> ToPairs(Metadata? metadata)
    {
        if (metadata is null || metadata.Count == 0) return Array.Empty<KeyValuePair<string, string>>();
        var list = new List<KeyValuePair<string, string>>(metadata.Count);
        foreach (var entry in metadata)
        {
            string value;
            try { value = entry.IsBinary ? Convert.ToBase64String(entry.ValueBytes) : entry.Value; }
            catch { value = string.Empty; }
            list.Add(new KeyValuePair<string, string>(entry.Key, value));
        }
        return list;
    }
}
