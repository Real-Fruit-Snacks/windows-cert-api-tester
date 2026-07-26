using System.IO;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

public class GatewayCertificateTests
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    [Fact]
    public void Create_returns_a_self_signed_certificate_for_127_0_0_1_with_a_private_key()
    {
        using var cert = GatewayCertificate.Create();

        Assert.Equal("CN=127.0.0.1", cert.Subject);
        Assert.Equal(cert.Subject, cert.Issuer);
        Assert.True(cert.HasPrivateKey);
    }

    [Fact]
    public void Create_returns_a_certificate_whose_san_names_localhost_and_127_0_0_1()
    {
        using var cert = GatewayCertificate.Create();

        var san = cert.Extensions.Single(e => e.Oid?.Value == "2.5.29.17");
        string formatted = san.Format(true);
        Assert.Contains("localhost", formatted);
        Assert.Contains("127.0.0.1", formatted);
    }

    [Fact]
    public void Create_returns_a_certificate_with_server_authentication_eku()
    {
        using var cert = GatewayCertificate.Create();

        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(),
            o => o.Value == ServerAuthOid);
    }

    [Fact]
    public void LoadOrCreate_caches_the_certificate_across_calls_in_the_given_directory()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"certapi-gw-{Guid.NewGuid():N}");
        try
        {
            using var first = GatewayCertificate.LoadOrCreate(dir);
            Assert.True(File.Exists(Path.Combine(dir, "gateway.pfx")));
            Assert.True(File.Exists(Path.Combine(dir, "gateway.cer")));

            using var second = GatewayCertificate.LoadOrCreate(dir);
            Assert.Equal(first.Thumbprint, second.Thumbprint);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryLoadCached_returns_null_and_creates_nothing_when_no_certificate_is_cached_yet()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"certapi-gw-{Guid.NewGuid():N}");

        var result = GatewayCertificate.TryLoadCached(dir);

        Assert.Null(result);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void TryLoadCached_returns_the_certificate_loadorcreate_wrote_to_the_same_directory()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"certapi-gw-{Guid.NewGuid():N}");
        try
        {
            using var created = GatewayCertificate.LoadOrCreate(dir);
            using var cached = GatewayCertificate.TryLoadCached(dir);

            Assert.NotNull(cached);
            Assert.Equal(created.Thumbprint, cached!.Thumbprint);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
