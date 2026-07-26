using System.Net.Http;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

public class ApiClientTransportTests
{
    [Fact]
    public async Task Compressed_response_is_decoded_by_default()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartGzipAsync(
            serverCert, clientCert.Thumbprint!, "{\"gzip\":\"ok\"}");

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal("{\"gzip\":\"ok\"}", Encoding.UTF8.GetString(resp.Body));
        Assert.False(resp.Body.Length >= 2 && resp.Body[0] == 0x1F && resp.Body[1] == 0x8B,
            "the body still carries the gzip magic number, so it was not decoded");
    }

    [Fact]
    public async Task Decompression_turned_off_relays_the_compressed_bytes_as_received()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartGzipAsync(
            serverCert, clientCert.Thumbprint!, "{\"gzip\":\"ok\"}");

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true, Decompress = false });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.True(resp.Body.Length >= 2, "expected the raw gzip stream, got an empty body");
        Assert.Equal(0x1F, resp.Body[0]);   // gzip magic number
        Assert.Equal(0x8B, resp.Body[1]);
        Assert.NotEqual("{\"gzip\":\"ok\"}", Encoding.UTF8.GetString(resp.Body));
    }

    [Fact]
    public async Task Resolve_override_dials_the_pinned_address_while_the_host_header_keeps_the_original_name()
    {
        // A .invalid name can never resolve (RFC 2606), so reaching the server at all proves the
        // override supplied the address; the certificate is issued for that same name, so the
        // handshake only completes if the TLS server name carried the original hostname too.
        const string hostname = "certapi-resolve-test.invalid";
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            hostname, ca, true, false, new[] { hostname });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartEchoAsync(serverCert, clientCert.Thumbprint!);
        int port = new Uri(server.BaseUrl).Port;

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = $"https://{hostname}:{port}/" },
            clientCert,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Resolve = new[] { new ResolveOverride(hostname, port, "127.0.0.1") }
            });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        // The echo server replays the raw request text, so this is what the server actually received.
        Assert.Contains($"Host: {hostname}:{port}", Encoding.UTF8.GetString(resp.Body));
        // ...and the TLS server name it was asked for is the original hostname, not the pinned address.
        Assert.Equal(hostname, server.LastSniHost);
    }

    [Fact]
    public async Task Pinning_an_address_while_routing_through_a_proxy_is_refused_before_any_request()
    {
        const string url = "https://api.example.com/things";
        var transport = new TransportOptions
        {
            Proxy = ProxyMode.Explicit,
            ProxyUrl = "http://127.0.0.1:8888",
            Resolve = new[] { new ResolveOverride("api.example.com", 443, "10.0.0.1") }
        };

        string? problem = ApiClient.ValidateTransport(transport, url);
        Assert.NotNull(problem);
        Assert.Contains("--resolve", problem);

        // The same refusal comes back from a send, as an error rather than a silently ignored setting.
        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = url }, clientCertificate: null, transport: transport);

        Assert.False(resp.IsSuccess);
        Assert.Null(resp.StatusCode);
        Assert.Equal(problem, resp.Error?.Message);
    }

    [Fact]
    public void A_redirect_limit_below_one_and_a_non_absolute_proxy_url_are_refused()
    {
        const string url = "https://api.example.com/things";

        string? zeroRedirects = ApiClient.ValidateTransport(new TransportOptions { MaxRedirects = 0 }, url);
        Assert.NotNull(zeroRedirects);
        Assert.Contains("--max-redirs", zeroRedirects);

        string? relativeProxy = ApiClient.ValidateTransport(
            new TransportOptions { Proxy = ProxyMode.Explicit, ProxyUrl = "127.0.0.1:8888" }, url);
        Assert.NotNull(relativeProxy);
        Assert.Contains("--proxy", relativeProxy);
        Assert.Contains("127.0.0.1:8888", relativeProxy);

        // The defaults, and a well-formed explicit proxy, are accepted.
        Assert.Null(ApiClient.ValidateTransport(new TransportOptions(), url));
        Assert.Null(ApiClient.ValidateTransport(
            new TransportOptions { Proxy = ProxyMode.Explicit, ProxyUrl = "http://127.0.0.1:8888" }, url));
    }

    [Fact]
    public async Task A_refused_connection_reports_the_socket_error_and_the_exception_chain()
    {
        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = "https://127.0.0.1:1/" },
            clientCertificate: null,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.Equal(ApiErrorKind.ConnectionRefused, resp.Error?.Kind);
        Assert.Equal(System.Net.Sockets.SocketError.ConnectionRefused, resp.Error?.SocketErrorCode);
        Assert.NotEmpty(resp.Error!.Detail!);
    }
}
