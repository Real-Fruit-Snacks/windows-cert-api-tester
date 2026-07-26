using System.IO;
using System.Text.Json;
using ApiTester.Core;
using ApiTester.Grpc;

namespace ApiTester.Tests.Grpc;

/// <summary>Ruling 2, proven at the library level: a <see cref="GrpcDescriptorSet"/> handed to
/// <see cref="GrpcCaller"/> is the sole source of descriptors — server reflection is never consulted,
/// which is what makes a server that does not implement reflection at all callable. This file pairs
/// the "before" (reflection-disabled server, no descriptor set, fails) with the "after" (same server,
/// with a descriptor set, succeeds) exactly as ruling 7 asks: the CLI-level half of the same pair is a
/// later slice. Every test here drives a real in-process Kestrel/HTTP2 <see cref="GrpcTestServer"/> and
/// the real <see cref="GrpcCaller"/> — no mock, no stand-in for anything inside ApiTester.Grpc.</summary>
public class GrpcProtosetCallerTests
{
    private static GrpcCaller Connect(GrpcTestServer server, GrpcDescriptorSet? descriptors = null) =>
        new(server.Uri, clientCertificate: null, new TransportOptions(), trustServerCertificate: null, descriptors);

    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoMetadata =
        Array.Empty<KeyValuePair<string, string>>();

    [Fact]
    public async Task Without_a_descriptor_set_a_server_that_has_no_reflection_cannot_be_discovered()
    {
        await using var server = await GrpcTestServer.StartAsync(reflection: false);
        await using var caller = Connect(server);

        var ex = await Assert.ThrowsAsync<GrpcReflectionUnavailableException>(() =>
            caller.DiscoverAsync(CancellationToken.None));

        Assert.Contains("reflection", ex.Message);
    }

    /// <summary>The whole feature, at the library level: a server with reflection turned off answers a
    /// real unary call once the caller supplies a descriptor set instead. This is the "after" half of
    /// the pair whose "before" half is
    /// <see cref="Without_a_descriptor_set_a_server_that_has_no_reflection_cannot_be_discovered"/> —
    /// same server, same call shape, the only difference is the descriptor set.</summary>
    [Fact]
    public async Task With_a_descriptor_set_the_same_server_answers_a_unary_call()
    {
        await using var server = await GrpcTestServer.StartAsync(reflection: false);
        string path = DescriptorSetFixture.WriteTemp(DescriptorSetFixture.Complete());
        try
        {
            var descriptors = GrpcDescriptorSet.Load(path);
            await using var caller = Connect(server, descriptors);

            var services = await caller.DiscoverAsync(CancellationToken.None);
            Assert.Single(services, s => s.Name == "certapi.test.Echo");

            var result = await caller.InvokeAsync(
                "certapi.test.Echo", "Unary", """{"text":"hello","count":2}""", NoMetadata, CancellationToken.None);

            Assert.Equal(0, result.StatusCode);
            using var document = JsonDocument.Parse(result.ResponseJson);
            Assert.Equal("hello", document.RootElement.GetProperty("text").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("count").GetInt32());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task A_descriptor_set_wins_over_server_reflection_when_both_are_available()
    {
        await using var server = await GrpcTestServer.StartAsync();

        await using (var reflectionCaller = Connect(server))
        {
            var reflected = await reflectionCaller.DiscoverAsync(CancellationToken.None);
            // A reflection-enabled server advertises the reflection service itself, in addition to
            // the Echo service under test — the very thing a descriptor set, below, does not declare.
            Assert.Contains(reflected, s => s.Name == "grpc.reflection.v1alpha.ServerReflection");
        }

        string path = DescriptorSetFixture.WriteTemp(DescriptorSetFixture.Complete());
        try
        {
            var descriptors = GrpcDescriptorSet.Load(path);
            await using var protosetCaller = Connect(server, descriptors);

            var fromSet = await protosetCaller.DiscoverAsync(CancellationToken.None);

            Assert.Contains(fromSet, s => s.Name == "certapi.test.Echo");
            // The proof that the set won rather than the server's view, or a merge of the two: the
            // reflection service the server undeniably advertises (asserted above, against the same
            // server) is absent here, because the descriptor set never declared it.
            Assert.DoesNotContain(fromSet, s => s.Name == "grpc.reflection.v1alpha.ServerReflection");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task A_service_the_descriptor_set_does_not_declare_names_what_it_does_declare()
    {
        await using var server = await GrpcTestServer.StartAsync(reflection: false);
        string path = DescriptorSetFixture.WriteTemp(DescriptorSetFixture.Complete());
        try
        {
            var descriptors = GrpcDescriptorSet.Load(path);
            await using var caller = Connect(server, descriptors);

            var ex = await Assert.ThrowsAsync<GrpcDescriptorSetException>(() =>
                caller.InvokeAsync("nope.NotThere", "Unary", "{}", NoMetadata, CancellationToken.None));

            Assert.Contains("certapi.test.Echo", ex.Message);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task A_server_streaming_call_works_through_a_descriptor_set_too()
    {
        await using var server = await GrpcTestServer.StartAsync(reflection: false);
        string path = DescriptorSetFixture.WriteTemp(DescriptorSetFixture.Complete());
        try
        {
            var descriptors = GrpcDescriptorSet.Load(path);
            await using var caller = Connect(server, descriptors);

            var messages = new List<string>();
            await foreach (var message in caller.InvokeStreamingAsync(
                "certapi.test.Echo", "ServerStream", """{"text":"m","count":3}""", NoMetadata, CancellationToken.None))
            {
                messages.Add(message);
            }

            Assert.Equal(3, messages.Count);
            foreach (var message in messages)
            {
                using var document = JsonDocument.Parse(message);
                Assert.StartsWith("m-", document.RootElement.GetProperty("text").GetString());
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
