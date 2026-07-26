using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

public class GatewayWebSocketRelayTests
{
    /// <summary>Every wait is bounded so a relay that never answers fails this test rather than
    /// hanging the suite.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// <summary>Stands in for the gateway's own listener: accepts one browser WebSocket and hands it
    /// to the relay, which is how ServeCommand reaches this code. The relay's outcome — the
    /// exception it surfaced, or null — is exposed so a test can assert on it.</summary>
    private sealed class RelayHost : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<Exception?> _outcome =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _loop;

        public Uri Url { get; }

        /// <summary>The exception RelayAsync surfaced, or null when it returned normally — the
        /// failure report slice 4 logs from.</summary>
        public Task<Exception?> Outcome => _outcome.Task;

        private RelayHost(GatewayWebSocketRelay relay, Uri upstream, string? subProtocol)
        {
            int port = FreePort();
            Url = new Uri($"ws://127.0.0.1:{port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = ServeAsync(relay, upstream, subProtocol);
        }

        public static RelayHost Start(GatewayWebSocketRelay relay, Uri upstream, string? subProtocol = null) =>
            new(relay, upstream, subProtocol);

        private async Task ServeAsync(GatewayWebSocketRelay relay, Uri upstream, string? subProtocol)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                var accepted = await context.AcceptWebSocketAsync(subProtocol);
                try
                {
                    await relay.RelayAsync(accepted.WebSocket, upstream, subProtocol, _cts.Token);
                    _outcome.TrySetResult(null);
                }
                catch (Exception ex) { _outcome.TrySetResult(ex); }
                // The socket belongs to the context that accepted it, so disposing it here is the
                // owner's job being done — the relay never disposes what it was handed.
                finally { accepted.WebSocket.Dispose(); }
            }
            catch (Exception ex) { _outcome.TrySetResult(ex); }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Close();
            try { await _loop.WaitAsync(Limit); } catch { /* the test's assertions are the verdict */ }
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task A_text_message_round_trips_through_the_relay_as_text()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var upstream = await LoopbackMtlsServer.StartWebSocketEchoAsync(serverCert, clientCert.Thumbprint!);

        await using var host = RelayHost.Start(
            new GatewayWebSocketRelay(clientCert, ignoreServerCertificateErrors: true),
            new Uri(upstream.BaseUrl));

        using var cts = new CancellationTokenSource(Limit);
        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(host.Url, cts.Token);
        await browser.SendAsync(Encoding.UTF8.GetBytes("hello upstream"), WebSocketMessageType.Text, true, cts.Token);

        var buffer = new byte[8192];
        var received = await browser.ReceiveAsync(buffer, cts.Token);

        Assert.Equal(WebSocketMessageType.Text, received.MessageType);
        Assert.Equal("hello upstream", Encoding.UTF8.GetString(buffer, 0, received.Count));
    }

    [Fact]
    public async Task A_binary_message_round_trips_through_the_relay_as_binary()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var upstream = await LoopbackMtlsServer.StartWebSocketEchoAsync(serverCert, clientCert.Thumbprint!);

        await using var host = RelayHost.Start(
            new GatewayWebSocketRelay(clientCert, ignoreServerCertificateErrors: true),
            new Uri(upstream.BaseUrl));

        using var cts = new CancellationTokenSource(Limit);
        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(host.Url, cts.Token);

        var payload = new byte[] { 0x00, 0x01, 0xFE, 0xFF, 0x7F };
        await browser.SendAsync(payload, WebSocketMessageType.Binary, true, cts.Token);

        var buffer = new byte[8192];
        var received = await browser.ReceiveAsync(buffer, cts.Token);

        Assert.Equal(WebSocketMessageType.Binary, received.MessageType);
        Assert.Equal(payload, buffer[..received.Count]);
    }

    [Fact]
    public async Task A_close_status_and_description_travel_through_the_relay()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var upstream = await LoopbackMtlsServer.StartWebSocketEchoAsync(serverCert, clientCert.Thumbprint!);

        await using var host = RelayHost.Start(
            new GatewayWebSocketRelay(clientCert, ignoreServerCertificateErrors: true),
            new Uri(upstream.BaseUrl));

        using var cts = new CancellationTokenSource(Limit);
        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(host.Url, cts.Token);

        // The close goes to the upstream, which mirrors it; the relay carries both halves, so the
        // status and reason the browser gets back are the ones the upstream sent.
        await browser.CloseAsync(WebSocketCloseStatus.PolicyViolation, "the upstream said no", cts.Token);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, browser.CloseStatus);
        Assert.Equal("the upstream said no", browser.CloseStatusDescription);
    }

    /// <summary>Nothing is listening on a port that was just released, so the connect is refused
    /// immediately rather than timing out.</summary>
    private static Uri DeadUpstream() => new($"http://127.0.0.1:{FreePort()}/socket");

    [Fact]
    public async Task An_unreachable_upstream_closes_the_browser_with_an_internal_server_error()
    {
        await using var host = RelayHost.Start(
            new GatewayWebSocketRelay(null, ignoreServerCertificateErrors: false), DeadUpstream());

        using var cts = new CancellationTokenSource(Limit);
        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(host.Url, cts.Token);

        var received = await browser.ReceiveAsync(new byte[8192], cts.Token);

        Assert.Equal(WebSocketMessageType.Close, received.MessageType);
        Assert.Equal(WebSocketCloseStatus.InternalServerError, received.CloseStatus);
        Assert.False(string.IsNullOrWhiteSpace(received.CloseStatusDescription));
    }

    [Fact]
    public async Task An_unreachable_upstream_reports_the_failure_to_the_caller()
    {
        await using var host = RelayHost.Start(
            new GatewayWebSocketRelay(null, ignoreServerCertificateErrors: false), DeadUpstream());

        using var cts = new CancellationTokenSource(Limit);
        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(host.Url, cts.Token);

        Assert.NotNull(await host.Outcome.WaitAsync(Limit));
    }

    [Theory]
    // http(s) becomes ws(s); the path, query and an explicit port come along unchanged.
    [InlineData("https://api.internal/orders?page=2", "wss://api.internal/orders?page=2")]
    [InlineData("http://api.internal/orders?page=2", "ws://api.internal/orders?page=2")]
    [InlineData("https://api.internal:8443/socket", "wss://api.internal:8443/socket")]
    [InlineData("http://127.0.0.1:8080/v1/chat", "ws://127.0.0.1:8080/v1/chat")]
    // Already a WebSocket address: nothing to map.
    [InlineData("wss://api.internal:8443/socket", "wss://api.internal:8443/socket")]
    [InlineData("ws://127.0.0.1:8080/v1/chat", "ws://127.0.0.1:8080/v1/chat")]
    public void ToWebSocketUri_maps_the_scheme_and_keeps_the_rest(string upstream, string expected) =>
        Assert.Equal(expected, GatewayWebSocketRelay.ToWebSocketUri(new Uri(upstream)).ToString());

    [Fact]
    public void ToWebSocketUri_refuses_a_scheme_that_cannot_carry_a_WebSocket() =>
        Assert.Throws<ArgumentException>(() =>
            GatewayWebSocketRelay.ToWebSocketUri(new Uri("ftp://files.internal/socket")));
}
