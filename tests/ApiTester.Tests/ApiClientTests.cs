using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

public class ApiClientTests
{
    [Fact]
    public async Task Sends_client_cert_and_captures_json_response()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(
            serverCert, clientCert.Thumbprint!, "{\"hello\":\"world\"}");

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            trustServerCertificate: c => c is not null && c.Thumbprint == serverCert.Thumbprint);

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal(200, resp.StatusCode);
        Assert.Contains("hello", Encoding.UTF8.GetString(resp.Body));
        Assert.True(resp.Elapsed > TimeSpan.Zero);
    }

    [Fact]
    public async Task Missing_client_cert_is_refused()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCertificate: null,
            trustServerCertificate: _ => true);

        Assert.False(resp.IsSuccess);
        Assert.NotNull(resp.Error);
        // A missing client cert fails the mTLS handshake. Depending on the platform's TLS
        // stack this surfaces either as an AuthenticationException (-> CertificateRefused) or
        // a connection reset (-> Network); both are valid classifications of this failure.
        Assert.True(resp.Error!.Kind is ApiErrorKind.CertificateRefused or ApiErrorKind.Network,
            $"Unexpected error kind: {resp.Error.Kind} ({resp.Error.Message})");
    }

    [Fact]
    public async Task Malformed_url_maps_to_error()
    {
        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = "notaurl" },
            clientCertificate: null,
            ignoreServerCertificateErrors: true);

        Assert.False(resp.IsSuccess);
        Assert.NotNull(resp.Error);
        Assert.Equal(ApiErrorKind.Unknown, resp.Error!.Kind);
    }

    [Fact]
    public async Task Connection_refused_maps_to_network_error()
    {
        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = "https://127.0.0.1:1/" },
            clientCertificate: null,
            ignoreServerCertificateErrors: true);

        Assert.Equal(ApiErrorKind.Network, resp.Error?.Kind);
    }

    [Fact]
    public async Task Slow_server_maps_to_timeout()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = listener.AcceptTcpClientAsync(); // accept but never respond
        try
        {
            var resp = await new ApiClient().SendAsync(
                new ApiRequest
                {
                    Method = HttpMethod.Get,
                    Url = $"https://127.0.0.1:{port}/",
                    Timeout = TimeSpan.FromMilliseconds(400)
                },
                clientCertificate: null,
                ignoreServerCertificateErrors: true);

            Assert.Equal(ApiErrorKind.Timeout, resp.Error?.Kind);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Untrusted_server_cert_without_bypass_maps_to_untrusted()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);

        // No trustServerCertificate predicate and the bypass off: the self-signed server cert
        // is untrusted, so the handshake must be rejected and classified as ServerCertificateUntrusted.
        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            ignoreServerCertificateErrors: false);

        Assert.False(resp.IsSuccess);
        Assert.Equal(ApiErrorKind.ServerCertificateUntrusted, resp.Error?.Kind);
    }

    [Fact]
    public async Task Ignore_server_cert_errors_allows_untrusted_server()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        // Bypass ON: the same untrusted server cert is accepted and the request succeeds.
        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            ignoreServerCertificateErrors: true);

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal(200, resp.StatusCode);
    }
}
