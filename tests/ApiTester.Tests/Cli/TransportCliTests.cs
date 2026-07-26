using System.IO;
using System.Text;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class TransportCliTests
{
    // Never the developer's real %AppData%\CertApiTester\state.json: certapi send always reads the
    // live state (for auto-token reuse), so every command-level test gets its own temp path.
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    private static TransportOptions Apply(params string[] tokens) =>
        TransportFlags.Parse(new Args(tokens), out _).ApplyTo(new TransportOptions());

    [Fact]
    public void Proxy_and_no_proxy_reach_the_transport_options()
    {
        var explicitProxy = Apply("https://x", "--proxy", "http://proxy.corp:8080");
        Assert.Equal(ProxyMode.Explicit, explicitProxy.Proxy);
        Assert.Equal("http://proxy.corp:8080", explicitProxy.ProxyUrl);

        var bypassed = Apply("https://x", "--no-proxy");
        Assert.Equal(ProxyMode.None, bypassed.Proxy);
    }

    [Fact]
    public void Proxy_credentials_split_on_the_first_colon_so_a_password_may_contain_one()
    {
        var options = Apply("https://x", "--proxy", "http://p:8080", "--proxy-user", "corp\\ada:p@ss:w0rd");

        Assert.Equal("corp\\ada", options.ProxyUser);
        Assert.Equal("p@ss:w0rd", options.ProxyPassword);
    }

    [Theory]
    [InlineData("--proxy", "http://p:8080", "--no-proxy")]           // two answers to the same question
    [InlineData("--proxy-user", "ada:secret")]                       // no proxy to authenticate to
    [InlineData("--proxy", "http://p:8080", "--proxy-user", "ada")]  // no colon, so no password
    [InlineData("--max-redirs", "0")]
    [InlineData("--max-redirs", "abc")]
    [InlineData("--http1.1", "--http2")]
    [InlineData("--resolve", "example.com:443")]                     // only two parts
    public void A_malformed_transport_flag_is_a_usage_error(params string[] tokens)
    {
        Assert.Throws<CliUsageException>(() => TransportFlags.Parse(new Args(tokens), out _));
    }

    [Fact]
    public void Resolve_pins_are_read_host_port_address_and_keep_an_IPv6_address_whole()
    {
        var options = Apply(
            "https://x",
            "--resolve", "api.example.com:443:10.0.0.7",
            "--resolve", "api.example.com:8443:2001:db8::1");

        Assert.Collection(options.Resolve,
            first =>
            {
                Assert.Equal("api.example.com", first.Host);
                Assert.Equal(443, first.Port);
                Assert.Equal("10.0.0.7", first.Address);
            },
            second =>
            {
                Assert.Equal(8443, second.Port);
                Assert.Equal("2001:db8::1", second.Address);
            });
    }

    [Fact]
    public void Only_the_settings_named_on_the_command_line_override_the_baseline()
    {
        // This is the property `run` depends on: a saved request's own transport choices survive
        // unless a flag on the command line names that same setting.
        var saved = new TransportOptions
        {
            Proxy = ProxyMode.Explicit,
            ProxyUrl = "http://saved.corp:3128",
            ProxyUser = "ada",
            FollowRedirects = false,
            MaxRedirects = 3,
            Decompress = false,
            Version = HttpVersionMode.Http2,
            IgnoreServerCertificateErrors = true,
            Resolve = new[] { new ResolveOverride("api.example.com", 443, "10.0.0.7") }
        };

        var options = TransportFlags.Parse(new Args(new[] { "--max-redirs", "7" }), out _).ApplyTo(saved);

        Assert.Equal(7, options.MaxRedirects);
        Assert.Equal(ProxyMode.Explicit, options.Proxy);
        Assert.Equal("http://saved.corp:3128", options.ProxyUrl);
        Assert.Equal("ada", options.ProxyUser);
        Assert.False(options.FollowRedirects);
        Assert.False(options.Decompress);
        Assert.Equal(HttpVersionMode.Http2, options.Version);
        Assert.True(options.IgnoreServerCertificateErrors);
        Assert.Equal("10.0.0.7", Assert.Single(options.Resolve).Address);
    }

    [Fact]
    public void The_redirect_chain_names_each_hop_and_the_two_facts_a_client_certificate_user_needs()
    {
        var hops = new[]
        {
            new RedirectHop(302, "https://a.example/", "https://b.example/", true, false),
            new RedirectHop(301, "https://b.example/", "http://b.example/", false, true),
            new RedirectHop(307, "http://b.example/", "http://c.example/", true, true)
        };

        Assert.Equal(
            "  302 https://a.example/ -> https://b.example/  (authorization dropped)\n" +
            "  301 https://b.example/ -> http://b.example/  (scheme downgrade)\n" +
            "  307 http://b.example/ -> http://c.example/  (authorization dropped)  (scheme downgrade)",
            OutputText.RedirectLines(hops));

        Assert.Equal("", OutputText.RedirectLines(Array.Empty<RedirectHop>()));
    }

    [Fact]
    public void The_json_envelope_reports_the_error_kind_message_and_exception_chain()
    {
        var response = new ApiResponse
        {
            Elapsed = TimeSpan.FromMilliseconds(12),
            Error = new ApiError(
                ApiErrorKind.ConnectionRefused,
                "No connection could be made because the target machine actively refused it.",
                new[]
                {
                    new ErrorDetail("System.Net.Http.HttpRequestException", "connection refused", null),
                    new ErrorDetail("System.Net.Sockets.SocketException", "actively refused", 10061)
                },
                System.Net.Sockets.SocketError.ConnectionRefused)
        };

        using var doc = JsonDocument.Parse(SendCommand.BuildEnvelope(response, includeBody: true));

        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("ConnectionRefused", error.GetProperty("kind").GetString());
        Assert.Contains("actively refused", error.GetProperty("message").GetString());
        Assert.Equal("ConnectionRefused", error.GetProperty("socketError").GetString());
        var detail = error.GetProperty("detail");
        Assert.Equal(2, detail.GetArrayLength());
        Assert.Equal("System.Net.Http.HttpRequestException", detail[0].GetProperty("exceptionType").GetString());
        Assert.Equal("connection refused", detail[0].GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, detail[0].GetProperty("nativeErrorCode").ValueKind);
        Assert.Equal(10061, detail[1].GetProperty("nativeErrorCode").GetInt32());

        // Nothing was followed, so the envelope stays exactly as it has always been for callers
        // that never see a redirect.
        Assert.False(doc.RootElement.TryGetProperty("redirects", out _));
    }

    [Fact]
    public void The_json_envelope_lists_the_redirect_chain_when_hops_were_followed()
    {
        var response = new ApiResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Body = Encoding.UTF8.GetBytes("hi"),
            Redirects = new[]
            {
                new RedirectHop(302, "https://a.example/", "https://b.example/", true, false),
                new RedirectHop(301, "https://b.example/", "http://b.example/", false, true)
            }
        };

        using var doc = JsonDocument.Parse(SendCommand.BuildEnvelope(response, includeBody: true));

        var hops = doc.RootElement.GetProperty("redirects");
        Assert.Equal(2, hops.GetArrayLength());
        Assert.Equal(302, hops[0].GetProperty("status").GetInt32());
        Assert.Equal("https://a.example/", hops[0].GetProperty("from").GetString());
        Assert.Equal("https://b.example/", hops[0].GetProperty("to").GetString());
        Assert.True(hops[0].GetProperty("authorizationDropped").GetBoolean());
        Assert.False(hops[0].GetProperty("schemeDowngrade").GetBoolean());
        Assert.True(hops[1].GetProperty("schemeDowngrade").GetBoolean());
    }

    [Fact]
    public void Pinning_an_address_while_routing_through_a_proxy_is_refused_before_anything_is_sent()
    {
        // The proxy port is deliberately unreachable: if the guard ever regresses this fails on the
        // exit code instead of hanging on a connection.
        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[]
            {
                "send", "https://example.invalid/",
                "--proxy", "http://127.0.0.1:9",
                "--resolve", "example.invalid:443:127.0.0.1"
            },
            new StringWriter(), stderr, new MemoryStream(), new CliServices { LiveStatePath = TempState() });

        Assert.Equal(2, code);
        Assert.Contains("--resolve", stderr.ToString());
    }

    [Theory]
    [InlineData("--json")]
    [InlineData("--capture", "token=access_token")]
    [InlineData("--assert", "status == 200")]
    public void All_ips_refuses_the_options_that_describe_a_single_response(params string[] extra)
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[] { "send", "https://example.invalid/", "--all-ips" }.Concat(extra).ToArray(),
            new StringWriter(), stderr, new MemoryStream(), new CliServices { LiveStatePath = TempState() });

        Assert.Equal(2, code);
        Assert.Contains("--all-ips", stderr.ToString());
    }

    [Fact]
    public async Task No_decompress_relays_the_bytes_the_server_framed_instead_of_the_decoded_body()
    {
        var decoded = await SendToGzipServerAsync();
        Assert.Equal(0, decoded.Code);
        Assert.Equal("{\"gzip\":\"ok\"}", Encoding.UTF8.GetString(decoded.Body));

        var relayed = await SendToGzipServerAsync("--no-decompress");
        Assert.Equal(0, relayed.Code);
        Assert.True(relayed.Body.Length >= 2, "expected the raw gzip stream, got an empty body");
        Assert.Equal(0x1F, relayed.Body[0]);        // gzip magic number
        Assert.Equal(0x8B, relayed.Body[1]);
    }

    /// <summary>certapi send against a loopback server that answers with a gzip-encoded body — the
    /// whole path from the command line into the handler, with a real TLS handshake.</summary>
    private static async Task<(int Code, string Err, byte[] Body)> SendToGzipServerAsync(params string[] extra)
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartGzipAsync(serverCert, clientCert.Thumbprint!);

        var services = new CliServices
        {
            LiveStatePath = TempState(),
            ListCertificates = _ => new[]
            {
                new CertificateInfo
                {
                    Subject = "CN=CliClient", Issuer = "CN=CA", Thumbprint = clientCert.Thumbprint!,
                    NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                    HasClientAuthEku = true, Certificate = clientCert
                }
            }
        };

        var stderr = new StringWriter();
        var body = new MemoryStream();
        var args = new[] { "send", server.BaseUrl, "--cert", "CliClient", "--insecure" }.Concat(extra).ToArray();
        int code = CliApp.Run(args, new StringWriter(), stderr, body, services);
        return (code, stderr.ToString(), body.ToArray());
    }
}
