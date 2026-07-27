using System.Security.Cryptography.X509Certificates;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

public class SelfSignedCertificateFactoryTests
{
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string CrlDistributionPointOid = "2.5.29.31";

    [Fact]
    public void Ca_has_private_key_and_is_ca()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Test CA");
        Assert.True(ca.HasPrivateKey);
        var bc = ca.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(bc.CertificateAuthority);
    }

    [Fact]
    public void Client_leaf_has_key_and_client_auth_eku()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Test CA");
        using var leaf = SelfSignedCertificateFactory.CreateSignedCertificate(
            "Client", ca, serverAuth: false, clientAuth: true);

        Assert.True(leaf.HasPrivateKey);
        var eku = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(),
            o => o.Value == ClientAuthOid);
    }

    [Fact]
    public void Leaf_chains_to_ca()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Test CA");
        using var leaf = SelfSignedCertificateFactory.CreateSignedCertificate(
            "Client", ca, serverAuth: false, clientAuth: true);
        Assert.Equal(ca.Subject, leaf.Issuer);
    }

    [Fact]
    public void Leaf_without_crl_parameter_carries_no_crl_distribution_point_extension()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Test CA");
        using var leaf = SelfSignedCertificateFactory.CreateSignedCertificate(
            "Server", ca, serverAuth: true, clientAuth: false);

        Assert.DoesNotContain(leaf.Extensions.Cast<X509Extension>(),
            e => e.Oid?.Value == CrlDistributionPointOid);
    }

    [Fact]
    public void Leaf_with_unroutable_crl_distribution_point_carries_crl_extension_naming_the_url()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Test CA");
        using var leaf = SelfSignedCertificateFactory.CreateSignedCertificate(
            "Server", ca, serverAuth: true, clientAuth: false,
            crlDistributionPoint: SelfSignedCertificateFactory.UnroutableCrlDistributionPoint);

        var crlExtension = leaf.Extensions.Cast<X509Extension>()
            .Single(e => e.Oid?.Value == CrlDistributionPointOid);

        var urlBytes = Encoding.ASCII.GetBytes(SelfSignedCertificateFactory.UnroutableCrlDistributionPoint);
        Assert.True(ContainsSubsequence(crlExtension.RawData, urlBytes),
            "Expected the CRL distribution point extension's raw data to contain the configured URL's bytes.");
    }

    [Fact]
    public void Leaf_with_crl_distribution_point_is_otherwise_unchanged()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Test CA");
        using var leaf = SelfSignedCertificateFactory.CreateSignedCertificate(
            "Server", ca, serverAuth: true, clientAuth: false,
            crlDistributionPoint: SelfSignedCertificateFactory.UnroutableCrlDistributionPoint);

        Assert.True(leaf.HasPrivateKey);
        var eku = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(),
            o => o.Value == ServerAuthOid);
        Assert.Equal(ca.Subject, leaf.Issuer);
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return true;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }
}
