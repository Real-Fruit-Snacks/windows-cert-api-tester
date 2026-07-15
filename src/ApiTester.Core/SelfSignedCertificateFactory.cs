using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

public static class SelfSignedCertificateFactory
{
    public static X509Certificate2 CreateCertificateAuthority(string name)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        // CA validity must be wider than leaf certs (+1y) so a leaf's notAfter can never exceed the issuer's,
        // avoiding the intermittent CertificateRequest.Create failure.
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        // Round-trip through PKCS#12 so the private key is usable by SslStream on Windows.
        // SChannel/SslStream cannot access ephemeral keys; use Exportable-only to create a temporary
        // non-persisted container that SChannel can use, auto-deleted on Dispose.
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.Exportable);
    }

    public static X509Certificate2 CreateSignedCertificate(
        string name, X509Certificate2 issuer, bool serverAuth, bool clientAuth,
        IEnumerable<string>? dnsNames = null)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));

        var ekus = new OidCollection();
        if (serverAuth) ekus.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        if (clientAuth) ekus.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        if (ekus.Count > 0)
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(ekus, false));

        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        if (dnsNames is not null)
        {
            var san = new SubjectAlternativeNameBuilder();
            foreach (var d in dnsNames) san.AddDnsName(d);
            req.CertificateExtensions.Add(san.Build());
        }

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);

        using var signed = req.Create(
            issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), serial);
        using var withKey = signed.CopyWithPrivateKey(rsa);
        // SChannel/SslStream cannot access ephemeral keys; use Exportable-only to create a temporary
        // non-persisted container that SChannel can use, auto-deleted on Dispose.
        return X509CertificateLoader.LoadPkcs12(
            withKey.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.Exportable);
    }
}
