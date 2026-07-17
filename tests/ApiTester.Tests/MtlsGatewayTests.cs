using System.IO;
using System.Net.Http;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

public class MtlsGatewayTests
{
    private static (System.Security.Cryptography.X509Certificates.X509Certificate2 ca,
                    System.Security.Cryptography.X509Certificates.X509Certificate2 server,
                    System.Security.Cryptography.X509Certificates.X509Certificate2 client) Certs()
    {
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        var server = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        var client = SelfSignedCertificateFactory.CreateSignedCertificate("GatewayClient", ca, false, true);
        return (ca, server, client);
    }

    private static async Task<string> ReadBody(GatewayResponse r)
    {
        using (r.Lifetime)
        using (var sr = new StreamReader(r.Body))
            return await sr.ReadToEndAsync();
    }

    [Fact]
    public void HopByHop_matches_the_spec_set_case_insensitively()
    {
        foreach (var h in new[] { "Connection", "keep-alive", "Transfer-Encoding", "upgrade", "Host", "Content-Length" })
            Assert.True(HopByHop.Is(h), h);
        Assert.False(HopByHop.Is("Authorization"));
        Assert.False(HopByHop.Is("X-Custom"));
    }

    [Fact]
    public async Task Forwards_get_with_client_certificate_and_returns_body()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartAsync(server, client.Thumbprint!, "{\"ok\":true}");
            using var gw = new MtlsGateway(new Uri(upstream.BaseUrl), client, ignoreServerCertificateErrors: true, TimeSpan.FromSeconds(30));

            var resp = await gw.ForwardAsync(
                new GatewayRequest("GET", "/", Array.Empty<KeyValuePair<string, string>>(), null, null), default);

            Assert.Equal(200, resp.StatusCode);
            Assert.Contains("ok", await ReadBody(resp));
        }
    }

    [Fact]
    public async Task Forwards_post_body_and_end_to_end_headers_but_not_hop_by_hop()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            // Loopback server echoes the request line + headers + body into its response so we can assert what arrived.
            await using var upstream = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            using var gw = new MtlsGateway(new Uri(upstream.BaseUrl), client, ignoreServerCertificateErrors: true, TimeSpan.FromSeconds(30));

            var headers = new[]
            {
                new KeyValuePair<string, string>("X-Trace", "abc"),
                new KeyValuePair<string, string>("Connection", "keep-alive")   // hop-by-hop, must be dropped
            };
            var body = new MemoryStream(Encoding.UTF8.GetBytes("hello-body"));
            var resp = await gw.ForwardAsync(new GatewayRequest("POST", "/submit?x=1", headers, body, "text/plain"), default);

            string echoed = await ReadBody(resp);
            Assert.Contains("POST /submit?x=1", echoed);
            Assert.Contains("X-Trace: abc", echoed);
            Assert.Contains("hello-body", echoed);
            Assert.DoesNotContain("Connection: keep-alive", echoed);
        }
    }

    [Fact]
    public async Task Does_not_follow_redirects()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartRedirectAsync(server, client.Thumbprint!, "/moved");
            using var gw = new MtlsGateway(new Uri(upstream.BaseUrl), client, ignoreServerCertificateErrors: true, TimeSpan.FromSeconds(30));

            var resp = await gw.ForwardAsync(
                new GatewayRequest("GET", "/", Array.Empty<KeyValuePair<string, string>>(), null, null), default);

            using (resp.Lifetime)
            {
                Assert.Equal(302, resp.StatusCode);
                Assert.Contains(resp.Headers, h => h.Key.Equals("Location", StringComparison.OrdinalIgnoreCase) && h.Value == "/moved");
            }
        }
    }

    [Fact]
    public async Task Unreachable_upstream_throws()
    {
        using var gw = new MtlsGateway(new Uri("https://127.0.0.1:1/"), clientCertificate: null,
                                       ignoreServerCertificateErrors: true, TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await gw.ForwardAsync(new GatewayRequest("GET", "/", Array.Empty<KeyValuePair<string, string>>(), null, null), default));
    }
}
