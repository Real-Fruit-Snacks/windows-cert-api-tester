using System.IO;
using System.Linq;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Core;
using ApiTester.Tests.Grpc;

namespace ApiTester.Tests.Cli;

/// <summary>Ruling 3's proof, end to end through the real <see cref="CliApp.Run"/> entry point: not
/// one test in this class starts a <c>GrpcTestServer</c> — that absence of any listener anywhere in
/// the process IS the assertion that `grpc list --protoset` never opens a socket. (Contrast
/// <see cref="GrpcCliTests"/>, every one of whose `list`/`call` tests starts a real server.)</summary>
public class GrpcProtosetCliTests
{
    private static CliServices Services(string live) =>
        new() { LiveStatePath = live, IsGuiRunning = () => false, Client = new ApiClient(), Cancel = default };

    private static string TempLive() =>
        Path.Combine(Path.GetTempPath(), $"certapi-grpc-protoset-live-{Guid.NewGuid():N}.json");

    [Fact]
    public void List_with_a_protoset_and_no_address_needs_no_server_at_all()
    {
        // No GrpcTestServer anywhere in this test — the missing server IS the proof of ruling 3.
        string live = TempLive();
        string protoset = DescriptorSetFixture.WriteTemp(DescriptorSetFixture.Complete());
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "list", "--protoset", protoset }, stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            string output = stdout.ToString();
            Assert.Contains("certapi.test.Echo", output);
            Assert.Contains("Unary", output);
            Assert.Contains("stream", output);
        }
        finally
        {
            File.Delete(protoset);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public void List_with_a_protoset_and_json_marks_the_server_streaming_method()
    {
        string live = TempLive();
        string protoset = DescriptorSetFixture.WriteTemp(DescriptorSetFixture.Complete());
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "list", "--protoset", protoset, "--json" }, stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout.ToString());
            var echo = doc.RootElement.EnumerateArray()
                .Single(e => e.GetProperty("service").GetString() == "certapi.test.Echo");
            var serverStream = echo.GetProperty("methods").EnumerateArray()
                .Single(m => m.GetProperty("name").GetString() == "ServerStream");

            Assert.True(serverStream.GetProperty("serverStreaming").GetBoolean());
            Assert.False(serverStream.GetProperty("clientStreaming").GetBoolean());
        }
        finally
        {
            File.Delete(protoset);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public void List_with_a_protoset_ignores_an_address_and_still_opens_no_connection()
    {
        // Nothing listens on port 9 (the discard port) — a connection attempt would fail or hang.
        // Exit 0 here is only possible because --protoset wins and the address is never dialed.
        string live = TempLive();
        string protoset = DescriptorSetFixture.WriteTemp(DescriptorSetFixture.Complete());
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "list", "http://127.0.0.1:9", "--protoset", protoset },
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            Assert.Contains("certapi.test.Echo", stdout.ToString());
        }
        finally
        {
            File.Delete(protoset);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public void A_missing_protoset_file_is_a_data_error()
    {
        string live = TempLive();
        string missing = Path.Combine(Path.GetTempPath(), $"certapi-protoset-missing-{Guid.NewGuid():N}.protoset");
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "list", "--protoset", missing }, stdout, stderr, services: Services(live));

            Assert.Equal(3, code);
            Assert.Contains(missing, stderr.ToString());
            AssertNoStackTraceReachedTheUser(stderr.ToString());
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public void A_proto_source_file_passed_as_a_protoset_says_exactly_that()
    {
        string live = TempLive();
        string path = DescriptorSetFixture.WriteTempText(DescriptorSetFixture.ProtoSource);
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "list", "--protoset", path }, stdout, stderr, services: Services(live));

            Assert.Equal(3, code);
            Assert.Contains("looks like a .proto source file", stderr.ToString());
            AssertNoStackTraceReachedTheUser(stderr.ToString());
        }
        finally
        {
            File.Delete(path);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public void A_protoset_missing_an_import_tells_the_user_to_use_include_imports()
    {
        string live = TempLive();
        string path = DescriptorSetFixture.WriteTemp(
            DescriptorSetFixture.MissingImport("google/protobuf/timestamp.proto"));
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "list", "--protoset", path }, stdout, stderr, services: Services(live));

            Assert.Equal(3, code);
            Assert.Contains("--include_imports", stderr.ToString());
            AssertNoStackTraceReachedTheUser(stderr.ToString());
        }
        finally
        {
            File.Delete(path);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public void Help_documents_protoset_and_how_to_produce_one()
    {
        string help = ApiTester.Cli.Commands.GrpcCommand.Help;

        Assert.Contains("--protoset", help);
        Assert.Contains("--descriptor_set_out", help);
        Assert.Contains("--include_imports", help);
        Assert.DoesNotContain("no way to supply a compiled descriptor set", help);
    }

    /// <summary>Private copy of GrpcCliTests' helper of the same name — deliberately duplicated
    /// rather than reaching into that class, per the brief.</summary>
    private static void AssertNoStackTraceReachedTheUser(string stderr)
    {
        Assert.DoesNotContain("   at ", stderr);
        Assert.DoesNotContain("GrpcDescriptorSetException", stderr);
    }
}
