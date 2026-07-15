using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

public class CertificateStoreServiceTests
{
    [Fact]
    public void Includes_client_auth_cert_and_flags_eku()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var client = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        var infos = CertificateStoreService.BuildCertificateInfos(new X509Certificate2Collection(client));

        var info = Assert.Single(infos);
        Assert.Equal(client.Thumbprint, info.Thumbprint);
        Assert.True(info.HasClientAuthEku);
        Assert.False(info.IsExpired());
    }

    [Fact]
    public void Skips_certs_without_private_key()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var client = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        // Public-only copy has no private key.
        using var publicOnly = X509CertificateLoader.LoadCertificate(client.Export(X509ContentType.Cert));

        var infos = CertificateStoreService.BuildCertificateInfos(new X509Certificate2Collection(publicOnly));

        Assert.Empty(infos);
    }

    [Fact]
    public void Server_only_cert_has_no_client_auth_flag()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var server = SelfSignedCertificateFactory.CreateSignedCertificate("srv", ca, true, false, new[] { "localhost" });

        var infos = CertificateStoreService.BuildCertificateInfos(new X509Certificate2Collection(server));

        Assert.False(Assert.Single(infos).HasClientAuthEku);
    }
}
