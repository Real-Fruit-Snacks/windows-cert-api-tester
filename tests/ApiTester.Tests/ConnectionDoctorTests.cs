using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Covers the doctor's pure decisions as data (issuer matching, interception heuristics)
/// and its staged run against loopback servers, including the failure paths that matter most:
/// a host that does not resolve, a port nothing listens on, and a server whose client-certificate
/// authority list does not include the certificate the user chose.</summary>
public class ConnectionDoctorTests
{
    private static readonly IReadOnlyList<X509Certificate2> NoCerts = Array.Empty<X509Certificate2>();

    private static DoctorStage Stage(DoctorReport r, string name) =>
        r.Stages.Single(s => s.Name == name);

    // ---------------------------------------------------------------- issuer matching (pure)

    [Fact]
    public void MatchIssuers_matches_on_issuer_ignoring_spacing_and_case()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Corp Issuing CA");
        using var leaf = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        // The server's spelling of the same authority, with different spacing and casing.
        string spelled = leaf.Issuer.Replace(", ", ",").ToUpperInvariant();

        var matched = DoctorReport.MatchIssuers(new[] { leaf }, new[] { spelled });

        Assert.Same(leaf, Assert.Single(matched));
    }

    [Fact]
    public void MatchIssuers_returns_nothing_when_no_certificate_shares_an_issuer()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Corp Issuing CA");
        using var leaf = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        Assert.Empty(DoctorReport.MatchIssuers(new[] { leaf }, new[] { "CN=Some Other CA, O=Elsewhere" }));
        // An empty request means the server asked for nothing — never "everything matches".
        Assert.Empty(DoctorReport.MatchIssuers(new[] { leaf }, Array.Empty<string>()));
    }

    // ---------------------------------------------------------------- interception notes (pure)

    [Theory]
    [InlineData("CN=Zscaler Root CA, O=Zscaler Inc.")]
    [InlineData("CN=Netskope Certificate Authority")]
    [InlineData("CN=Fortinet CA, O=Fortinet")]
    public void InterceptionNote_names_a_known_inspection_vendor(string rootSubject)
    {
        string? note = DoctorReport.InterceptionNote(rootSubject, rootIsLocallyTrusted: false);

        Assert.NotNull(note);
        Assert.Contains("decrypted and re-signed", note);
    }

    [Fact]
    public void InterceptionNote_flags_a_locally_trusted_private_root_more_cautiously()
    {
        string? note = DoctorReport.InterceptionNote("CN=Acme Internal Root", rootIsLocallyTrusted: true);

        Assert.NotNull(note);
        Assert.Contains("consistent with SSL inspection", note);
    }

    [Fact]
    public void InterceptionNote_is_silent_for_an_ordinary_public_root()
    {
        Assert.Null(DoctorReport.InterceptionNote("CN=DigiCert Global Root G2, O=DigiCert Inc", false));
    }

    // ---------------------------------------------------------------- staged runs

    [Fact]
    public async Task A_url_that_is_not_absolute_http_fails_at_the_url_stage()
    {
        var report = await ConnectionDoctor.RunAsync("not-a-url", null, NoCerts, new TransportOptions());

        Assert.False(report.Ok);
        Assert.Equal("url", report.FirstFailure!.Name);
        Assert.Single(report.Stages);   // nothing else was attempted
    }

    [Fact]
    public async Task A_host_that_does_not_resolve_fails_at_dns_and_advises_from_the_probe()
    {
        // The probe stands in for the internet-reachability check, so no test touches the network.
        var report = await ConnectionDoctor.RunAsync(
            "https://doctor-no-such-host.invalid/x", null, NoCerts, new TransportOptions(),
            probe: _ => Task.FromResult<string?>("Microsoft Connect Test"));

        Assert.False(report.Ok);
        Assert.Equal("dns", report.FirstFailure!.Name);
        Assert.Contains("specific to this host", Stage(report, "dns").Advice);
    }

    [Fact]
    public async Task A_captive_portal_answer_is_reported_as_such()
    {
        var report = await ConnectionDoctor.RunAsync(
            "https://doctor-no-such-host.invalid/x", null, NoCerts, new TransportOptions(),
            probe: _ => Task.FromResult<string?>("<html>Please sign in to continue</html>"));

        Assert.Contains("captive portal", Stage(report, "dns").Advice);
    }

    [Fact]
    public async Task No_internet_at_all_is_reported_when_the_probe_itself_fails()
    {
        var report = await ConnectionDoctor.RunAsync(
            "https://doctor-no-such-host.invalid/x", null, NoCerts, new TransportOptions(),
            probe: _ => Task.FromResult<string?>(null));

        Assert.Contains("no working internet connection", Stage(report, "dns").Advice);
    }

    [Fact]
    public async Task A_closed_port_resolves_but_fails_at_tcp()
    {
        var report = await ConnectionDoctor.RunAsync(
            "https://127.0.0.1:1/", null, NoCerts, new TransportOptions(),
            probe: _ => Task.FromResult<string?>("Microsoft Connect Test"));

        Assert.True(Stage(report, "dns").Ok);
        Assert.Equal("tcp", report.FirstFailure!.Name);
    }

    [Fact]
    public async Task A_healthy_mtls_endpoint_passes_every_stage_and_reports_the_handshake()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        var report = await ConnectionDoctor.RunAsync(
            server.BaseUrl, clientCert, new[] { clientCert }, new TransportOptions());

        Assert.True(report.Ok, report.FirstFailure?.Summary);
        var tls = Stage(report, "tls");
        Assert.Contains(tls.Detail, d => d.StartsWith("protocol: ", StringComparison.Ordinal));
        Assert.Contains(tls.Detail, d => d.StartsWith("cipher: ", StringComparison.Ordinal));
        Assert.Contains(tls.Detail, d => d.Contains("client certificate presented", StringComparison.Ordinal));
        Assert.Contains(report.Stages, s => s.Name == "http");
    }

    [Fact]
    public async Task The_acceptable_issuer_list_is_reported_and_matched_against_the_store()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        var report = await ConnectionDoctor.RunAsync(
            server.BaseUrl, clientCert, new[] { clientCert }, new TransportOptions());

        var tls = Stage(report, "tls");
        // Whether the loopback server sends an issuer list is the platform's business; what must
        // hold is that the report SAYS which of the two situations it found, rather than staying
        // silent about the single most useful fact in an mTLS diagnosis.
        Assert.Contains(tls.Detail, d =>
            d.Contains("did not ask for one", StringComparison.Ordinal) ||
            d.Contains("accepts client certificates from", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_plain_http_endpoint_skips_tls_and_still_reports_http()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);

        var report = await ConnectionDoctor.RunAsync(
            $"http://127.0.0.1:{mock.Port}/api/x", null, NoCerts, new TransportOptions());

        Assert.True(report.Ok, report.FirstFailure?.Summary);
        Assert.DoesNotContain(report.Stages, s => s.Name == "tls");
        Assert.Contains("200", Stage(report, "http").Summary);
    }

    [Fact]
    public async Task The_proxy_stage_reports_the_decision_including_a_bypass()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);
        ProxyBypass.TryParse("127.0.0.1", out var rules, out _);

        var report = await ConnectionDoctor.RunAsync(
            $"http://127.0.0.1:{mock.Port}/api/x", null, NoCerts,
            new TransportOptions { Proxy = ProxyMode.Explicit, ProxyUrl = "http://proxy.invalid:9", NoProxy = rules });

        // The bypass wins over the explicit proxy, and the report says which rule did it.
        Assert.Contains("bypassed by '127.0.0.1'", Stage(report, "proxy").Summary);
        Assert.True(report.Ok, report.FirstFailure?.Summary);
    }

    [Fact]
    public async Task An_unreachable_proxy_blames_the_proxy_not_the_host()
    {
        var report = await ConnectionDoctor.RunAsync(
            "https://api.example.com/x", null, NoCerts,
            new TransportOptions { Proxy = ProxyMode.Explicit, ProxyUrl = "http://127.0.0.1:1" });

        Assert.Equal("tcp", report.FirstFailure!.Name);
        Assert.Contains("PROXY", Stage(report, "tcp").Advice);
    }

    [Fact]
    public async Task Through_a_proxy_the_target_host_is_never_resolved_locally()
    {
        // The corporate case this ordering exists for: an internal name only the proxy can see.
        // Resolving it here would fail a connection that would actually have worked, so DNS must
        // report on the PROXY's hostname instead — and the run must get as far as TCP.
        var report = await ConnectionDoctor.RunAsync(
            "https://intranet-only.invalid/x", null, NoCerts,
            new TransportOptions { Proxy = ProxyMode.Explicit, ProxyUrl = "http://127.0.0.1:1" });

        var dns = Stage(report, "dns");
        Assert.True(dns.Ok);
        Assert.Contains("resolved by it, not here", dns.Summary);
        Assert.Equal("tcp", report.FirstFailure!.Name);
    }
}
