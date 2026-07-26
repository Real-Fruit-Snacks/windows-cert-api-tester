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

    [Fact]
    public async Task Response_content_length_is_preserved()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartAsync(server, client.Thumbprint!, "abcde");
            using var gw = new MtlsGateway(new Uri(upstream.BaseUrl), client, ignoreServerCertificateErrors: true, TimeSpan.FromSeconds(30));

            var resp = await gw.ForwardAsync(
                new GatewayRequest("GET", "/", Array.Empty<KeyValuePair<string, string>>(), null, null), default);
            using (resp.Lifetime)
                Assert.Contains(resp.Headers, h =>
                    h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && h.Value == "5");
        }
    }

    [Theory]
    [InlineData("//evil.com/steal")]
    [InlineData("https://evil.com/steal")]
    [InlineData("http://evil.com/steal")]
    public async Task Off_host_request_targets_are_refused(string target)
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            // Upstream is a real loopback server; the off-host target must be rejected BEFORE any connect,
            // so the certificate never reaches another host.
            await using var upstream = await LoopbackMtlsServer.StartAsync(server, client.Thumbprint!, "{\"ok\":true}");
            using var gw = new MtlsGateway(new Uri(upstream.BaseUrl), client, ignoreServerCertificateErrors: true, TimeSpan.FromSeconds(30));

            await Assert.ThrowsAsync<GatewayTargetException>(async () =>
                await gw.ForwardAsync(new GatewayRequest("GET", target, Array.Empty<KeyValuePair<string, string>>(), null, null), default));
        }
    }

    [Fact]
    public async Task Normal_paths_still_forward()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartAsync(server, client.Thumbprint!, "{\"ok\":true}");
            using var gw = new MtlsGateway(new Uri(upstream.BaseUrl), client, ignoreServerCertificateErrors: true, TimeSpan.FromSeconds(30));

            var resp = await gw.ForwardAsync(
                new GatewayRequest("GET", "/api/x?q=1", Array.Empty<KeyValuePair<string, string>>(), null, null), default);
            using (resp.Lifetime) Assert.Equal(200, resp.StatusCode);
        }
    }

    [Fact]
    public async Task Request_matching_no_route_is_refused_without_contacting_an_upstream()
    {
        // The only route points at a dead port, so any attempt to forward would surface as a
        // connection failure instead — the route miss has to be decided before that.
        var routes = new GatewayRoutes(new[] { new GatewayRoute("/api", new Uri("https://127.0.0.1:1/")) });
        using var gw = new MtlsGateway(routes, clientCertificate: null,
                                       ignoreServerCertificateErrors: true, TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<GatewayRouteNotFoundException>(async () =>
            await gw.ForwardAsync(
                new GatewayRequest("GET", "/static/app.js", Array.Empty<KeyValuePair<string, string>>(), null, null),
                default));
    }

    [Fact]
    public async Task Each_mounted_prefix_reaches_its_own_upstream_with_the_prefix_stripped()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            // Two echo servers on their own ports: each replays the request line and headers it
            // received, so the Host header says which one was reached and the request line says
            // what path arrived there.
            await using var alpha = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var beta = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            var routes = new GatewayRoutes(new[]
            {
                new GatewayRoute("/alpha", new Uri(alpha.BaseUrl)),
                new GatewayRoute("/beta", new Uri(beta.BaseUrl))
            });
            using var gw = new MtlsGateway(routes, client, ignoreServerCertificateErrors: true, TimeSpan.FromSeconds(30));

            string toAlpha = await ReadBody(await gw.ForwardAsync(
                new GatewayRequest("GET", "/alpha/orders?x=1", Array.Empty<KeyValuePair<string, string>>(), null, null),
                default));
            string toBeta = await ReadBody(await gw.ForwardAsync(
                new GatewayRequest("GET", "/beta/items", Array.Empty<KeyValuePair<string, string>>(), null, null),
                default));

            Assert.Contains("GET /orders?x=1", toAlpha);
            Assert.Contains($"Host: 127.0.0.1:{new Uri(alpha.BaseUrl).Port}", toAlpha);
            Assert.Contains("GET /items", toBeta);
            Assert.Contains($"Host: 127.0.0.1:{new Uri(beta.BaseUrl).Port}", toBeta);
        }
    }

    [Fact]
    public async Task Off_host_targets_under_a_mounted_prefix_are_refused()
    {
        const string target = "/api//evil.com/steal";
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            // Stripping the mount point must not turn an off-host target into an accepted one: the
            // authority check is re-applied against the matched route's own upstream.
            await using var upstream = await LoopbackMtlsServer.StartAsync(server, client.Thumbprint!, "{\"ok\":true}");
            var routes = new GatewayRoutes(new[] { new GatewayRoute("/api", new Uri(upstream.BaseUrl)) });
            using var gw = new MtlsGateway(routes, client, ignoreServerCertificateErrors: true, TimeSpan.FromSeconds(30));

            await Assert.ThrowsAsync<GatewayTargetException>(async () =>
                await gw.ForwardAsync(
                    new GatewayRequest("GET", target, Array.Empty<KeyValuePair<string, string>>(), null, null),
                    default));
        }
    }

    [Fact]
    public async Task Resolve_override_dials_the_pinned_address_while_the_upstream_keeps_its_hostname()
    {
        // A .invalid name can never resolve (RFC 2606), so reaching the upstream at all proves the
        // override supplied the address; the certificate is issued for that same name, so the
        // handshake only completes if the TLS server name carried the original hostname too.
        const string hostname = "certapi-gateway-resolve.invalid";
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            hostname, ca, true, false, new[] { hostname });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var upstream = await LoopbackMtlsServer.StartEchoAsync(serverCert, clientCert.Thumbprint!);
        int port = new Uri(upstream.BaseUrl).Port;

        var routes = new GatewayRoutes(new[] { new GatewayRoute("/", new Uri($"https://{hostname}:{port}/")) });
        using var gw = new MtlsGateway(routes, clientCert, ignoreServerCertificateErrors: true,
                                       TimeSpan.FromSeconds(30),
                                       new TransportOptions
                                       {
                                           Resolve = new[] { new ResolveOverride(hostname, port, "127.0.0.1") }
                                       });

        string echoed = await ReadBody(await gw.ForwardAsync(
            new GatewayRequest("GET", "/orders", Array.Empty<KeyValuePair<string, string>>(), null, null), default));

        Assert.Contains("GET /orders", echoed);
        Assert.Contains($"Host: {hostname}:{port}", echoed);
        Assert.Equal(hostname, upstream.LastSniHost);
    }
}
