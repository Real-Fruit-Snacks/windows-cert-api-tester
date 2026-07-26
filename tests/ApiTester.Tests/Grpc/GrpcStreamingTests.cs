using System.Text.Json;
using ApiTester.Core;
using ApiTester.Grpc;

namespace ApiTester.Tests.Grpc;

/// <summary>Server-streaming coverage for <see cref="GrpcCaller.InvokeStreamingAsync"/>: several
/// messages arriving in order, compact (single-line) JSON per message, a consumer stopping early
/// without leaking the call, a non-OK status surfacing after the partial stream, and the same
/// unsupported-method guards <see cref="GrpcCaller.InvokeAsync"/> enforces. Also covers the D4
/// exit-3 path (a server with no reflection service), the other real-server behavior this slice's
/// test-server addition (<see cref="GrpcTestServer.StartAsync"/>'s <c>reflection</c> parameter)
/// unlocks. Real harness, real GrpcCaller — no mocks.</summary>
public class GrpcStreamingTests
{
    private static GrpcCaller Connect(GrpcTestServer server) =>
        new(server.Uri, clientCertificate: null, new TransportOptions());

    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoMetadata =
        Array.Empty<KeyValuePair<string, string>>();

    [Fact]
    public async Task Several_messages_arrive_in_order()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var messages = new List<string>();
        await foreach (var message in caller.InvokeStreamingAsync(
            "certapi.test.Echo", "ServerStream", """{"text":"m","count":4}""", NoMetadata, CancellationToken.None))
        {
            messages.Add(message);
        }

        Assert.Equal(4, messages.Count);
        for (int i = 0; i < 4; i++)
        {
            using var document = JsonDocument.Parse(messages[i]);
            // proto3 omits a field holding its type's default, so messageIndex == 0 (the first
            // message) is simply absent rather than present-and-zero.
            int messageIndex = document.RootElement.TryGetProperty("messageIndex", out var value) ? value.GetInt32() : 0;
            Assert.Equal(i, messageIndex);
            Assert.Equal($"m-{i}", document.RootElement.GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task Each_yielded_message_is_single_line_compact_json()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        await foreach (var message in caller.InvokeStreamingAsync(
            "certapi.test.Echo", "ServerStream", """{"text":"m","count":2}""", NoMetadata, CancellationToken.None))
        {
            Assert.DoesNotContain('\n', message);
        }
    }

    [Fact]
    public async Task Stopping_enumeration_early_cancels_the_call_without_leaking_it()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        int received = 0;
        await foreach (var _ in caller.InvokeStreamingAsync(
            "certapi.test.Echo", "ServerStream", """{"text":"m","count":50}""", NoMetadata, CancellationToken.None))
        {
            received++;
            break;
        }

        Assert.Equal(1, received);

        // The abandoned stream must not have poisoned the channel: the same caller can still make a
        // successful unary call afterwards.
        var result = await caller.InvokeAsync(
            "certapi.test.Echo", "Unary", """{"text":"still-alive"}""", NoMetadata, CancellationToken.None);
        Assert.Equal(0, result.StatusCode);
        using var document = JsonDocument.Parse(result.ResponseJson);
        Assert.Equal("still-alive", document.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_stream_that_ends_with_a_non_ok_status_yields_what_arrived_then_throws()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var messages = new List<string>();
        var ex = await Assert.ThrowsAsync<GrpcStatusException>(async () =>
        {
            await foreach (var message in caller.InvokeStreamingAsync(
                "certapi.test.Echo", "StreamThenFail", """{"text":"m"}""", NoMetadata, CancellationToken.None))
            {
                messages.Add(message);
            }
        });

        Assert.Equal(2, messages.Count);
        Assert.Equal(8, ex.StatusCode);
        Assert.Equal("ResourceExhausted", ex.StatusName);
        Assert.Contains("stream cut short", ex.StatusDetail);
    }

    [Fact]
    public async Task A_unary_method_is_rejected_by_the_streaming_call()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var enumerable = caller.InvokeStreamingAsync(
            "certapi.test.Echo", "Unary", "{}", NoMetadata, CancellationToken.None);

        // An async iterator's body does not run until enumeration begins.
        await Assert.ThrowsAsync<GrpcUnsupportedMethodException>(async () =>
        {
            await foreach (var _ in enumerable) { }
        });
    }

    [Fact]
    public async Task A_client_streaming_method_is_rejected_by_the_streaming_call()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var enumerable = caller.InvokeStreamingAsync(
            "certapi.test.Echo", "ClientStream", "{}", NoMetadata, CancellationToken.None);

        await Assert.ThrowsAsync<GrpcUnsupportedMethodException>(async () =>
        {
            await foreach (var _ in enumerable) { }
        });
    }

    [Fact]
    public async Task Metadata_sent_by_the_client_reaches_the_server_on_a_streaming_call()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var metadata = new[] { new KeyValuePair<string, string>("x-test-key", "streamed") };

        string? first = null;
        await foreach (var message in caller.InvokeStreamingAsync(
            "certapi.test.Echo", "ServerStream", """{"text":"m","count":1}""", metadata, CancellationToken.None))
        {
            first = message;
            break;
        }

        Assert.NotNull(first);
        using var document = JsonDocument.Parse(first!);
        Assert.Equal("streamed", document.RootElement.GetProperty("observedMetadata").GetString());
    }

    [Fact]
    public async Task Discovery_against_a_server_with_no_reflection_service_throws_reflection_unavailable()
    {
        await using var server = await GrpcTestServer.StartAsync(reflection: false);
        await using var caller = Connect(server);

        await Assert.ThrowsAsync<GrpcReflectionUnavailableException>(() =>
            caller.DiscoverAsync(CancellationToken.None));
    }
}
