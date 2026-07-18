using System.IO;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

public class CertificateFileLoaderTests
{
    private static X509Certificate2 MakeClientCert()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        return SelfSignedCertificateFactory.CreateSignedCertificate("FileClient", ca, false, true);
    }

    private static string Temp(string ext) => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ext);

    [Fact]
    public void Loads_a_pfx_with_a_password()
    {
        using var cert = MakeClientCert();
        var pfx = Temp(".pfx");
        File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pkcs12, "s3cret"));
        try
        {
            using var loaded = CertificateFileLoader.Load(pfx, "s3cret");
            Assert.True(loaded.HasPrivateKey);
            Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
        }
        finally { File.Delete(pfx); }
    }

    [Fact]
    public void Loads_a_passwordless_pfx()
    {
        using var cert = MakeClientCert();
        var pfx = Temp(".pfx");
        File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pkcs12));
        try
        {
            using var loaded = CertificateFileLoader.Load(pfx);
            Assert.True(loaded.HasPrivateKey);
            Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
        }
        finally { File.Delete(pfx); }
    }

    [Fact]
    public void Loads_a_pem_with_cert_and_key_in_one_file()
    {
        using var cert = MakeClientCert();
        var pem = Temp(".pem");
        using (var rsa = cert.GetRSAPrivateKey()!)
            File.WriteAllText(pem, cert.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem());
        try
        {
            using var loaded = CertificateFileLoader.Load(pem);
            Assert.True(loaded.HasPrivateKey);
            Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
        }
        finally { File.Delete(pem); }
    }

    [Fact]
    public void Loads_a_pem_cert_with_a_separate_key_file()
    {
        using var cert = MakeClientCert();
        var crt = Temp(".crt");
        var key = Temp(".key");
        File.WriteAllText(crt, cert.ExportCertificatePem());
        using (var rsa = cert.GetRSAPrivateKey()!)
            File.WriteAllText(key, rsa.ExportPkcs8PrivateKeyPem());
        try
        {
            using var loaded = CertificateFileLoader.Load(crt, password: null, keyPath: key);
            Assert.True(loaded.HasPrivateKey);
            Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
        }
        finally { File.Delete(crt); File.Delete(key); }
    }

    [Fact]
    public void Missing_file_throws_a_clear_error()
    {
        var ex = Assert.Throws<CertificateFileException>(() => CertificateFileLoader.Load("C:\\no\\such.pfx"));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void Wrong_pfx_password_throws_a_clear_error()
    {
        using var cert = MakeClientCert();
        var pfx = Temp(".pfx");
        File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pkcs12, "right"));
        try
        {
            Assert.Throws<CertificateFileException>(() => CertificateFileLoader.Load(pfx, "wrong"));
        }
        finally { File.Delete(pfx); }
    }

    [Fact]
    public void Pem_without_a_private_key_throws_a_clear_error()
    {
        using var cert = MakeClientCert();
        var crt = Temp(".crt");
        File.WriteAllText(crt, cert.ExportCertificatePem());   // cert only, no key
        try
        {
            var ex = Assert.Throws<CertificateFileException>(() => CertificateFileLoader.Load(crt));
            Assert.Contains("private key", ex.Message);
        }
        finally { File.Delete(crt); }
    }
}
