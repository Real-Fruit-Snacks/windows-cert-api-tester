using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Proves the revocation mode set on <see cref="TransportOptions"/> actually reaches
/// ApiClient's TLS stack -- decided via the shared <see cref="RevocationCheck"/> table -- and is
/// reported back on <see cref="ApiResponse.Connection"/>, rather than merely being parsed and
/// dropped. A genuinely revoked certificate needs a real public-key infrastructure and cannot be
/// produced here (see <see cref="RevocationCheckTests"/> for that half, at the pure-function
/// level); what these tests can and do prove is that the setting is actually wired through.</summary>
public class ApiClientRevocationTests
{
    [Fact]
    public async Task Online_revocation_against_an_unreachable_crl_reports_unknown_while_none_reports_not_checked()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, true, false, new[] { "localhost" },
            crlDistributionPoint: SelfSignedCertificateFactory.UnroutableCrlDistributionPoint);
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);

        // The CA is self-signed and therefore untrusted on its own, so a pin matching the server
        // certificate's own thumbprint is supplied on both sends -- that holds trust constant and
        // leaves the revocation mode as the only thing that differs between them.
        bool Pin(X509Certificate2? cert) => cert is not null && cert.Thumbprint == serverCert.Thumbprint;

        using var client = new ApiClient();

        var online = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { Revocation = RevocationMode.Online },
            trustServerCertificate: Pin);

        var none = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { Revocation = RevocationMode.None },
            trustServerCertificate: Pin);

        // An unreachable revocation endpoint must not be fatal by default -- this is the common
        // corporate case the whole feature exists for.
        Assert.True(online.IsSuccess, online.Error?.Message);
        Assert.True(none.IsSuccess, none.Error?.Message);

        // The contrast is the proof: Unknown can only appear if the platform genuinely attempted
        // to fetch the CRL, which only happens when the requested mode actually reached the stack.
        Assert.Equal(RevocationMode.Online, online.Connection?.RevocationMode);
        Assert.Equal(RevocationStatus.Unknown, online.Connection?.RevocationStatus);
        Assert.Equal(RevocationStatus.NotChecked, none.Connection?.RevocationStatus);
    }

    [Fact]
    public async Task Insecure_with_online_revocation_reports_that_revocation_was_not_enforced()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { Revocation = RevocationMode.Online, IgnoreServerCertificateErrors = true });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal(RevocationStatus.NotEnforced, resp.Connection?.RevocationStatus);
    }

    [Fact]
    public async Task Insecure_with_no_revocation_mode_reports_not_checked_rather_than_not_enforced()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { Revocation = RevocationMode.None, IgnoreServerCertificateErrors = true });

        // Nothing was asked for, so there is nothing to report as unenforced.
        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal(RevocationStatus.NotChecked, resp.Connection?.RevocationStatus);
    }

    [Fact]
    public async Task Untrusted_server_by_default_still_maps_to_untrusted_not_revoked()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions());

        Assert.False(resp.IsSuccess);
        Assert.Equal(ApiErrorKind.ServerCertificateUntrusted, resp.Error?.Kind);
        Assert.Equal(RevocationStatus.NotChecked, resp.Connection?.RevocationStatus);
    }

    [Fact]
    public async Task Pinned_thumbprint_still_accepts_an_untrusted_server_under_the_default_revocation_mode()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions(),
            trustServerCertificate: c => c is not null && c.Thumbprint == serverCert.Thumbprint);

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal(RevocationStatus.NotChecked, resp.Connection?.RevocationStatus);
    }

    [Fact]
    public void ValidateTransport_refuses_strict_without_a_revocation_mode()
    {
        string? problem = ApiClient.ValidateTransport(
            new TransportOptions { RevocationStrict = true }, "https://example.com");

        Assert.Equal(RevocationCheck.StrictWithoutCheckingMessage, problem);
    }

    [Fact]
    public void ValidateTransport_allows_strict_alongside_a_revocation_mode()
    {
        Assert.Null(ApiClient.ValidateTransport(
            new TransportOptions { RevocationStrict = true, Revocation = RevocationMode.Online },
            "https://example.com"));

        Assert.Null(ApiClient.ValidateTransport(
            new TransportOptions { RevocationStrict = true, Revocation = RevocationMode.Offline },
            "https://example.com"));
    }

    [Fact]
    public async Task SendAsync_refuses_the_strict_without_checking_combination_before_opening_a_socket()
    {
        // Loopback port 1 refuses a connection immediately (see
        // SelfSignedCertificateFactory.UnroutableCrlDistributionPoint's own remarks on why this
        // address is used elsewhere to prove the same thing): if this invalid combination reached
        // the socket layer at all, the error would be ConnectionRefused, not Unknown -- see
        // Connection_refused_maps_to_a_connection_refused_error in ApiClientTests for that mapping.
        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = "https://127.0.0.1:1/" },
            clientCertificate: null,
            transport: new TransportOptions { RevocationStrict = true });

        Assert.False(resp.IsSuccess);
        Assert.Null(resp.StatusCode);
        Assert.Equal(ApiErrorKind.Unknown, resp.Error?.Kind);
        Assert.Equal(RevocationCheck.StrictWithoutCheckingMessage, resp.Error?.Message);
    }
}
