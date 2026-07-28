using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ApiTester.Tests;

/// <summary>Pins the `--http3` contract. The wire test runs against a Kestrel endpoint that speaks
/// HTTP/3 ONLY, so a client that did not actually negotiate QUIC cannot pass it — success IS the
/// proof, with no version field to trust. Guarded by <see cref="System.Net.Quic.QuicListener.IsSupported"/>:
/// on an OS without msquic the wire test records that it could not run and passes vacuously — the
/// one sanctioned soft-pass in this suite, because failing on hardware the feature honestly
/// documents as unsupported would teach nothing.</summary>
public class Http3Tests
{
    // ---------------------------------------------------------------- validation (pure, always runs)

    [Fact]
    public void Http3_refuses_a_proxy_and_a_resolve_pin_by_name()
    {
        var proxied = ApiClient.ValidateTransport(
            new TransportOptions { Version = HttpVersionMode.Http3, Proxy = ProxyMode.Explicit, ProxyUrl = "http://p:8080" },
            "https://api.example.com/x");
        Assert.NotNull(proxied);
        Assert.Contains("UDP", proxied);

        var pinned = ApiClient.ValidateTransport(
            new TransportOptions
            {
                Version = HttpVersionMode.Http3,
                Resolve = new[] { new ResolveOverride("api.example.com", 443, "127.0.0.1") }
            },
            "https://api.example.com/x");
        Assert.NotNull(pinned);
        Assert.Contains("--resolve", pinned);
    }

    [Fact]
    public void Http3_direct_with_no_proxy_in_play_passes_validation()
    {
        Assert.Null(ApiClient.ValidateTransport(
            new TransportOptions { Version = HttpVersionMode.Http3, Proxy = ProxyMode.None },
            "https://api.example.com/x"));
    }

    // ---------------------------------------------------------------- the wire (QUIC-gated)

    [Fact]
    public async Task A_pinned_http3_request_reaches_an_h3_only_server()
    {
        if (!System.Net.Quic.QuicListener.IsSupported)
            return;   // no msquic on this OS — the guard the feature's own docs promise

        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, true, false, new[] { "localhost" });

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                // HTTP/3 ONLY: an H1/H2 client cannot pass this test by accident.
                listen.Protocols = HttpProtocols.Http3;
                listen.UseHttps(https => https.ServerCertificate = serverCert);
            });
        });
        var app = builder.Build();
        app.MapGet("/h3", () => "over-quic");
        await app.StartAsync();
        try
        {
            var address = app.Urls.First();
            var response = await new ApiClient().SendAsync(
                new ApiRequest { Method = HttpMethod.Get, Url = address + "/h3" },
                clientCertificate: null,
                transport: new TransportOptions
                {
                    Version = HttpVersionMode.Http3,
                    IgnoreServerCertificateErrors = true
                });

            Assert.True(response.IsSuccess, response.Error?.Message);
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("over-quic", System.Text.Encoding.UTF8.GetString(response.Body));
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task A_pinned_http2_request_fails_loudly_against_an_h3_only_server()
    {
        if (!System.Net.Quic.QuicListener.IsSupported)
            return;   // same guard, same reason

        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, true, false, new[] { "localhost" });

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http3;
                listen.UseHttps(https => https.ServerCertificate = serverCert);
            });
        });
        var app = builder.Build();
        app.MapGet("/h3", () => "over-quic");
        await app.StartAsync();
        try
        {
            // The control: pinning is exact, so the wrong version against this server must be a
            // loud failure, not a silent downgrade-or-upgrade to whatever works.
            var response = await new ApiClient().SendAsync(
                new ApiRequest { Method = HttpMethod.Get, Url = app.Urls.First() + "/h3" },
                clientCertificate: null,
                transport: new TransportOptions
                {
                    Version = HttpVersionMode.Http2,
                    IgnoreServerCertificateErrors = true
                });

            Assert.False(response.IsSuccess);
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
