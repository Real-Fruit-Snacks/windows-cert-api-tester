using System.IO;
using System.Linq;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Core;
using ApiTester.Tests.Grpc;

namespace ApiTester.Tests.Cli;

/// <summary>e2e coverage for `certapi grpc call` against the two kinds added in this release —
/// client-streaming and bidirectional — run entirely through <see cref="CliApp.Run"/> against the
/// real in-process <see cref="GrpcTestServer"/> harness. Never a mock of GrpcCaller or anything
/// inside ApiTester.Grpc.</summary>
public class GrpcStreamingCliTests
{
    private static CliServices Services(string live) =>
        new() { LiveStatePath = live, IsGuiRunning = () => false, Client = new ApiClient(), Cancel = default };

    private static string TempLive() =>
        Path.Combine(Path.GetTempPath(), $"certapi-grpc-streaming-live-{Guid.NewGuid():N}.json");

    /// <summary>Ruling 2's whole point: malformed input is a data error, never a crash. Neither a raw
    /// .NET stack-frame line nor the internal exception type name may reach the user's stderr.</summary>
    private static void AssertNoStackTraceReachedTheUser(string stderr)
    {
        Assert.DoesNotContain("   at ", stderr);
        Assert.DoesNotContain("GrpcJsonException", stderr);
    }

    [Fact]
    public async Task Client_streaming_sends_every_repeated_data_value()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/ClientStream",
                    "-d", "{\"text\":\"a\"}", "-d", "{\"text\":\"b\"}", "-d", "{\"text\":\"c\"}"
                },
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(3, doc.RootElement.GetProperty("count").GetInt32());
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task Client_streaming_sends_each_line_read_from_stdin()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "call", server.Address, "certapi.test.Echo/ClientStream" },
                new StringReader("{\"text\":\"a\"}\n{\"text\":\"b\"}\n"),
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task Data_values_are_sent_before_stdin_lines()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/ClientStream",
                    "-d", "{\"text\":\"from-flag\"}"
                },
                new StringReader("{\"text\":\"line1\"}\n{\"text\":\"line2\"}\n"),
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(3, doc.RootElement.GetProperty("count").GetInt32());
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task A_whitespace_only_line_on_stdin_is_not_sent_as_a_message()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "call", server.Address, "certapi.test.Echo/ClientStream" },
                new StringReader("{\"text\":\"line1\"}\n   \n{\"text\":\"line2\"}\n"),
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task A_client_streaming_non_ok_status_is_exit_1()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/ClientStreamThenFail",
                    "-d", "{\"text\":\"a\"}", "-d", "{\"text\":\"b\"}"
                },
                stdout, stderr, services: Services(live));

            Assert.Equal(1, code);
            string error = stderr.ToString();
            Assert.Contains("PermissionDenied", error);
            Assert.Contains("client stream refused", error);
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task Json_on_a_client_streaming_call_carries_the_response()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/ClientStream",
                    "-d", "{\"text\":\"a\"}", "--json"
                },
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(0, doc.RootElement.GetProperty("status").GetInt32());
            Assert.True(doc.RootElement.TryGetProperty("message", out _));
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task Several_data_values_against_a_unary_method_is_a_usage_error_naming_the_kind()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/Unary",
                    "-d", "{\"text\":\"a\"}", "-d", "{\"text\":\"b\"}"
                },
                stdout, stderr, services: Services(live));

            Assert.Equal(2, code);
            string error = stderr.ToString();
            Assert.Contains("certapi.test.Echo/Unary", error);
            Assert.Contains("unary", error);
            AssertNoStackTraceReachedTheUser(error);
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task Several_data_values_against_a_server_streaming_method_is_a_usage_error_naming_the_kind()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/ServerStream",
                    "-d", "{\"text\":\"a\"}", "-d", "{\"text\":\"b\"}"
                },
                stdout, stderr, services: Services(live));

            Assert.Equal(2, code);
            Assert.Contains("server-streaming", stderr.ToString());
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task Stdin_is_ignored_by_a_unary_call()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/Unary",
                    "-d", "{\"text\":\"flag\"}"
                },
                new StringReader("{\"text\":\"stdin1\"}\n{\"text\":\"stdin2\"}\n"),
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal("flag", doc.RootElement.GetProperty("text").GetString());
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task A_bidirectional_call_prints_one_compact_json_object_per_line()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/BidiStream",
                    "-d", "{\"text\":\"first\"}", "-d", "{\"text\":\"second\"}"
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
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task A_bidirectional_call_sends_stdin_lines_too()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "call", server.Address, "certapi.test.Echo/BidiStream" },
                new StringReader("{\"text\":\"one\"}\n{\"text\":\"two\"}\n"),
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            var lines = stdout.ToString()
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            Assert.Equal(2, lines.Count);
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task Max_messages_stops_a_bidirectional_call_early()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/BidiStream",
                    "-d", "{\"text\":\"a\"}", "-d", "{\"text\":\"b\"}", "-d", "{\"text\":\"c\"}",
                    "--max-messages", "1"
                },
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            var lines = stdout.ToString()
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            Assert.Single(lines);
            Assert.Contains("stopped after 1 message", stderr.ToString());
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task Json_on_a_bidirectional_call_collects_a_messages_array()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/BidiStream",
                    "-d", "{\"text\":\"a\"}", "-d", "{\"text\":\"b\"}", "--json"
                },
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(0, doc.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(2, doc.RootElement.GetProperty("messages").GetArrayLength());
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task A_bidirectional_non_ok_status_is_exit_1_with_the_partial_output_intact()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/BidiStreamThenFail",
                    "-d", "{\"text\":\"a\"}"
                },
                stdout, stderr, services: Services(live));

            Assert.Equal(1, code);
            var lines = stdout.ToString()
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
            Assert.Single(lines);
            string error = stderr.ToString();
            Assert.Contains("ResourceExhausted", error);
            Assert.Contains("duplex cut short", error);
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task Json_on_a_failing_bidirectional_call_carries_status_and_the_partial_message()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/BidiStreamThenFail",
                    "-d", "{\"text\":\"a\"}", "--json"
                },
                stdout, stderr, services: Services(live));

            Assert.Equal(1, code);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(8, doc.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("messages").GetArrayLength());
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task A_malformed_message_in_a_stream_is_a_clean_data_error_naming_the_field()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/BidiStream",
                    "-d", "{\"nope\":1}"
                },
                stdout, stderr, services: Services(live));

            Assert.Equal(3, code);
            string error = stderr.ToString();
            Assert.Contains("nope", error);
            AssertNoStackTraceReachedTheUser(error);
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public void Help_no_longer_claims_the_new_kinds_are_out_of_scope()
    {
        Assert.DoesNotContain("out of scope", GrpcCommand.Help);
        Assert.Contains("client-streaming", GrpcCommand.Help);
        Assert.Contains("bidirectional", GrpcCommand.Help);
    }
}
