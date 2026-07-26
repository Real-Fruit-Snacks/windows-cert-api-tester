using System.Net.Http;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

public class ApiClientProxyTests
{
    /// <summary>Generous: a tunnelled request that hangs must fail this test rather than the suite.</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task An_explicit_proxy_tunnels_the_request_to_the_server()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(
            serverCert, clientCert.Thumbprint!, "{\"through\":\"proxy\"}");
        await using var proxy = await LoopbackConnectProxy.StartAsync();
        var origin = new Uri(server.BaseUrl);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl, Timeout = SendTimeout },
            clientCert,
            transport: new TransportOptions
            {
                Proxy = ProxyMode.Explicit,
                ProxyUrl = proxy.Url,
                IgnoreServerCertificateErrors = true
            });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal(200, resp.StatusCode);
        Assert.Equal("{\"through\":\"proxy\"}", Encoding.UTF8.GetString(resp.Body));

        // The proxy is a real network peer, so this is evidence the bytes went through it and not
        // straight to the server.
        Assert.Equal(1, proxy.ConnectCount);
        Assert.Equal(new[] { $"{origin.Host}:{origin.Port}" }, proxy.Targets);

        Assert.True(resp.Connection!.ViaProxy);
        Assert.Equal(proxy.Url, resp.Connection.ProxyUri);
    }

    [Fact]
    public async Task Bypassing_the_proxy_connects_directly_and_keeps_the_tls_diagnostics()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(
            serverCert, clientCert.Thumbprint!, "{\"direct\":true}");
        // A proxy that is up and reachable, and deliberately not used: if the request still went
        // through one, this proxy is the one it would reach.
        await using var proxy = await LoopbackConnectProxy.StartAsync();

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl, Timeout = SendTimeout },
            clientCert,
            transport: new TransportOptions
            {
                Proxy = ProxyMode.None,
                ProxyUrl = proxy.Url,
                IgnoreServerCertificateErrors = true
            });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal("{\"direct\":true}", Encoding.UTF8.GetString(resp.Body));
        Assert.Equal(0, proxy.ConnectCount);
        Assert.Empty(proxy.Targets);
        Assert.False(resp.Connection!.ViaProxy);
        Assert.Null(resp.Connection.ProxyUri);

        // Bypassing the proxy does double duty: the client drives the handshake itself again, so the
        // TLS facts a user needs come back with it.
        Assert.NotNull(resp.Connection.TlsProtocol);
        Assert.True(resp.Connection.ClientCertificateSent);
    }

    [Fact]
    public async Task A_tunnelled_request_succeeds_but_reports_no_tls_details()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);
        await using var proxy = await LoopbackConnectProxy.StartAsync();

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl, Timeout = SendTimeout },
            clientCert,
            transport: new TransportOptions
            {
                Proxy = ProxyMode.Explicit,
                ProxyUrl = proxy.Url,
                IgnoreServerCertificateErrors = true
            });

        // The handler drives the CONNECT and the handshake behind it, so the negotiated version and
        // cipher are not the client's to read. Pinned deliberately: this blind spot is the reason
        // --no-proxy exists, and a change that silently altered it should fail here.
        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Null(resp.Connection!.TlsProtocol);
        Assert.Null(resp.Connection.CipherSuite);
        Assert.False(resp.Connection.ClientCertificateSent);

        // The server certificate is still observed, because validation runs either way.
        Assert.Contains("localhost", resp.Connection.ServerCertificateSubject);
    }

    [Fact]
    public async Task Proxy_credentials_answer_a_proxy_authentication_challenge()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(
            serverCert, clientCert.Thumbprint!, "{\"authenticated\":true}");
        await using var proxy = await LoopbackConnectProxy.StartRequiringBasicAuthAsync("proxyuser", "s3cret");

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl, Timeout = SendTimeout },
            clientCert,
            transport: new TransportOptions
            {
                Proxy = ProxyMode.Explicit,
                ProxyUrl = proxy.Url,
                ProxyUser = "proxyuser",
                ProxyPassword = "s3cret",
                IgnoreServerCertificateErrors = true
            });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal("{\"authenticated\":true}", Encoding.UTF8.GetString(resp.Body));

        string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("proxyuser:s3cret"));
        Assert.Contains(expected, proxy.ProxyAuthorizations);
        // The challenged attempt never tunnelled; only the authenticated retry did.
        Assert.Equal(1, proxy.ConnectCount);
    }

    [Fact]
    public async Task Pinning_an_address_through_a_proxy_is_refused_without_contacting_the_proxy()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);
        await using var proxy = await LoopbackConnectProxy.StartAsync();
        int port = new Uri(server.BaseUrl).Port;

        var transport = new TransportOptions
        {
            Proxy = ProxyMode.Explicit,
            ProxyUrl = proxy.Url,
            IgnoreServerCertificateErrors = true,
            Resolve = new[] { new ResolveOverride("127.0.0.1", port, "127.0.0.1") }
        };

        string? problem = ApiClient.ValidateTransport(transport, server.BaseUrl);
        Assert.NotNull(problem);
        Assert.Contains("--resolve", problem);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl, Timeout = SendTimeout },
            clientCert,
            transport: transport);

        Assert.False(resp.IsSuccess);
        Assert.Null(resp.StatusCode);
        Assert.Equal(problem, resp.Error?.Message);

        // A usage error is only a usage error if it lands before anything is sent: this request,
        // which would otherwise have tunnelled, never reached the proxy at all.
        Assert.Equal(0, proxy.ConnectCount);
    }
}
