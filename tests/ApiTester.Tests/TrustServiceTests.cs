using System.IO;
using System.Net.Http;
using ApiTester.Core;

namespace ApiTester.Tests;

public class TrustServiceTests
{
    [Fact]
    public void Trusted_host_and_thumbprint_pair_is_trusted_case_insensitively()
    {
        var state = new AppState();
        TrustService.Trust(state, "Internal.Corp", "ABC123", "CN=x");

        Assert.True(TrustService.IsTrusted(state, "internal.corp", "abc123"));
    }

    [Fact]
    public void Different_thumbprint_on_trusted_host_is_not_trusted()
    {
        var state = new AppState();
        TrustService.Trust(state, "Internal.Corp", "ABC123", "CN=x");

        Assert.False(TrustService.IsTrusted(state, "internal.corp", "DIFFERENT"));
    }

    [Fact]
    public void Same_thumbprint_on_different_host_is_not_trusted()
    {
        var state = new AppState();
        TrustService.Trust(state, "Internal.Corp", "ABC123", "CN=x");

        Assert.False(TrustService.IsTrusted(state, "other.host", "ABC123"));
    }

    [Fact]
    public void Trusting_the_same_host_and_thumbprint_twice_leaves_a_single_entry()
    {
        var state = new AppState();
        TrustService.Trust(state, "internal.corp", "ABC123", "CN=x");
        TrustService.Trust(state, "internal.corp", "ABC123", "CN=x updated");

        Assert.Single(state.TrustedServerCerts);
    }

    [Fact]
    public void Trusting_a_second_thumbprint_on_the_same_host_yields_two_entries()
    {
        var state = new AppState();
        TrustService.Trust(state, "internal.corp", "ABC123", "CN=x");
        TrustService.Trust(state, "internal.corp", "DEF456", "CN=y");

        Assert.Equal(2, state.TrustedServerCerts.Count);
        Assert.True(TrustService.IsTrusted(state, "internal.corp", "ABC123"));
        Assert.True(TrustService.IsTrusted(state, "internal.corp", "DEF456"));
    }

    [Fact]
    public void Untrust_with_thumbprint_removes_only_that_pin()
    {
        var state = new AppState();
        TrustService.Trust(state, "internal.corp", "ABC123", "CN=x");
        TrustService.Trust(state, "internal.corp", "DEF456", "CN=y");

        TrustService.Untrust(state, "internal.corp", "ABC123");

        Assert.False(TrustService.IsTrusted(state, "internal.corp", "ABC123"));
        Assert.True(TrustService.IsTrusted(state, "internal.corp", "DEF456"));
    }

    [Fact]
    public void Untrust_without_thumbprint_removes_all_pins_for_that_host()
    {
        var state = new AppState();
        TrustService.Trust(state, "internal.corp", "ABC123", "CN=x");
        TrustService.Trust(state, "internal.corp", "DEF456", "CN=y");
        TrustService.Trust(state, "other.host", "ABC123", "CN=z");

        TrustService.Untrust(state, "internal.corp");

        Assert.False(TrustService.IsTrusted(state, "internal.corp", "ABC123"));
        Assert.False(TrustService.IsTrusted(state, "internal.corp", "DEF456"));
        Assert.True(TrustService.IsTrusted(state, "other.host", "ABC123"));
    }

    [Fact]
    public void Fresh_app_state_has_an_empty_trusted_server_certs_list()
    {
        var state = new AppState();

        Assert.NotNull(state.TrustedServerCerts);
        Assert.Empty(state.TrustedServerCerts);
    }

    [Fact]
    public void Save_and_load_round_trips_pinned_certs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trust-state-{Guid.NewGuid():N}.json");
        try
        {
            var state = new AppState();
            TrustService.Trust(state, "internal.corp", "ABC123", "CN=x");
            TrustService.Trust(state, "other.host", "DEF456", "CN=y");
            state.SaveTo(path);

            var loaded = AppState.LoadFrom(path);

            Assert.True(TrustService.IsTrusted(loaded, "internal.corp", "ABC123"));
            Assert.True(TrustService.IsTrusted(loaded, "other.host", "DEF456"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Pinned_thumbprint_lets_sendasync_reach_an_untrusted_selfsigned_loopback_server()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);

        var state = new AppState();
        TrustService.Trust(state, "127.0.0.1", serverCert);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            trustServerCertificate: c => c is not null && TrustService.IsTrusted(state, "127.0.0.1", c.Thumbprint!));

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal(200, resp.StatusCode);
    }

    [Fact]
    public async Task Unpinned_selfsigned_loopback_server_is_rejected_as_untrusted()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);

        // No pin recorded for this host at all.
        var state = new AppState();

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            trustServerCertificate: c => c is not null && TrustService.IsTrusted(state, "127.0.0.1", c.Thumbprint!));

        Assert.False(resp.IsSuccess);
        Assert.Equal(ApiErrorKind.ServerCertificateUntrusted, resp.Error?.Kind);
    }
}
