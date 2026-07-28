using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Proves `--proxy socks5://…` end to end: a minimal no-auth SOCKS5 CONNECT server on
/// loopback fronts the mTLS loopback server, and the client's mutual-TLS handshake — client
/// certificate included — runs *through* the tunnel, because SOCKS relays bytes rather than
/// terminating TLS. That is the property that makes an SSH jump host (`ssh -D 1080`) a way to
/// reach a certificate-protected API, and it is the reason this scheme is worth supporting at
/// all.</summary>
public class SocksProxyTests : IAsyncLifetime
{
    private TcpListener _listener = null!;
    private Task? _accepting;
    private int _connects;

    public int Port { get; private set; }

    public Task InitializeAsync()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = AcceptLoopAsync();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _listener.Stop();
        try { if (_accepting is not null) await _accepting; } catch { /* listener stopped */ }
    }

    // ---------------------------------------------------------------- the fixture

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch { return; }   // listener stopped
            _ = HandleAsync(client);
        }
    }

    /// <summary>RFC 1928, the no-auth subset: greeting, CONNECT, then a dumb byte pump. Anything
    /// unexpected just closes the connection — a diagnostic fixture has no error protocol.</summary>
    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var buffer = new byte[512];

        // Greeting: VER NMETHODS METHODS… → choose no-auth (00).
        if (await stream.ReadAsync(buffer.AsMemory(0, 2)) != 2 || buffer[0] != 5) return;
        int methods = buffer[1];
        if (await FillAsync(stream, buffer, methods) != methods) return;
        await stream.WriteAsync(new byte[] { 5, 0 });

        // Request: VER CMD RSV ATYP DST.ADDR DST.PORT — only CONNECT (01) is served.
        if (await FillAsync(stream, buffer, 4) != 4 || buffer[0] != 5 || buffer[1] != 1) return;
        string host;
        switch (buffer[3])
        {
            case 1:   // IPv4
                if (await FillAsync(stream, buffer, 4) != 4) return;
                host = new IPAddress(buffer.AsSpan(0, 4).ToArray()).ToString();
                break;
            case 3:   // domain name
                if (await FillAsync(stream, buffer, 1) != 1) return;
                int len = buffer[0];
                if (await FillAsync(stream, buffer, len) != len) return;
                host = System.Text.Encoding.ASCII.GetString(buffer, 0, len);
                break;
            default: return;   // IPv6 not needed by these tests
        }
        if (await FillAsync(stream, buffer, 2) != 2) return;
        int port = (buffer[0] << 8) | buffer[1];

        Interlocked.Increment(ref _connects);

        using var upstream = new TcpClient();
        try { await upstream.ConnectAsync(host, port); }
        catch
        {
            await stream.WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 });   // refused
            return;
        }
        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });        // granted

        var up = upstream.GetStream();
        var a = stream.CopyToAsync(up);
        var b = up.CopyToAsync(stream);
        try { await Task.WhenAny(a, b); } catch { /* either side closing ends the tunnel */ }
    }

    private static async Task<int> FillAsync(NetworkStream stream, byte[] buffer, int count)
    {
        int have = 0;
        while (have < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(have, count - have));
            if (n == 0) return have;
            have += n;
        }
        return have;
    }

    // ---------------------------------------------------------------- the tests

    [Fact]
    public async Task Mutual_tls_runs_through_a_socks5_tunnel_with_the_client_certificate_intact()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var upstream = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        var response = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = upstream.BaseUrl },
            clientCert,
            transport: new TransportOptions
            {
                Proxy = ProxyMode.Explicit,
                ProxyUrl = $"socks5://127.0.0.1:{Port}",
                IgnoreServerCertificateErrors = true
            });

        Assert.True(response.IsSuccess, response.Error?.Message);
        Assert.Equal(200, response.StatusCode);
        // The proof the bytes went through the tunnel, not around it.
        Assert.Equal(1, _connects);
        Assert.True(response.Connection?.ViaProxy);
    }

    [Fact]
    public void Socks_schemes_pass_validation_and_junk_schemes_still_do_not()
    {
        foreach (var scheme in new[] { "socks5", "socks4", "socks4a" })
            Assert.Null(ApiClient.ValidateTransport(
                new TransportOptions { Proxy = ProxyMode.Explicit, ProxyUrl = $"{scheme}://127.0.0.1:1080" },
                "https://api.example.com/x"));

        var refused = ApiClient.ValidateTransport(
            new TransportOptions { Proxy = ProxyMode.Explicit, ProxyUrl = "ftp://127.0.0.1:1080" },
            "https://api.example.com/x");
        Assert.NotNull(refused);
        Assert.Contains("socks", refused);   // the message teaches what IS accepted
    }

    [Fact]
    public async Task A_bypass_rule_still_sends_matching_hosts_around_the_socks_proxy()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var upstream = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        ProxyBypass.TryParse("127.0.0.1", out var rules, out _);
        var response = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = upstream.BaseUrl },
            clientCert,
            transport: new TransportOptions
            {
                Proxy = ProxyMode.Explicit,
                ProxyUrl = $"socks5://127.0.0.1:{Port}",
                NoProxy = rules,
                IgnoreServerCertificateErrors = true
            });

        Assert.True(response.IsSuccess, response.Error?.Message);
        Assert.Equal(0, _connects);   // the tunnel was never used
    }
}
