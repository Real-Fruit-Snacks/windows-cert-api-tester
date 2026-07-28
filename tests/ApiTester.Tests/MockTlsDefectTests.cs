using System.IO;
using System.Net.Http;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The mock serving a deliberately broken certificate. Until now every client-side TLS
/// error this product reports was only reachable from inside the suite's own fixtures; these prove
/// the mock can produce each one on demand — which is what makes `doctor` and `send`'s error paths
/// reproducible at a terminal.</summary>
public class MockTlsDefectTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("certapi-tlsdefect-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private X509CertificateHolder Generate(MockTlsDefect defect) =>
        new(MockCertificates.Generate(MockTlsMode.Https, _dir, defect).ServerCertificate);

    /// <summary>Owns a generated certificate for the length of a test.</summary>
    private sealed class X509CertificateHolder(System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
        : IDisposable
    {
        public System.Security.Cryptography.X509Certificates.X509Certificate2 Cert { get; } = cert;
        public void Dispose() => Cert.Dispose();
    }

    // ---------------------------------------------------------------- the certificates themselves

    [Fact]
    public void The_default_certificate_is_valid_for_localhost()
    {
        using var held = Generate(MockTlsDefect.None);

        Assert.Contains("localhost", held.Cert.Subject);
        Assert.True(held.Cert.NotAfter > DateTime.Now, "the ordinary certificate should not be expired");
    }

    [Fact]
    public void The_expired_mode_produces_a_certificate_whose_validity_has_already_ended()
    {
        using var held = Generate(MockTlsDefect.Expired);

        Assert.True(held.Cert.NotAfter < DateTime.Now, "expired mode should produce an expired certificate");
        // It expired, rather than never having been valid: a certificate that WAS fine is the
        // realistic case, and it exercises a different message than "not yet valid".
        Assert.True(held.Cert.NotBefore < DateTime.Now, "it should have been valid at some point");
    }

    [Fact]
    public void The_wrong_host_mode_produces_a_valid_certificate_for_somewhere_else()
    {
        using var held = Generate(MockTlsDefect.WrongHost);

        Assert.DoesNotContain("localhost", held.Cert.Subject);
        Assert.True(held.Cert.NotAfter > DateTime.Now, "the point is the NAME, so the dates must be fine");
    }

    [Fact]
    public void The_self_signed_mode_is_its_own_issuer()
    {
        using var held = Generate(MockTlsDefect.SelfSigned);

        Assert.Equal(held.Cert.Subject, held.Cert.Issuer);
        // The ordinary one chains to the mock's certificate authority instead.
        using var ordinary = Generate(MockTlsDefect.None);
        Assert.NotEqual(ordinary.Cert.Subject, ordinary.Cert.Issuer);
    }

    // ---------------------------------------------------------------- over the wire

    [Theory]
    [InlineData(MockTlsDefect.Expired)]
    [InlineData(MockTlsDefect.WrongHost)]
    [InlineData(MockTlsDefect.SelfSigned)]
    public async Task A_client_refuses_a_broken_certificate_and_says_it_was_untrusted(MockTlsDefect defect)
    {
        using var held = Generate(defect);
        await using var mock = MockServer.Start(0, MockTlsMode.Https, held.Cert);

        var response = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = $"https://127.0.0.1:{mock.Port}/api/x" },
            clientCertificate: null);

        Assert.False(response.IsSuccess);
        Assert.Equal(ApiErrorKind.ServerCertificateUntrusted, response.Error?.Kind);
    }

    [Theory]
    [InlineData(MockTlsDefect.Expired)]
    [InlineData(MockTlsDefect.WrongHost)]
    [InlineData(MockTlsDefect.SelfSigned)]
    public async Task Insecure_still_gets_through_a_broken_certificate(MockTlsDefect defect)
    {
        // The escape hatch has to keep working against every defect, or the mode would be useless
        // for testing anything BUT the refusal.
        using var held = Generate(defect);
        await using var mock = MockServer.Start(0, MockTlsMode.Https, held.Cert);

        var response = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = $"https://127.0.0.1:{mock.Port}/api/x" },
            clientCertificate: null,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.True(response.IsSuccess, response.Error?.Message);
    }

    [Fact]
    public async Task Doctor_reports_the_handshake_problem_against_a_broken_certificate()
    {
        // The point of the whole feature: doctor's TLS stage becomes reproducible at a terminal.
        using var held = Generate(MockTlsDefect.Expired);
        await using var mock = MockServer.Start(0, MockTlsMode.Https, held.Cert);

        var report = await ConnectionDoctor.RunAsync(
            $"https://127.0.0.1:{mock.Port}/api/x", null,
            Array.Empty<System.Security.Cryptography.X509Certificates.X509Certificate2>(),
            new TransportOptions());

        var tls = report.Stages.Single(s => s.Name == "tls");
        // Doctor deliberately accepts the certificate so it can describe it; the problem shows up
        // in the summary rather than as a refusal, which is exactly what a diagnosis should do.
        Assert.Contains("certificate", tls.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(tls.Detail, d => d.Contains("server certificate", StringComparison.OrdinalIgnoreCase));
    }
}
