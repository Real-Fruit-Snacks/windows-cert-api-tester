using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Core;
using ApiTester.Tests.Grpc;

namespace ApiTester.Tests.Cli;

/// <summary>e2e coverage for <c>certapi grpc list|call</c>, run entirely through
/// <see cref="CliApp.Run"/> against the real in-process <see cref="GrpcTestServer"/> harness — a
/// real <see cref="GrpcCaller"/> and real Kestrel/HTTP2 connection every time, never a mock of
/// GrpcCaller or anything inside ApiTester.Grpc.</summary>
public class GrpcCliTests
{
    private static CliServices Services(string live) =>
        new() { LiveStatePath = live, IsGuiRunning = () => false, Client = new ApiClient(), Cancel = default };

    private static string TempLive() =>
        Path.Combine(Path.GetTempPath(), $"certapi-grpc-live-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task List_names_the_echo_service_and_marks_a_streaming_method()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(new[] { "grpc", "list", server.Address }, stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            string output = stdout.ToString();
            Assert.Contains("certapi.test.Echo", output);
            Assert.Contains("Unary", output);
            Assert.Contains("stream", output);
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task List_json_marks_the_server_streaming_method_correctly()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "list", server.Address, "--json" }, stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout.ToString());
            var echo = doc.RootElement.EnumerateArray()
                .Single(e => e.GetProperty("service").GetString() == "certapi.test.Echo");
            var serverStream = echo.GetProperty("methods").EnumerateArray()
                .Single(m => m.GetProperty("name").GetString() == "ServerStream");

            Assert.True(serverStream.GetProperty("serverStreaming").GetBoolean());
            Assert.False(serverStream.GetProperty("clientStreaming").GetBoolean());
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task Call_unary_round_trips_the_request_as_json()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "call", server.Address, "certapi.test.Echo/Unary", "-d", "{\"text\":\"hi\",\"count\":2}" },
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal("hi", doc.RootElement.GetProperty("text").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task Call_resolves_a_short_service_name()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "call", server.Address, "Echo/Unary", "-d", "{\"text\":\"hi\",\"count\":2}" },
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal("hi", doc.RootElement.GetProperty("text").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task Call_server_streaming_stops_after_max_messages()
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
                    "-d", "{\"text\":\"m\",\"count\":5}", "--max-messages", "2"
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
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task Call_a_failing_method_exits_1_and_reports_the_status()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "call", server.Address, "certapi.test.Echo/Failing" },
                stdout, stderr, services: Services(live));

            Assert.Equal(1, code);
            Assert.Contains("PermissionDenied", stderr.ToString());
            Assert.Contains("test denied", stderr.ToString());
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task Call_a_failing_method_with_json_carries_status_and_detail()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "call", server.Address, "certapi.test.Echo/Failing", "--json" },
                stdout, stderr, services: Services(live));

            Assert.Equal(1, code);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(7, doc.RootElement.GetProperty("status").GetInt32());
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("detail").GetString()));
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task Call_with_an_unmatched_request_field_is_a_data_error_naming_the_field()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "call", server.Address, "certapi.test.Echo/Unary", "-d", "{\"nope\":1}" },
                stdout, stderr, services: Services(live));

            Assert.Equal(3, code);
            Assert.Contains("nope", stderr.ToString());
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task List_against_a_server_without_reflection_is_a_data_error()
    {
        await using var server = await GrpcTestServer.StartAsync(reflection: false);
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(new[] { "grpc", "list", server.Address }, stdout, stderr, services: Services(live));

            Assert.Equal(3, code);
            Assert.Contains("reflection", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public void A_non_http_or_https_address_is_a_usage_error()
    {
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(new[] { "grpc", "list", "ftp://host" }, stdout, stderr, services: Services(live));

            Assert.Equal(2, code);
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task A_service_slash_method_with_no_slash_is_a_usage_error()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "call", server.Address, "EchoUnary" }, stdout, stderr, services: Services(live));

            Assert.Equal(2, code);
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task Calling_a_client_streaming_method_is_a_usage_error()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "call", server.Address, "certapi.test.Echo/ClientStream" },
                stdout, stderr, services: Services(live));

            Assert.Equal(2, code);
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task Metadata_header_reaches_the_server()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "call", server.Address, "certapi.test.Echo/Unary", "-H", "x-test-key: hello" },
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal("hello", doc.RootElement.GetProperty("observedMetadata").GetString());
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public async Task Mutual_tls_presents_the_certificate_named_by_cert_file()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, true, false, new[] { "127.0.0.1" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("GrpcClient", ca, false, true);
        await using var server = await GrpcTestServer.StartTlsAsync(serverCert, requireClientCertificate: true);

        string live = TempLive();
        string pfx = Path.Combine(Path.GetTempPath(), $"certapi-grpc-client-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(pfx, clientCert.Export(X509ContentType.Pkcs12, "pw"));
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[]
                {
                    "grpc", "call", server.Address, "certapi.test.Echo/Unary", "-d", "{\"text\":\"mtls\"}",
                    "--cert-file", pfx, "--cert-password", "pw", "--insecure"
                },
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(
                clientCert.Thumbprint, doc.RootElement.GetProperty("observedClientCertThumbprint").GetString(),
                ignoreCase: true);
        }
        finally
        {
            File.Delete(pfx);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public async Task A_pinned_server_certificate_needs_no_insecure()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, true, false, new[] { "127.0.0.1" });
        await using var server = await GrpcTestServer.StartTlsAsync(serverCert, requireClientCertificate: false);

        string live = TempLive();
        string workspace = Path.Combine(Path.GetTempPath(), $"certapi-grpc-ws-{Guid.NewGuid():N}.json");
        var state = new AppState();
        TrustService.Trust(state, "127.0.0.1", serverCert);
        state.SaveTo(workspace);
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "call", server.Address, "certapi.test.Echo/Unary", "--workspace", workspace },
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
        }
        finally
        {
            if (File.Exists(workspace)) File.Delete(workspace);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    /// <summary>The negative control for the pinned-certificate test above: without the pin (and
    /// without --insecure), the identical call must fail rather than quietly succeeding — otherwise
    /// the positive test could pass even if certificate validation never ran at all.</summary>
    [Fact]
    public async Task Without_a_pin_or_insecure_the_same_call_fails()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, true, false, new[] { "127.0.0.1" });
        await using var server = await GrpcTestServer.StartTlsAsync(serverCert, requireClientCertificate: false);

        string live = TempLive();
        string workspace = Path.Combine(Path.GetTempPath(), $"certapi-grpc-ws-{Guid.NewGuid():N}.json");
        new AppState().SaveTo(workspace);
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "call", server.Address, "certapi.test.Echo/Unary", "--workspace", workspace },
                stdout, stderr, services: Services(live));

            Assert.Equal(1, code);
            Assert.NotEmpty(stderr.ToString());
        }
        finally
        {
            if (File.Exists(workspace)) File.Delete(workspace);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public async Task Quiet_suppresses_the_metadata_line_but_not_the_response()
    {
        await using var server = await GrpcTestServer.StartAsync();
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "call", server.Address, "certapi.test.Echo/Unary", "-d", "{\"text\":\"quiet\"}", "-q" },
                stdout, stderr, services: Services(live));

            Assert.Equal(0, code);
            Assert.Empty(stderr.ToString());
            Assert.Contains("quiet", stdout.ToString());
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public void Help_grpc_names_list_and_call_and_the_overview_mentions_grpc()
    {
        var stdout = new StringWriter();
        int code = CliApp.Run(new[] { "help", "grpc" }, stdout, new StringWriter());

        Assert.Equal(0, code);
        Assert.Contains("grpc list", stdout.ToString());
        Assert.Contains("grpc call", stdout.ToString());
        Assert.Contains("grpc", CliApp.Usage);
    }
}
