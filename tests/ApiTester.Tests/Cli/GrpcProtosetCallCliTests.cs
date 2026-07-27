using System.IO;
using System.Linq;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Core;
using ApiTester.Tests.Grpc;

namespace ApiTester.Tests.Cli;

/// <summary>e2e coverage for <c>certapi grpc call --protoset</c>, run entirely through
/// <see cref="CliApp.Run"/> against a real in-process <see cref="GrpcTestServer"/> started with
/// reflection disabled — a real <see cref="ApiTester.Grpc.GrpcCaller"/> and a real Kestrel/HTTP2
/// connection every time, never a mock of GrpcCaller or anything inside ApiTester.Grpc. Unlike
/// <see cref="GrpcProtosetCliTests"/> (which proves the offline `list` path never opens a socket),
/// every test here dials a real reflectionless server, because that is the whole point of `call`.</summary>
public class GrpcProtosetCallCliTests
{
    private static CliServices Services(string live) =>
        new() { LiveStatePath = live, IsGuiRunning = () => false, Client = new ApiClient(), Cancel = default };

    private static string TempLive() =>
        Path.Combine(Path.GetTempPath(), $"certapi-grpc-protoset-call-live-{Guid.NewGuid():N}.json");

    /// <summary>The whole feature in one pair: a reflectionless server refuses the call outright,
    /// then the identical call — plus --protoset — succeeds against that exact same server. If this
    /// passes, `certapi grpc call` no longer has a hard wall against a server that does not (or
    /// cannot, in production) enable reflection.</summary>
    [Fact]
    public async Task A_call_that_fails_without_a_protoset_succeeds_with_one_against_the_same_reflectionless_server()
    {
        await using var server = await GrpcTestServer.StartAsync(reflection: false);
        string live = TempLive();
        string protoset = DescriptorSetFixture.WriteTemp(DescriptorSetFixture.Complete());
        try
        {
            var stdoutWithout = new StringWriter();
            var stderrWithout = new StringWriter();
            int codeWithout = CliApp.Run(
                new[] { "grpc", "call", server.Address, "certapi.test.Echo/Unary", "-d", "{\"text\":\"hi\"}" },
                stdoutWithout, stderrWithout, services: Services(live));

            Assert.Equal(3, codeWithout);
            Assert.Contains("reflection", stderrWithout.ToString(), StringComparison.OrdinalIgnoreCase);
            // A user who hits this wall is exactly the user this release serves: the error must name
            // the way through (--protoset), not just the problem, or it strands them right here.
            Assert.Contains("--protoset", stderrWithout.ToString());

            var stdoutWith = new StringWriter();
            var stderrWith = new StringWriter();
            int codeWith = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/Unary", "-d", "{\"text\":\"hi\"}",
                    "--protoset", protoset
                },
                stdoutWith, stderrWith, services: Services(live));

            Assert.Equal(0, codeWith);
            using var doc = JsonDocument.Parse(stdoutWith.ToString());
            Assert.Equal("hi", doc.RootElement.GetProperty("text").GetString());
        }
        finally
        {
            File.Delete(protoset);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    /// <summary>Ruling 6: a protoset-sourced descriptor must behave identically to a
    /// reflection-sourced one for well-known-type rendering, because that rendering keys off the
    /// descriptor's full type name and not off where the descriptor came from. Mirrors
    /// GrpcCliTests.Call_unary_round_trips_a_well_known_timestamp_and_duration_as_the_same_canonical_strings
    /// but against a reflection-disabled server, with the descriptors supplied via --protoset.</summary>
    [Fact]
    public async Task Call_with_a_protoset_round_trips_a_well_known_timestamp_and_duration_as_the_same_canonical_strings()
    {
        await using var server = await GrpcTestServer.StartAsync(reflection: false);
        string live = TempLive();
        string protoset = DescriptorSetFixture.WriteTemp(DescriptorSetFixture.Complete());
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/Unary",
                    "-d", """{"all":{"fTimestamp":"2023-11-14T22:13:20Z","fDuration":"1.500s"}}""",
                    "--protoset", protoset
                },
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            string output = stdout.ToString();
            Assert.Contains("\"2023-11-14T22:13:20Z\"", output);
            Assert.Contains("\"1.500s\"", output);
        }
        finally
        {
            File.Delete(protoset);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    /// <summary>Proves the CLI's generous short-name resolution ("Echo/Unary" for
    /// "certapi.test.Echo/Unary") runs against a descriptor set's listing exactly as it does against
    /// reflection's.</summary>
    [Fact]
    public async Task Call_with_a_protoset_resolves_a_short_service_name()
    {
        await using var server = await GrpcTestServer.StartAsync(reflection: false);
        string live = TempLive();
        string protoset = DescriptorSetFixture.WriteTemp(DescriptorSetFixture.Complete());
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "Echo/Unary", "-d", "{\"text\":\"hi\"}",
                    "--protoset", protoset
                },
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal("hi", doc.RootElement.GetProperty("text").GetString());
        }
        finally
        {
            File.Delete(protoset);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public async Task Call_with_a_protoset_that_does_not_declare_the_service_says_what_it_does_declare()
    {
        await using var server = await GrpcTestServer.StartAsync(reflection: false);
        string live = TempLive();
        string protoset = DescriptorSetFixture.WriteTemp(DescriptorSetFixture.Complete());
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "nope.NotThere/Unary", "--protoset", protoset
                },
                stdout, stderr, services: Services(live));

            Assert.Equal(3, code);
            string error = stderr.ToString();
            Assert.Contains("descriptor set", error);
            Assert.Contains("certapi.test.Echo", error);
            AssertNoStackTraceReachedTheUser(error);
        }
        finally
        {
            File.Delete(protoset);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public async Task Call_with_a_missing_protoset_file_is_a_data_error_before_any_call()
    {
        await using var server = await GrpcTestServer.StartAsync(reflection: false);
        string live = TempLive();
        string missing = Path.Combine(Path.GetTempPath(), $"certapi-protoset-call-missing-{Guid.NewGuid():N}.protoset");
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/Unary", "--protoset", missing
                },
                stdout, stderr, services: Services(live));

            Assert.Equal(3, code);
            string error = stderr.ToString();
            Assert.Contains(missing, error);
            AssertNoStackTraceReachedTheUser(error);
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    /// <summary>Keyed off the server's own message count (it stops writing once the client stops
    /// reading), never a clock — ruling 10.</summary>
    [Fact]
    public async Task A_server_streaming_call_with_a_protoset_stops_after_max_messages()
    {
        await using var server = await GrpcTestServer.StartAsync(reflection: false);
        string live = TempLive();
        string protoset = DescriptorSetFixture.WriteTemp(DescriptorSetFixture.Complete());
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/ServerStream",
                    "-d", "{\"text\":\"m\",\"count\":5}", "--max-messages", "2", "--protoset", protoset
                },
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            var lines = stdout.ToString()
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            Assert.Equal(2, lines.Count);
            foreach (var line in lines) JsonDocument.Parse(line).Dispose();
        }
        finally
        {
            File.Delete(protoset);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    /// <summary>Ruling 7 through the client-streaming kind: the same pair as this file's headline
    /// unary test, but for a method whose input is a stream of messages. Against a reflection-
    /// disabled server, the identical command line fails without --protoset and succeeds with it —
    /// and the reply's "count" (the number of messages the server actually received) confirms all
    /// three -d values reached it, not just that the call returned OK.</summary>
    [Fact]
    public async Task A_client_streaming_call_that_fails_without_a_protoset_succeeds_with_one()
    {
        await using var server = await GrpcTestServer.StartAsync(reflection: false);
        string live = TempLive();
        string protoset = DescriptorSetFixture.WriteTemp(DescriptorSetFixture.Complete());
        try
        {
            var stdoutWithout = new StringWriter();
            var stderrWithout = new StringWriter();
            int codeWithout = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/ClientStream",
                    "-d", "{\"text\":\"a\"}", "-d", "{\"text\":\"b\"}", "-d", "{\"text\":\"c\"}"
                },
                stdoutWithout, stderrWithout, services: Services(live));

            Assert.Equal(3, codeWithout);
            Assert.Contains("reflection", stderrWithout.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--protoset", stderrWithout.ToString());

            var stdoutWith = new StringWriter();
            var stderrWith = new StringWriter();
            int codeWith = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/ClientStream",
                    "-d", "{\"text\":\"a\"}", "-d", "{\"text\":\"b\"}", "-d", "{\"text\":\"c\"}",
                    "--protoset", protoset
                },
                stdoutWith, stderrWith, services: Services(live));

            Assert.Equal(0, codeWith);
            using var doc = JsonDocument.Parse(stdoutWith.ToString());
            Assert.Equal(3, doc.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            File.Delete(protoset);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    /// <summary>Ruling 7 through the bidirectional kind: a descriptor set drives a duplex call
    /// exactly as reflection does — one reply per request, in order, printed as it arrives — against
    /// a server with no reflection service mounted at all.</summary>
    [Fact]
    public async Task A_bidirectional_call_with_a_protoset_prints_one_line_per_response()
    {
        await using var server = await GrpcTestServer.StartAsync(reflection: false);
        string live = TempLive();
        string protoset = DescriptorSetFixture.WriteTemp(DescriptorSetFixture.Complete());
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/BidiStream",
                    "-d", "{\"text\":\"first\"}", "-d", "{\"text\":\"second\"}", "--protoset", protoset
                },
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            var lines = stdout.ToString()
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            Assert.Equal(2, lines.Count);
            using var first = JsonDocument.Parse(lines[0]);
            using var second = JsonDocument.Parse(lines[1]);
            Assert.Equal("first", first.RootElement.GetProperty("text").GetString());
            Assert.Equal("second", second.RootElement.GetProperty("text").GetString());
        }
        finally
        {
            File.Delete(protoset);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    /// <summary>Ruling 8 through the new kind AND the descriptor-set source at once: well-known-type
    /// rendering keys off the descriptor's full type name, never off where the descriptor came from
    /// (reflection vs. --protoset) or which method kind carried the message (unary vs. duplex). A
    /// plain nested message with the same field shape would render as {"seconds":...,"nanos":...};
    /// only a Timestamp/Duration whose descriptor is recognized renders as these canonical strings,
    /// so this pins that recognition all the way through a bidirectional call sourced from a
    /// descriptor set.</summary>
    [Fact]
    public async Task A_bidirectional_call_with_a_protoset_round_trips_the_well_known_types_as_canonical_strings()
    {
        await using var server = await GrpcTestServer.StartAsync(reflection: false);
        string live = TempLive();
        string protoset = DescriptorSetFixture.WriteTemp(DescriptorSetFixture.Complete());
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/BidiStream",
                    "-d", """{"all":{"fTimestamp":"2023-11-14T22:13:20Z","fDuration":"1.500s"}}""",
                    "--protoset", protoset
                },
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            var lines = stdout.ToString()
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            Assert.Single(lines);
            Assert.Contains("\"2023-11-14T22:13:20Z\"", lines[0]);
            Assert.Contains("\"1.500s\"", lines[0]);
        }
        finally
        {
            File.Delete(protoset);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    /// <summary>Private copy of GrpcCliTests'/GrpcProtosetCliTests' helper of the same name —
    /// deliberately duplicated rather than reaching into another test class, per the brief.</summary>
    private static void AssertNoStackTraceReachedTheUser(string stderr)
    {
        Assert.DoesNotContain("   at ", stderr);
        Assert.DoesNotContain("GrpcDescriptorSetException", stderr);
    }
}
