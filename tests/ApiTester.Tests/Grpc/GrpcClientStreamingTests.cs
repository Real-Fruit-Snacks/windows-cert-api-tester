using System.Text.Json;
using ApiTester.Core;
using ApiTester.Grpc;

namespace ApiTester.Tests.Grpc;

/// <summary>Client-streaming coverage for <see cref="GrpcCaller.InvokeClientStreamingAsync"/>: every
/// sent message reaching the server, an empty stream being legal, well-known types travelling through
/// a streamed message, a non-OK status coming back as data rather than an exception, a malformed
/// message surfacing as <see cref="GrpcJsonException"/>, and the same unsupported-method guards the
/// other entry points enforce. Real harness, real GrpcCaller — no mocks.</summary>
public class GrpcClientStreamingTests
{
    private static GrpcCaller Connect(GrpcTestServer server) =>
        new(server.Uri, clientCertificate: null, new TransportOptions());

    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoMetadata =
        Array.Empty<KeyValuePair<string, string>>();

    private static async IAsyncEnumerable<string> Messages(params string[] messages)
    {
        foreach (var message in messages)
        {
            // A genuinely asynchronous source, so the caller is never quietly relying on a
            // synchronous one.
            await Task.Yield();
            yield return message;
        }
    }

    [Fact]
    public async Task Every_message_reaches_the_server()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var result = await caller.InvokeClientStreamingAsync(
            "certapi.test.Echo", "ClientStream",
            Messages("""{"text":"a"}""", """{"text":"b"}""", """{"text":"c"}"""),
            NoMetadata, CancellationToken.None);

        Assert.Equal(0, result.StatusCode);
        using var document = JsonDocument.Parse(result.ResponseJson);
        Assert.Equal(3, document.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task A_stream_with_no_messages_at_all_is_legal()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var result = await caller.InvokeClientStreamingAsync(
            "certapi.test.Echo", "ClientStream",
            Messages(), NoMetadata, CancellationToken.None);

        Assert.Equal(0, result.StatusCode);
        using var document = JsonDocument.Parse(result.ResponseJson);
        // proto3 omits a field holding its type's default, so count == 0 is simply absent rather
        // than present-and-zero.
        int count = document.RootElement.TryGetProperty("count", out var value) ? value.GetInt32() : 0;
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task A_canonical_well_known_timestamp_is_accepted_inside_a_streamed_message()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var result = await caller.InvokeClientStreamingAsync(
            "certapi.test.Echo", "ClientStream",
            Messages("""{"all":{"fTimestamp":"2023-11-14T22:13:20Z"}}"""),
            NoMetadata, CancellationToken.None);

        Assert.Equal(0, result.StatusCode);
        using var document = JsonDocument.Parse(result.ResponseJson);
        Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task A_non_ok_status_comes_back_as_data_not_as_an_exception()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var result = await caller.InvokeClientStreamingAsync(
            "certapi.test.Echo", "ClientStreamThenFail",
            Messages("""{"text":"a"}""", """{"text":"b"}"""),
            NoMetadata, CancellationToken.None);

        Assert.Equal(7, result.StatusCode);
        Assert.Equal("PermissionDenied", result.StatusName);
        Assert.Contains("client stream refused", result.StatusDetail);
        Assert.Equal("", result.ResponseJson);
        Assert.Contains(result.Trailers, pair => pair.Key == "x-test-trailer" && pair.Value == "trailed");
    }

    [Fact]
    public async Task A_malformed_message_is_a_json_error_naming_the_field()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var ex = await Assert.ThrowsAsync<GrpcJsonException>(() => caller.InvokeClientStreamingAsync(
            "certapi.test.Echo", "ClientStream",
            Messages("""{"nope":"x"}"""),
            NoMetadata, CancellationToken.None));

        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task A_unary_method_is_rejected_by_the_client_streaming_call()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        await Assert.ThrowsAsync<GrpcUnsupportedMethodException>(() => caller.InvokeClientStreamingAsync(
            "certapi.test.Echo", "Unary", Messages(), NoMetadata, CancellationToken.None));
    }

    [Fact]
    public async Task A_bidirectional_method_is_rejected_by_the_client_streaming_call()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        await Assert.ThrowsAsync<GrpcUnsupportedMethodException>(() => caller.InvokeClientStreamingAsync(
            "certapi.test.Echo", "BidiStream", Messages(), NoMetadata, CancellationToken.None));
    }

    [Fact]
    public async Task The_channel_survives_a_failed_client_streaming_call()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var failed = await caller.InvokeClientStreamingAsync(
            "certapi.test.Echo", "ClientStreamThenFail",
            Messages("""{"text":"a"}"""), NoMetadata, CancellationToken.None);
        Assert.Equal(7, failed.StatusCode);

        // The failed stream must not have poisoned the channel: the same caller can still make a
        // successful unary call afterwards.
        var result = await caller.InvokeAsync(
            "certapi.test.Echo", "Unary", """{"text":"still-alive"}""", NoMetadata, CancellationToken.None);
        Assert.Equal(0, result.StatusCode);
        using var document = JsonDocument.Parse(result.ResponseJson);
        Assert.Equal("still-alive", document.RootElement.GetProperty("text").GetString());
    }
}
