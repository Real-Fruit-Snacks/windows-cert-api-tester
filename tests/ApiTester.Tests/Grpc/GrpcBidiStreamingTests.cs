using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ApiTester.Core;
using ApiTester.Grpc;

namespace ApiTester.Tests.Grpc;

/// <summary>Bidirectional-streaming coverage for <see cref="GrpcCaller.InvokeDuplexAsync"/>: sending
/// never blocking receiving (the headline behavior a bidirectional call exists for), compact
/// (single-line) JSON per response, a non-OK status coming back as data after the messages that did
/// arrive, a sink stopping the call early without leaking it, metadata and well-known types
/// surviving the round trip, a malformed message surfacing as <see cref="GrpcJsonException"/>, an
/// empty message sequence being legal, and the same unsupported-method guards the other entry points
/// enforce. Real harness, real GrpcCaller — no mocks.</summary>
public class GrpcBidiStreamingTests
{
    private static GrpcCaller Connect(GrpcTestServer server) =>
        new(server.Uri, clientCertificate: null, new TransportOptions());

    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoMetadata =
        Array.Empty<KeyValuePair<string, string>>();

    private static async IAsyncEnumerable<string> Messages(
        string[] messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var message in messages)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return message;
        }
    }

    /// <summary>Yields "m0" immediately, then waits on <paramref name="gate"/> (completed by the
    /// response sink once it has seen the first reply) before yielding "m1" — the source half of
    /// the interleaving proof. Honors cancellation so an early-ending call never leaves this parked.</summary>
    private static async IAsyncEnumerable<string> GatedMessages(
        ConcurrentQueue<string> log, TaskCompletionSource gate,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        log.Enqueue("sent m0");
        yield return """{"text":"m0"}""";
        await gate.Task.WaitAsync(ct);
        log.Enqueue("sent m1");
        yield return """{"text":"m1"}""";
    }

    [Fact]
    public async Task Sending_does_not_block_receiving_so_a_response_arrives_before_the_next_message_is_sent()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var log = new ConcurrentQueue<string>();
        var gate = new TaskCompletionSource();
        int responseCount = 0;

        // A hang-guard, not a timing assertion (ruling 10): this bounds how long a defective
        // implementation is allowed to hang before the test fails, it never drives an assertion.
        using var hang = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        bool OnResponse(string json)
        {
            using var document = JsonDocument.Parse(json);
            string text = document.RootElement.GetProperty("text").GetString()!;
            log.Enqueue("recv " + text);
            responseCount++;
            if (responseCount == 1) gate.TrySetResult();
            return true;
        }

        var result = await caller.InvokeDuplexAsync(
            "certapi.test.Echo", "BidiStream",
            GatedMessages(log, gate, hang.Token),
            NoMetadata, OnResponse, hang.Token);

        Assert.Equal(0, result.StatusCode);
        // Only reachable if the implementation reads responses while the request source is still
        // producing: an implementation that sent everything first could never release the gate that
        // unblocks "sent m1", so the log could never end up in this order.
        Assert.Equal(new[] { "sent m0", "recv m0", "sent m1", "recv m1" }, log.ToArray());
    }

    [Fact]
    public async Task Each_response_is_single_line_compact_json()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);
        using var hang = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await caller.InvokeDuplexAsync(
            "certapi.test.Echo", "BidiStream",
            Messages(new[] { """{"text":"a"}""", """{"text":"b"}""" }),
            NoMetadata,
            json => { Assert.DoesNotContain('\n', json); return true; },
            hang.Token);

        Assert.Equal(0, result.StatusCode);
    }

    [Fact]
    public async Task A_non_ok_status_comes_back_as_data_after_the_messages_that_did_arrive()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);
        using var hang = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        int received = 0;
        var result = await caller.InvokeDuplexAsync(
            "certapi.test.Echo", "BidiStreamThenFail",
            Messages(new[] { """{"text":"a"}""" }),
            NoMetadata,
            _ => { received++; return true; },
            hang.Token);

        Assert.Equal(1, received);
        Assert.Equal(8, result.StatusCode);
        Assert.Equal("ResourceExhausted", result.StatusName);
        Assert.Contains("duplex cut short", result.StatusDetail);
        Assert.Equal("", result.ResponseJson);
    }

    [Fact]
    public async Task A_sink_that_stops_early_ends_the_call_without_leaking_it()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);
        using var hang = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        int received = 0;
        var result = await caller.InvokeDuplexAsync(
            "certapi.test.Echo", "BidiStream",
            Messages(new[] { """{"text":"a"}""", """{"text":"b"}""", """{"text":"c"}""" }),
            NoMetadata,
            _ => { received++; return false; },
            hang.Token);

        Assert.Equal(1, received);
        Assert.Equal(0, result.StatusCode);

        // The abandoned duplex call must not have poisoned the channel: the same caller can still
        // make a successful unary call afterwards.
        var unary = await caller.InvokeAsync(
            "certapi.test.Echo", "Unary", """{"text":"still-alive"}""", NoMetadata, CancellationToken.None);
        Assert.Equal(0, unary.StatusCode);
        using var document = JsonDocument.Parse(unary.ResponseJson);
        Assert.Equal("still-alive", document.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Metadata_sent_by_the_client_reaches_the_server()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);
        using var hang = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var metadata = new[] { new KeyValuePair<string, string>("x-test-key", "duplexed") };
        string? first = null;
        var result = await caller.InvokeDuplexAsync(
            "certapi.test.Echo", "BidiStream",
            Messages(new[] { """{"text":"a"}""" }),
            metadata,
            json => { first = json; return true; },
            hang.Token);

        Assert.Equal(0, result.StatusCode);
        Assert.NotNull(first);
        using var document = JsonDocument.Parse(first!);
        Assert.Equal("duplexed", document.RootElement.GetProperty("observedMetadata").GetString());
    }

    [Fact]
    public async Task Well_known_types_survive_a_bidirectional_round_trip()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);
        using var hang = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        string? first = null;
        var result = await caller.InvokeDuplexAsync(
            "certapi.test.Echo", "BidiStream",
            Messages(new[] { """{"all":{"fTimestamp":"2023-11-14T22:13:20Z","fDuration":"1.500s"}}""" }),
            NoMetadata,
            json => { first = json; return true; },
            hang.Token);

        Assert.Equal(0, result.StatusCode);
        Assert.NotNull(first);
        Assert.Contains("2023-11-14T22:13:20Z", first);
        Assert.Contains("1.500s", first);
    }

    [Fact]
    public async Task A_malformed_message_is_a_json_error_naming_the_field()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);
        using var hang = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var ex = await Assert.ThrowsAsync<GrpcJsonException>(() => caller.InvokeDuplexAsync(
            "certapi.test.Echo", "BidiStream",
            Messages(new[] { """{"nope":"x"}""" }),
            NoMetadata,
            _ => true,
            hang.Token));

        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task An_empty_message_sequence_is_legal()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);
        using var hang = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        bool called = false;
        var result = await caller.InvokeDuplexAsync(
            "certapi.test.Echo", "BidiStream",
            Messages(Array.Empty<string>()),
            NoMetadata,
            _ => { called = true; return true; },
            hang.Token);

        Assert.Equal(0, result.StatusCode);
        Assert.False(called);
    }

    [Theory]
    [InlineData("Unary")]
    [InlineData("ServerStream")]
    [InlineData("ClientStream")]
    public async Task A_non_bidirectional_method_is_rejected_by_the_duplex_call(string method)
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        await Assert.ThrowsAsync<GrpcUnsupportedMethodException>(() => caller.InvokeDuplexAsync(
            "certapi.test.Echo", method, Messages(Array.Empty<string>()), NoMetadata, _ => true, CancellationToken.None));
    }
}
