using ApiTester.Cli;
using ApiTester.Core;
using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Tests.Cli;

public class CertPickerTests
{
    private static CertificateInfo Info(string subject, X509Certificate2 cert, DateTime? notAfter = null) => new()
    {
        Subject = subject,
        Issuer = "CN=CA",
        Thumbprint = cert.Thumbprint!,
        NotBefore = DateTime.Now.AddDays(-1),
        NotAfter = notAfter ?? DateTime.Now.AddDays(30),
        HasClientAuthEku = true,
        Certificate = cert
    };

    private static X509Certificate2 MakeCert(string cn)
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        return SelfSignedCertificateFactory.CreateSignedCertificate(cn, ca, false, true);
    }

    [Fact]
    public void Resolves_by_thumbprint_ignoring_case_and_spaces()
    {
        var cert = MakeCert("Alice");
        var list = new[] { Info("CN=Alice", cert) };
        var spaced = string.Join(" ", cert.Thumbprint!.ToLowerInvariant().Chunk(2).Select(c => new string(c)));

        var hit = CertPicker.Resolve(list, spaced, TextWriter.Null);
        Assert.Equal("CN=Alice", hit.Subject);
    }

    [Fact]
    public void Resolves_by_subject_substring()
    {
        var list = new[] { Info("CN=Alice Prod", MakeCert("AliceProd")), Info("CN=Bob", MakeCert("Bob")) };
        Assert.Equal("CN=Bob", CertPicker.Resolve(list, "bob", TextWriter.Null).Subject);
    }

    [Fact]
    public void Zero_matches_is_a_data_error_pointing_at_certs()
    {
        var ex = Assert.Throws<CliDataException>(() =>
            CertPicker.Resolve(Array.Empty<CertificateInfo>(), "nobody", TextWriter.Null));
        Assert.Contains("certapi certs", ex.Message);
    }

    [Fact]
    public void Ambiguity_is_a_data_error_listing_candidates()
    {
        var list = new[] { Info("CN=Alice One", MakeCert("A1")), Info("CN=Alice Two", MakeCert("A2")) };
        var ex = Assert.Throws<CliDataException>(() => CertPicker.Resolve(list, "alice", TextWriter.Null));
        Assert.Contains("Alice One", ex.Message);
        Assert.Contains("Alice Two", ex.Message);
    }

    [Fact]
    public void Expired_match_warns_but_resolves()
    {
        var cert = MakeCert("Old");
        var list = new[] { Info("CN=Old", cert, DateTime.Now.AddDays(-1)) };
        var err = new StringWriter();
        var hit = CertPicker.Resolve(list, "old", err);
        Assert.Equal("CN=Old", hit.Subject);
        Assert.Contains("expired", err.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
