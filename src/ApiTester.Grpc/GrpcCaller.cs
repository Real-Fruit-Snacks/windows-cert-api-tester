using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;
using Grpc.Core;
using Grpc.Net.Client;

namespace ApiTester.Grpc;

/// <summary>The contained entry point to gRPC support: everything in <see cref="Grpc.Core"/>,
/// <see cref="Grpc.Net.Client"/>, and Google.Protobuf stays behind this class — no type from any of
/// them appears in a public signature here, so a caller (the CLI) never has to reference gRPC's own
/// types.
/// <para>Discovery and invocation report a non-OK status differently, because the two return shapes
/// cannot both carry one the same way: <see cref="InvokeAsync"/> returns a non-OK status as data —
/// <see cref="GrpcCallResult"/> has fields for exactly that — while <see cref="DiscoverAsync"/> and
/// <see cref="InvokeStreamingAsync"/> throw <see cref="GrpcStatusException"/>, since neither of those
/// return shapes has anywhere to put a status.</para></summary>
public sealed class GrpcCaller : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly ReflectionClient _reflection;
    private readonly DescriptorPool _descriptors;

    // A pass-through marshaller: every call's request is already encoded, and its response is
    // decoded by ProtoJsonReader afterwards, so nothing here needs Grpc.Core to know about Protobuf
    // at all. Request and response share the same byte[]-to-byte[] shape, so one marshaller instance
    // serves both positions Method<TRequest, TResponse> requires, for every call this class makes.
    private static readonly Marshaller<byte[]> ByteMarshaller = Marshallers.Create<byte[]>(b => b, b => b);

    /// <param name="trustServerCertificate">Consulted when the server's certificate fails ordinary
    /// validation, so a host pinned with `certapi trust add` is reachable without --insecure — the
    /// same seam ApiClient already uses.</param>
    public GrpcCaller(Uri address, X509Certificate2? clientCertificate, TransportOptions transport,
                       Func<X509Certificate2?, bool>? trustServerCertificate = null)
    {
        // Eager: a bad address (wrong scheme, unreachable channel construction) should fail fast,
        // before the caller has invested in a discovery or invoke call.
        _channel = GrpcChannelFactory.Create(address, clientCertificate, transport, trustServerCertificate);
        _reflection = new ReflectionClient(_channel);
        _descriptors = new DescriptorPool(_reflection);
    }

    /// <summary>The services the server advertises via reflection, in the order it listed them, each
    /// with its methods. If one service's descriptors cannot be resolved, that service is returned
    /// with an empty method list rather than failing the whole listing — one uncooperative service
    /// should not hide the rest, and an empty list is visible instead of silent. Throws
    /// <see cref="GrpcReflectionUnavailableException"/> if the server does not implement reflection at
    /// all, or <see cref="GrpcStatusException"/> for any other failure listing services.</summary>
    public async Task<IReadOnlyList<GrpcServiceInfo>> DiscoverAsync(CancellationToken ct)
    {
        var names = await _reflection.ListServicesAsync(ct);
        var services = new List<GrpcServiceInfo>(names.Count);
        foreach (var name in names)
        {
            IReadOnlyList<GrpcMethodInfo> methods;
            try
            {
                var service = await _descriptors.FindServiceAsync(name, ct);
                methods = service.Methods.Select(m => new GrpcMethodInfo(
                    m.Name, m.IsClientStreaming, m.IsServerStreaming, m.InputType.FullName, m.OutputType.FullName)).ToList();
            }
            catch (GrpcMethodNotFoundException)
            {
                methods = Array.Empty<GrpcMethodInfo>();
            }
            services.Add(new GrpcServiceInfo(name, methods));
        }
        return services;
    }

    /// <summary>Makes one unary call. Request JSON that does not match the discovered input type
    /// throws <see cref="GrpcJsonException"/> naming the offending field. A metadata key ending in
    /// "-bin" needs a binary value, which a string pair cannot supply, so that throws
    /// <see cref="ArgumentException"/> naming the key — the command layer turns that into a usage
    /// error, since it is a caller mistake rather than a call failure.</summary>
    public async Task<GrpcCallResult> InvokeAsync(string service, string method, string requestJson,
        IReadOnlyList<KeyValuePair<string, string>> metadata, CancellationToken ct)
    {
        var serviceDescriptor = await _descriptors.FindServiceAsync(service, ct);
        var methodDescriptor = serviceDescriptor.Methods.FirstOrDefault(m => m.Name == method);
        if (methodDescriptor is null)
        {
            string known = serviceDescriptor.Methods.Count == 0
                ? "(none)"
                : string.Join(", ", serviceDescriptor.Methods.Select(m => m.Name));
            throw new GrpcMethodNotFoundException(
                $"Method '{method}' was not found on service '{service}'. Methods that do exist: {known}.");
        }
        if (methodDescriptor.IsClientStreaming)
        {
            throw new GrpcUnsupportedMethodException(
                $"'{service}/{method}' is client-streaming; client-streaming and bidirectional methods are out of scope for this version.");
        }
        if (methodDescriptor.IsServerStreaming)
        {
            throw new GrpcUnsupportedMethodException(
                $"'{service}/{method}' is server-streaming; use the streaming call instead.");
        }

        byte[] requestBytes = ProtoJsonWriter.ToProtobuf(methodDescriptor.InputType, requestJson, _descriptors.FindMessage);
        var callMetadata = BuildMetadata(metadata);

        var grpcMethod = new Method<byte[], byte[]>(MethodType.Unary, service, method, ByteMarshaller, ByteMarshaller);

        var stopwatch = Stopwatch.StartNew();
        using var call = _channel.CreateCallInvoker().AsyncUnaryCall(
            grpcMethod, host: null, new CallOptions(callMetadata, cancellationToken: ct), requestBytes);
        try
        {
            byte[] responseBytes = await call.ResponseAsync;
            var elapsed = stopwatch.Elapsed;
            string responseJson = ProtoJsonReader.ToJson(methodDescriptor.OutputType, responseBytes, indented: true, _descriptors.FindMessage);
            return new GrpcCallResult(0, "OK", "", responseJson, GrpcFailure.ToPairs(call.GetTrailers()), elapsed);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
        {
            // A user who pressed Ctrl+C did not get an answer from the server; do not hand back a
            // GrpcCallResult that looks like one.
            throw new OperationCanceledException(ct);
        }
        catch (RpcException ex)
        {
            var elapsed = stopwatch.Elapsed;
            var failure = GrpcFailure.FromRpcException(ex);
            return new GrpcCallResult(failure.StatusCode, failure.StatusName, failure.StatusDetail, "", failure.Trailers, elapsed);
        }
    }

    /// <summary>Invokes a server-streaming method, yielding each message as compact single-line JSON
    /// (<c>indented: false</c>) — deliberately unlike <see cref="InvokeAsync"/>'s indented output: a
    /// stream of pretty-printed objects is neither readable nor pipeable, whereas one compact object
    /// per line is both.
    /// <para>A non-OK status that ends the stream throws <see cref="GrpcStatusException"/>, carrying
    /// the code, name, detail, and trailers — after every message that did arrive has already been
    /// yielded, so a caller can see the partial stream and still learn why it stopped.
    /// <see cref="IAsyncEnumerable{T}"/> has nowhere else to put a status, which is why this path
    /// throws where <see cref="InvokeAsync"/> returns one as data. For the same reason, a
    /// successful stream's trailers and total elapsed time are not returned here: the
    /// <c>IAsyncEnumerable&lt;string&gt;</c> return shape cannot carry them, so a caller that needs
    /// timing measures it itself.</para>
    /// <para>A consumer that stops enumerating early (a <c>break</c> out of <c>await foreach</c>)
    /// disposes and cancels the underlying call rather than leaking a half-open stream — the
    /// mechanism the command layer's <c>--max-messages</c> relies on. Request JSON that does not
    /// match the discovered input type throws <see cref="GrpcJsonException"/>; a client-streaming or
    /// bidirectional method throws <see cref="GrpcUnsupportedMethodException"/>, as does a unary
    /// method (use <see cref="InvokeAsync"/> instead). Because this is an async iterator, none of
    /// that runs until the caller actually begins enumerating — not at the point this method is
    /// called.</para></summary>
    public async IAsyncEnumerable<string> InvokeStreamingAsync(
        string service, string method, string requestJson,
        IReadOnlyList<KeyValuePair<string, string>> metadata,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var serviceDescriptor = await _descriptors.FindServiceAsync(service, ct);
        var methodDescriptor = serviceDescriptor.Methods.FirstOrDefault(m => m.Name == method);
        if (methodDescriptor is null)
        {
            string known = serviceDescriptor.Methods.Count == 0
                ? "(none)"
                : string.Join(", ", serviceDescriptor.Methods.Select(m => m.Name));
            throw new GrpcMethodNotFoundException(
                $"Method '{method}' was not found on service '{service}'. Methods that do exist: {known}.");
        }
        if (methodDescriptor.IsClientStreaming)
        {
            throw new GrpcUnsupportedMethodException(
                $"'{service}/{method}' is client-streaming; client-streaming and bidirectional methods are out of scope for this version.");
        }
        if (!methodDescriptor.IsServerStreaming)
        {
            throw new GrpcUnsupportedMethodException(
                $"'{service}/{method}' is unary; use InvokeAsync instead.");
        }

        byte[] requestBytes = ProtoJsonWriter.ToProtobuf(methodDescriptor.InputType, requestJson, _descriptors.FindMessage);
        var callMetadata = BuildMetadata(metadata);

        var grpcMethod = new Method<byte[], byte[]>(MethodType.ServerStreaming, service, method, ByteMarshaller, ByteMarshaller);

        using var call = _channel.CreateCallInvoker().AsyncServerStreamingCall(
            grpcMethod, host: null, new CallOptions(callMetadata, cancellationToken: ct), requestBytes);

        while (true)
        {
            bool moved;
            try
            {
                moved = await call.ResponseStream.MoveNext(ct);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
            {
                // A user who pressed Ctrl+C did not get an answer from the server.
                throw new OperationCanceledException(ct);
            }
            catch (RpcException ex)
            {
                throw GrpcFailure.FromRpcException(ex);
            }
            if (!moved) break;
            yield return ProtoJsonReader.ToJson(methodDescriptor.OutputType, call.ResponseStream.Current, indented: false, _descriptors.FindMessage);
        }
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Builds call metadata from a caller-supplied string pair list, shared by every call
    /// this class makes. A metadata key ending in "-bin" needs a binary value, which a string pair
    /// cannot supply, so that throws <see cref="ArgumentException"/> naming the key — the command
    /// layer turns that into a usage error, since it is a caller mistake rather than a call
    /// failure.</summary>
    private static Metadata BuildMetadata(IReadOnlyList<KeyValuePair<string, string>> metadata)
    {
        var callMetadata = new Metadata();
        foreach (var pair in metadata)
        {
            if (pair.Key.EndsWith("-bin", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Metadata key '{pair.Key}' ends with '-bin' and needs a binary value; a string metadata pair cannot supply one.",
                    nameof(metadata));
            }
            callMetadata.Add(pair.Key, pair.Value);
        }
        return callMetadata;
    }
}
