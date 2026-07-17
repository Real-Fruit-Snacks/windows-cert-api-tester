using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Cli.Mcp;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class McpCommandTests
{
    private static (X509Certificate2 ca, X509Certificate2 server, X509Certificate2 client) Certs()
    {
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        var server = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        var client = SelfSignedCertificateFactory.CreateSignedCertificate("McpClient", ca, false, true);
        return (ca, server, client);
    }

    private static ToolDef Tool(IReadOnlyList<ToolDef> tools, string name) => tools.First(t => t.Name == name);

    private static JsonElement Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Send_request_uses_the_pinned_cert_against_an_allowed_host()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartAsync(server, client.Thumbprint!, "{\"ok\":true}");
            var host = new Uri(upstream.BaseUrl).Host;   // 127.0.0.1
            var tools = McpCommand.BuildTools(client, new HostAllowlist(new[] { host }),
                insecure: true, timeout: 30, includeLocalMachine: false, workspace: null, new CliServices());

            var result = Tool(tools, "send_request").Handler(Args($"{{\"method\":\"GET\",\"url\":\"{upstream.BaseUrl}\"}}"));
            Assert.False(result.IsError);
            using var doc = JsonDocument.Parse(result.Json);
            Assert.Equal(200, doc.RootElement.GetProperty("status").GetInt32());
            Assert.True(doc.RootElement.GetProperty("clientCertPresented").GetBoolean());
        }
    }

    [Fact]
    public void Send_request_refuses_a_host_off_the_allowlist_before_connecting()
    {
        var tools = McpCommand.BuildTools(null, new HostAllowlist(new[] { "internal.corp" }),
            insecure: false, timeout: 5, includeLocalMachine: false, workspace: null, new CliServices());
        var result = Tool(tools, "send_request").Handler(Args("{\"url\":\"https://evil.com/x\"}"));
        Assert.True(result.IsError);
        Assert.Contains("not allowed", result.Json);
    }

    [Fact]
    public void List_certificates_returns_the_store()
    {
        var services = new CliServices
        {
            ListCertificates = _ => new[]
            {
                new CertificateInfo { Subject = "CN=A", Issuer = "CN=CA", Thumbprint = "AA",
                    NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                    HasClientAuthEku = true, Certificate = null! }
            }
        };
        var tools = McpCommand.BuildTools(null, new HostAllowlist(Array.Empty<string>()), false, 5, false, null, services);
        var result = Tool(tools, "list_certificates").Handler(Args("{}"));
        Assert.False(result.IsError);
        Assert.Contains("CN=A", result.Json);
    }

    [Fact]
    public void Self_test_passes()
    {
        var tools = McpCommand.BuildTools(null, new HostAllowlist(Array.Empty<string>()), false, 5, false, null, new CliServices());
        var result = Tool(tools, "self_test").Handler(Args("{}"));
        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Json);
        Assert.True(doc.RootElement.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public void Missing_store_value_is_a_usage_error()
    {
        int code = CliApp.Run(new[] { "mcp", "--store", "Nope" }, TextReader.Null, new StringWriter(), TextWriter.Null, services: new CliServices());
        Assert.Equal(2, code);
    }
}
