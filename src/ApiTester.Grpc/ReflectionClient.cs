using System.IO;
using System.Net.Http;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Reflection.V1Alpha;

namespace ApiTester.Grpc;

/// <summary>Thin wrapper over <see cref="ServerReflection.ServerReflectionClient"/> (v1alpha — what
/// <c>Grpc.AspNetCore.Server.Reflection</c>, and effectively every server, implements). No
/// <see cref="Grpc.Core"/> exception type ever escapes this class: every failure becomes one of the
/// five exceptions declared in GrpcExceptions.cs, so callers never need to reference Grpc.Core
/// themselves.</summary>
internal sealed class ReflectionClient(GrpcChannel channel)
{
    private readonly ServerReflection.ServerReflectionClient _client = new(channel);

    internal async Task<IReadOnlyList<string>> ListServicesAsync(CancellationToken ct)
    {
        var response = await SendAsync(new ServerReflectionRequest { ListServices = "" }, ct);
        return response.MessageResponseCase switch
        {
            ServerReflectionResponse.MessageResponseOneofCase.ListServicesResponse =>
                response.ListServicesResponse.Service.Select(s => s.Name).ToList(),
            ServerReflectionResponse.MessageResponseOneofCase.ErrorResponse =>
                throw ToException(response.ErrorResponse, "the service list"),
            _ => throw Unexpected(response)
        };
    }

    internal async Task<IReadOnlyList<ByteString>> FileContainingSymbolAsync(string symbol, CancellationToken ct)
    {
        var response = await SendAsync(new ServerReflectionRequest { FileContainingSymbol = symbol }, ct);
        return ExtractFileDescriptors(response, symbol);
    }

    internal async Task<IReadOnlyList<ByteString>> FileByFilenameAsync(string filename, CancellationToken ct)
    {
        var response = await SendAsync(new ServerReflectionRequest { FileByFilename = filename }, ct);
        return ExtractFileDescriptors(response, filename);
    }

    private static IReadOnlyList<ByteString> ExtractFileDescriptors(ServerReflectionResponse response, string askedFor) =>
        response.MessageResponseCase switch
        {
            ServerReflectionResponse.MessageResponseOneofCase.FileDescriptorResponse =>
                response.FileDescriptorResponse.FileDescriptorProto.ToList(),
            ServerReflectionResponse.MessageResponseOneofCase.ErrorResponse =>
                throw ToException(response.ErrorResponse, askedFor),
            _ => throw Unexpected(response)
        };

    /// <summary>One request, one response: open the bidirectional stream, write the single request,
    /// complete the request side, read exactly one response, then let <c>using</c> dispose the call.
    /// A stream per request keeps the state machine trivial and is well within what any server
    /// tolerates.
    /// <para>A dead connection (a refused TLS handshake — an untrusted server certificate, or a
    /// mutual-TLS server given no client certificate — tears down the HTTP/2 connection this call
    /// needs) fails the write below as a bare cancellation or IO exception, not an
    /// <see cref="RpcException"/>: the authoritative status, with the real cause hanging off
    /// <see cref="Status.DebugException"/>, is only obtainable afterwards by reading the response
    /// stream. So a write failure that is not itself an <see cref="RpcException"/> — and that the
    /// caller did not itself request via <paramref name="ct"/> — is swallowed here and the read is
    /// attempted anyway; only if that read also fails to produce an <see cref="RpcException"/> does
    /// the original write failure become the fallback <c>Unavailable</c> status below.</para></summary>
    private async Task<ServerReflectionResponse> SendAsync(ServerReflectionRequest request, CancellationToken ct)
    {
        using var call = _client.ServerReflectionInfo(cancellationToken: ct);

        Exception? writeFailure = null;
        try
        {
            await call.RequestStream.WriteAsync(request);
            await call.RequestStream.CompleteAsync();
        }
        catch (RpcException ex)
        {
            throw Translate(ex);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is TaskCanceledException or OperationCanceledException or IOException or HttpRequestException)
        {
            writeFailure = ex;
        }

        try
        {
            if (!await call.ResponseStream.MoveNext(ct))
            {
                throw new GrpcStatusException((int)StatusCode.Internal, nameof(StatusCode.Internal),
                    "The server reflection stream ended without sending a response.",
                    Array.Empty<KeyValuePair<string, string>>());
            }
            return call.ResponseStream.Current;
        }
        catch (RpcException ex)
        {
            throw Translate(ex);
        }
        catch (Exception) when (writeFailure is not null)
        {
            // Last resort: reading the response stream after the write died did not produce an
            // RpcException either — the connection failed so completely that even this read just
            // cancels again. Report Unavailable with the cause chain the original write failure
            // carried, rather than letting a bare TaskCanceledException reach the user.
            throw new GrpcStatusException((int)StatusCode.Unavailable, nameof(StatusCode.Unavailable),
                GrpcFailure.DescribeDetail(new Status(StatusCode.Unavailable, "", writeFailure)),
                Array.Empty<KeyValuePair<string, string>>());
        }

        // Translates an RpcException from either the write or the read above. A caller-requested
        // cancellation (ct.IsCancellationRequested) must stay a cancellation — Ctrl+C is not a gRPC
        // status — and Unimplemented must stay ahead of the general translation, because "the
        // server does not implement reflection" is a data error (exit 3), not a transport failure
        // (exit 1).
        Exception Translate(RpcException ex)
        {
            if (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested) return new OperationCanceledException(ct);
            if (ex.StatusCode == StatusCode.Unimplemented) return ReflectionUnavailable();
            return GrpcFailure.FromRpcException(ex);
        }
    }

    /// <summary>gRPC error code 12 (<see cref="StatusCode.Unimplemented"/>) is how a server that does
    /// not host the reflection service answers a reflection request at all — this is the common shape
    /// that also shows up as an <see cref="RpcException"/> rather than an <see cref="ErrorResponse"/>
    /// on some server stacks, which is why both paths funnel into the same message.</summary>
    private static Exception ToException(ErrorResponse error, string askedFor) => error.ErrorCode switch
    {
        (int)StatusCode.Unimplemented => ReflectionUnavailable(),
        (int)StatusCode.NotFound => new GrpcMethodNotFoundException(
            $"Server reflection has nothing for '{askedFor}': {error.ErrorMessage}"),
        _ => new GrpcStatusException(error.ErrorCode, ((StatusCode)error.ErrorCode).ToString(), error.ErrorMessage,
            Array.Empty<KeyValuePair<string, string>>())
    };

    private static GrpcReflectionUnavailableException ReflectionUnavailable() => new(
        "The server does not implement gRPC server reflection (grpc.reflection.v1alpha.ServerReflection), " +
        "so certapi grpc cannot learn the service's request and response message types by asking it. " +
        "Supply a compiled descriptor set instead: --protoset <file>, produced by " +
        "protoc --descriptor_set_out=<file> --include_imports <proto>.");

    private static GrpcStatusException Unexpected(ServerReflectionResponse response) => new(
        (int)StatusCode.Internal, nameof(StatusCode.Internal),
        $"Unexpected server reflection response: {response.MessageResponseCase}.",
        Array.Empty<KeyValuePair<string, string>>());
}
