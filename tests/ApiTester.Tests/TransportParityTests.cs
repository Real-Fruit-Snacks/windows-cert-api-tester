using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Proves the v1.68.0 parity promise: every connection the product opens — SSE, WebSocket,
/// OAuth token fetch, and the gateway's upstream — reaches a host pinned with `certapi trust add`
/// with --insecure OFF, and still refuses an unpinned self-signed host. The pin predicate is the
/// same seam ApiClient uses, so these are the exact code paths the commands run.</summary>
public class TransportParityTests
{
    private static (X509Certificate2 ca, X509Certificate2 server, X509Certificate2 client) Certs()
    {
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        var server = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        var client = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        return (ca, server, client);
    }

    private static Func<X509Certificate2?, bool> PinFor(X509Certificate2 serverCert, string host)
    {
        var state = new AppState();
        TrustService.Trust(state, host, serverCert);
        return new TrustPredicates(state).For(host);
    }

    [Fact]
    public async Task Sse_reaches_a_pinned_selfsigned_host_without_insecure_and_refuses_an_unpinned_one()
    {
        var (ca, serverCert, clientCert) = Certs();
        using (ca) using (serverCert) using (clientCert)
        {
            await using var server = await LoopbackMtlsServer.StartSseAsync(
                serverCert, clientCert.Thumbprint!, new List<(string?, string)> { ("tick", "1") });
            var host = new Uri(server.BaseUrl).Host;

            var received = new List<SseEvent>();
            await foreach (var ev in SseClient.StreamAsync(server.BaseUrl, clientCert, null,
                               ignoreServerCertificateErrors: false,
                               trustServerCertificate: PinFor(serverCert, host)))
                received.Add(ev);
            Assert.Single(received);
        }
        // Unpinned control needs its own server: the pinned one has already closed above.
        var (ca2, serverCert2, clientCert2) = Certs();
        using (ca2) using (serverCert2) using (clientCert2)
        {
            await using var server = await LoopbackMtlsServer.StartSseAsync(
                serverCert2, clientCert2.Thumbprint!, new List<(string?, string)> { ("tick", "1") });
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
            {
                await foreach (var _ in SseClient.StreamAsync(server.BaseUrl, clientCert2, null,
                                   ignoreServerCertificateErrors: false)) { }
            });
        }
    }

    [Fact]
    public async Task WebSocket_handshake_honors_a_pin_without_insecure()
    {
        var (ca, serverCert, clientCert) = Certs();
        using (ca) using (serverCert) using (clientCert)
        {
            await using var server = await LoopbackMtlsServer.StartWebSocketEchoAsync(serverCert, clientCert.Thumbprint!);
            string wsUrl = server.WebSocketUrl;
            var host = new Uri(wsUrl).Host;

            await using var session = new WebSocketSession();
            await session.ConnectAsync(wsUrl, clientCert, null,
                ignoreServerCertificateErrors: false,
                trustServerCertificate: PinFor(serverCert, host));

            await session.SendTextAsync("ping");
            await foreach (var msg in session.ReceiveAllAsync())
            {
                Assert.True(msg.IsText);
                Assert.Equal("ping", msg.Text);
                break;
            }
        }
    }

    [Fact]
    public async Task Token_fetch_honors_a_pin_without_insecure()
    {
        var (ca, serverCert, clientCert) = Certs();
        using (ca) using (serverCert) using (clientCert)
        {
            await using var server = await LoopbackMtlsServer.StartOAuthTokenAsync(
                serverCert, clientCert.Thumbprint!, "cid", "shh");
            var host = new Uri(server.BaseUrl).Host;

            var result = await OAuthClient.RequestTokenAsync(
                new OAuthRequest { TokenEndpoint = server.BaseUrl, ClientId = "cid", ClientSecret = "shh" },
                clientCert, ignoreServerCertificateErrors: false,
                trustServerCertificate: PinFor(serverCert, host));

            Assert.True(result.Success, result.FailureMessage);
            Assert.NotNull(result.AccessToken);
        }
    }

    [Fact]
    public async Task Gateway_upstream_honors_a_pin_without_insecure()
    {
        var (ca, serverCert, clientCert) = Certs();
        using (ca) using (serverCert) using (clientCert)
        {
            await using var upstream = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");
            var upstreamUri = new Uri(upstream.BaseUrl);
            var state = new AppState();
            TrustService.Trust(state, upstreamUri.Host, serverCert);
            var predicates = new TrustPredicates(state);

            using var gw = new MtlsGateway(
                new GatewayRoutes(new[] { new GatewayRoute("/", upstreamUri) }),
                clientCert, ignoreServerCertificateErrors: false, TimeSpan.FromSeconds(30),
                transport: null, trustForHost: predicates.For);

            var resp = await gw.ForwardAsync(
                new GatewayRequest("GET", "/", new List<KeyValuePair<string, string>>(), null, null), default);

            Assert.Equal(200, resp.StatusCode);
        }
    }

    [Fact]
    public async Task Gateway_upstream_without_a_pin_or_insecure_still_refuses_a_selfsigned_host()
    {
        var (ca, serverCert, clientCert) = Certs();
        using (ca) using (serverCert) using (clientCert)
        {
            await using var upstream = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

            using var gw = new MtlsGateway(
                new GatewayRoutes(new[] { new GatewayRoute("/", new Uri(upstream.BaseUrl)) }),
                clientCert, ignoreServerCertificateErrors: false, TimeSpan.FromSeconds(30),
                transport: null, trustForHost: new TrustPredicates(new AppState()).For);

            // An empty pin list must not soften anything: the handshake still fails the way it
            // always did for an unknown self-signed certificate.
            await Assert.ThrowsAsync<HttpRequestException>(async () => await gw.ForwardAsync(
                new GatewayRequest("GET", "/", new List<KeyValuePair<string, string>>(), null, null), default));
        }
    }
}
