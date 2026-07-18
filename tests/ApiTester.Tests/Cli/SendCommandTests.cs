using System.IO;
using System.Text;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class SendCommandTests
{
    // certapi send always reads the live state now (for auto-token reuse), so tests must point
    // it at a per-test temp path — otherwise they'd read (and a corrupt copy could poison
    // exit-code assertions with) the developer's real %AppData%\CertApiTester\state.json.
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    private static async Task<(int Code, string Out, string Err, byte[] Body)> RunAsync(
        string[] args, string responseBody = "{\"ok\":true}")
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, responseBody);

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

        var so = new StringWriter();
        var se = new StringWriter();
        var body = new MemoryStream();
        var full = args.Select(a => a.Replace("{URL}", server.BaseUrl)).ToArray();
        int code = CliApp.Run(full, so, se, body, services);
        return (code, so.ToString(), se.ToString(), body.ToArray());
    }

    [Fact]
    public async Task Sends_with_client_cert_and_writes_body_to_stdout()
    {
        var r = await RunAsync(new[] { "send", "{URL}", "--cert", "CliClient", "--insecure" });
        Assert.Equal(0, r.Code);
        Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(r.Body));
        Assert.Contains("200", r.Err);           // metadata line on stderr
        Assert.DoesNotContain("200", r.Out);     // stdout text stream stays clean
    }

    [Fact]
    public async Task Include_prints_headers_and_json_builds_an_envelope()
    {
        var inc = await RunAsync(new[] { "send", "{URL}", "--cert", "CliClient", "--insecure", "--include" });
        Assert.Contains("200", inc.Out);         // status line + headers as text

        var js = await RunAsync(new[] { "send", "{URL}", "--cert", "CliClient", "--insecure", "--json" });
        using var doc = System.Text.Json.JsonDocument.Parse(js.Out);
        Assert.Equal(200, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Contains("ok", doc.RootElement.GetProperty("body").GetString());
        Assert.True(doc.RootElement.GetProperty("clientCertPresented").GetBoolean());
    }

    [Fact]
    public async Task Vars_resolve_and_missing_url_is_usage()
    {
        var r = await RunAsync(new[] { "send", "{URL}?q={{missing}}", "--cert", "CliClient", "--insecure" });
        Assert.Equal(0, r.Code);
        Assert.Contains("missing", r.Err);       // unresolved-token warning

        var so = new StringWriter(); var se = new StringWriter();
        Assert.Equal(2, CliApp.Run(new[] { "send" }, so, se, new MemoryStream(), new CliServices { LiveStatePath = TempState() }));
    }

    [Fact]
    public void Transport_errors_exit_1()
    {
        // The loopback server always answers 200, so --fail's status branch can't be hit here;
        // the transport-error path is what matters: an unreachable port is a network failure -> 1.
        var so = new StringWriter(); var se = new StringWriter();
        int code = CliApp.Run(
            new[] { "send", "https://127.0.0.1:1/", "--timeout", "2" },
            so, se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });
        Assert.Equal(1, code);
        Assert.Contains("error", se.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Envelope_groups_duplicate_response_headers()
    {
        var response = new ApiResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Headers = new[]
            {
                new KeyValuePair<string, string>("Set-Cookie", "a=1"),
                new KeyValuePair<string, string>("Set-Cookie", "b=2"),
                new KeyValuePair<string, string>("Content-Type", "text/plain")
            },
            Body = Encoding.UTF8.GetBytes("hi")
        };

        var json = SendCommand.BuildEnvelope(response, includeBody: true);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var headers = doc.RootElement.GetProperty("headers");
        Assert.Equal(2, headers.GetProperty("Set-Cookie").GetArrayLength());
        Assert.Equal("text/plain", headers.GetProperty("Content-Type").GetString());
        Assert.Equal("hi", doc.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public void Json_with_output_file_does_not_write_on_transport_error()
    {
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-err-{Guid.NewGuid():N}.bin");
        try
        {
            int code = CliApp.Run(
                new[] { "send", "https://127.0.0.1:1/", "--timeout", "2", "--json", "-o", outFile },
                new StringWriter(), new StringWriter(), new MemoryStream(), new CliServices { LiveStatePath = TempState() });
            Assert.Equal(1, code);
            Assert.False(File.Exists(outFile));
        }
        finally { if (File.Exists(outFile)) File.Delete(outFile); }
    }

    [Fact]
    public void Invalid_timeout_is_a_usage_error()
    {
        foreach (var bad in new[] { "abc", "0", "-5" })
        {
            var se = new StringWriter();
            int code = CliApp.Run(new[] { "send", "https://h/", "--timeout", bad },
                                  new StringWriter(), se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });
            Assert.Equal(2, code);
            Assert.Contains("--timeout", se.ToString());
        }
    }

    [Fact]
    public void Envelope_merges_mixed_case_duplicate_headers()
    {
        var response = new ApiResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Headers = new[]
            {
                new KeyValuePair<string, string>("Set-Cookie", "a=1"),
                new KeyValuePair<string, string>("set-cookie", "b=2")
            },
            Body = Encoding.UTF8.GetBytes("hi")
        };

        var json = ApiTester.Cli.Commands.SendCommand.BuildEnvelope(response, includeBody: false);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var headers = doc.RootElement.GetProperty("headers");
        Assert.Single(headers.EnumerateObject());
        Assert.Equal(2, headers.GetProperty("Set-Cookie").GetArrayLength());
    }

    [Fact]
    public void Unexpected_exceptions_become_a_clean_error_and_exit_1()
    {
        var se = new StringWriter();
        int code = CliApp.Run(new[] { "send", "https://h/", "-X", "BAD METHOD" },
                              new StringWriter(), se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });
        Assert.Equal(1, code);
        Assert.Contains("error:", se.ToString());
        Assert.DoesNotContain("   at ", se.ToString());
    }
}
