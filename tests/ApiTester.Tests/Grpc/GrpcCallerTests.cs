using System.Text.Json;
using ApiTester.Core;
using ApiTester.Grpc;

namespace ApiTester.Tests.Grpc;

/// <summary>The tracer bullet for Phase D2 (slice D-2): server reflection discovers a service, its
/// descriptors are built at runtime, a JSON request is encoded to Protobuf, a real unary call goes out
/// over HTTP/2, and the response comes back as JSON — all against the real in-process Kestrel harness.
/// GrpcCaller's internal collaborators (ReflectionClient, DescriptorPool, the JSON/Protobuf converters)
/// are never substituted; only the real server and the public GrpcCaller surface are exercised.</summary>
public class GrpcCallerTests
{
    private static GrpcCaller Connect(GrpcTestServer server)
    {
        // No AppContext switch needed: on .NET 9, SocketsHttpHandler negotiates HTTP/2 over
        // plaintext (h2c) whenever the request pins HTTP/2 exactly, which GrpcChannel already does.
        // (Confirmed empirically for slice D-3: this repo's own .NET Core 3.x-era comment claiming
        // otherwise no longer holds — see the identical finding in GrpcTestServerTests.cs.)
        return new GrpcCaller(server.Uri, clientCertificate: null, new TransportOptions());
    }

    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoMetadata =
        Array.Empty<KeyValuePair<string, string>>();

    [Fact]
    public async Task Discovery_lists_the_echo_service_with_correct_method_streaming_flags()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var services = await caller.DiscoverAsync(CancellationToken.None);

        var echo = Assert.Single(services, s => s.Name == "certapi.test.Echo");

        var unary = Assert.Single(echo.Methods, m => m.Name == "Unary");
        Assert.False(unary.ClientStreaming);
        Assert.False(unary.ServerStreaming);
        Assert.Equal("certapi.test.EchoRequest", unary.InputType);
        Assert.Equal("certapi.test.EchoReply", unary.OutputType);

        var serverStream = Assert.Single(echo.Methods, m => m.Name == "ServerStream");
        Assert.False(serverStream.ClientStreaming);
        Assert.True(serverStream.ServerStreaming);

        var clientStream = Assert.Single(echo.Methods, m => m.Name == "ClientStream");
        Assert.True(clientStream.ClientStreaming);
        Assert.False(clientStream.ServerStreaming);
    }

    [Fact]
    public async Task Unary_call_round_trips_text_and_count_as_json()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var result = await caller.InvokeAsync(
            "certapi.test.Echo", "Unary", """{"text":"hello","count":2}""", NoMetadata, CancellationToken.None);

        Assert.Equal(0, result.StatusCode);
        Assert.Equal("OK", result.StatusName);
        using var document = JsonDocument.Parse(result.ResponseJson);
        Assert.Equal("hello", document.RootElement.GetProperty("text").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task An_enum_a_nested_message_and_a_repeated_string_survive_the_round_trip()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var result = await caller.InvokeAsync("certapi.test.Echo", "Unary",
            """{"color":"COLOR_GREEN","nested":{"label":"n","depth":3},"tags":["a","b"]}""",
            NoMetadata, CancellationToken.None);

        using var document = JsonDocument.Parse(result.ResponseJson);
        Assert.Equal("COLOR_GREEN", document.RootElement.GetProperty("color").GetString());
        var nested = document.RootElement.GetProperty("nested");
        Assert.Equal("n", nested.GetProperty("label").GetString());
        Assert.Equal(3, nested.GetProperty("depth").GetInt32());
        var tags = document.RootElement.GetProperty("tags").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "a", "b" }, tags);
    }

    [Fact]
    public async Task An_any_field_is_expanded_using_the_real_descriptor_pool_resolving_the_type_url_from_reflection()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        // Proves (G2)'s production resolver actually reaches the wire: DescriptorPool.FindMessage must
        // resolve "certapi.test.Nested" from the descriptors server reflection fetched for this call,
        // with no test-local stand-in resolver involved anywhere in this path.
        var result = await caller.InvokeAsync("certapi.test.Echo", "Unary",
            """{"all":{"fAny":{"@type":"type.googleapis.com/certapi.test.Nested","label":"packed","depth":3}}}""",
            NoMetadata, CancellationToken.None);

        using var document = JsonDocument.Parse(result.ResponseJson);
        var fAny = document.RootElement.GetProperty("all").GetProperty("fAny");
        Assert.Equal("type.googleapis.com/certapi.test.Nested", fAny.GetProperty("@type").GetString());
        Assert.Equal("packed", fAny.GetProperty("label").GetString());
        Assert.Equal(3, fAny.GetProperty("depth").GetInt32());
    }

    [Fact]
    public async Task Metadata_sent_by_the_client_is_observed_by_the_server()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var result = await caller.InvokeAsync("certapi.test.Echo", "Unary", """{"text":"x"}""",
            new[] { new KeyValuePair<string, string>("x-test-key", "hello") }, CancellationToken.None);

        using var document = JsonDocument.Parse(result.ResponseJson);
        Assert.Equal("hello", document.RootElement.GetProperty("observedMetadata").GetString());
    }

    [Fact]
    public async Task Trailers_sent_by_the_server_are_returned_with_the_result()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var result = await caller.InvokeAsync(
            "certapi.test.Echo", "Unary", """{"text":"x"}""", NoMetadata, CancellationToken.None);

        Assert.Contains(result.Trailers, kv => kv.Key == "x-test-trailer" && kv.Value == "trailed");
    }

    [Fact]
    public async Task A_non_ok_status_is_returned_as_a_result_rather_than_thrown()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var result = await caller.InvokeAsync(
            "certapi.test.Echo", "Failing", "{}", NoMetadata, CancellationToken.None);

        Assert.Equal(7, result.StatusCode);
        Assert.Equal("PermissionDenied", result.StatusName);
        Assert.Contains("test denied", result.StatusDetail);
        Assert.Equal("", result.ResponseJson);
        Assert.True(result.Elapsed > TimeSpan.Zero);
    }

    [Fact]
    public async Task A_mistyped_method_name_names_the_methods_that_do_exist()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var ex = await Assert.ThrowsAsync<GrpcMethodNotFoundException>(() =>
            caller.InvokeAsync("certapi.test.Echo", "Unarry", "{}", NoMetadata, CancellationToken.None));

        Assert.Contains("Unary", ex.Message);
    }

    [Fact]
    public async Task A_client_streaming_method_is_rejected_as_unsupported()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        await Assert.ThrowsAsync<GrpcUnsupportedMethodException>(() =>
            caller.InvokeAsync("certapi.test.Echo", "ClientStream", "{}", NoMetadata, CancellationToken.None));
    }

    [Fact]
    public async Task An_unknown_request_field_names_the_offending_field()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = Connect(server);

        var ex = await Assert.ThrowsAsync<GrpcJsonException>(() =>
            caller.InvokeAsync("certapi.test.Echo", "Unary", """{"nope":"x"}""", NoMetadata, CancellationToken.None));

        Assert.Contains("nope", ex.Message);
    }

    [Theory]
    [InlineData("unix:///tmp/x")]
    [InlineData("ftp://host")]
    public void A_non_http_or_https_address_fails_at_construction(string address)
    {
        Assert.Throws<ArgumentException>(() =>
            new GrpcCaller(new Uri(address), clientCertificate: null, new TransportOptions()));
    }
}
