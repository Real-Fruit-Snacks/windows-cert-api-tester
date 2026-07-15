using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

public class SelfSignedCertificateFactoryTests
{
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

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
}
