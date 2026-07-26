using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

/// <summary>The TLS certificate `certapi serve --tls` presents on loopback: CN=127.0.0.1 with a
/// Subject Alternative Name (SAN) covering 127.0.0.1 and localhost, cached under
/// %AppData%\CertApiTester\gateway-tls\ and reused across runs — a fresh certificate every run would
/// retrain the browser's trust prompt every time.</summary>
public static class GatewayCertificate
{
    private const string PfxFileName = "gateway.pfx";
    private const string CerFileName = "gateway.cer";

    /// <summary>%AppData%\CertApiTester\gateway-tls</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "CertApiTester", "gateway-tls");

    /// <summary>The cached certificate, generated and cached on first use, and regenerated when the
    /// cached one is missing, unreadable, or within 7 days of expiry.</summary>
    public static X509Certificate2 LoadOrCreate(string? directory = null)
    {
        string dir = directory ?? DefaultDirectory;
        Directory.CreateDirectory(dir);
        string pfxPath = Path.Combine(dir, PfxFileName);

        if (File.Exists(pfxPath))
        {
            try
            {
                var cached = X509CertificateLoader.LoadPkcs12(
                    File.ReadAllBytes(pfxPath), (string?)null, X509KeyStorageFlags.Exportable);
                if (cached.NotAfter > DateTime.Now.AddDays(7)) return cached;
                cached.Dispose();
            }
            catch
            {
                // Missing, unreadable, or corrupt — fall through and regenerate.
            }
        }

        var cert = Create();
        File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx));
        File.WriteAllBytes(Path.Combine(dir, CerFileName), cert.Export(X509ContentType.Cert));
        return cert;
    }

    /// <summary>The cached gateway certificate when one exists and loads, else null. Never generates
    /// and never writes — the read-only counterpart to <see cref="LoadOrCreate"/>.</summary>
    public static X509Certificate2? TryLoadCached(string? directory = null)
    {
        string dir = directory ?? DefaultDirectory;
        string pfxPath = Path.Combine(dir, PfxFileName);
        if (!File.Exists(pfxPath)) return null;
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                File.ReadAllBytes(pfxPath), (string?)null, X509KeyStorageFlags.Exportable);
        }
        catch
        {
            // Missing, unreadable, or corrupt — nothing cached to return.
            return null;
        }
    }

    /// <summary>A fresh gateway certificate, not cached — no file is written.</summary>
    public static X509Certificate2 Create() =>
        SelfSignedCertificateFactory.CreateSelfSignedServerCertificate(
            "127.0.0.1", new[] { "localhost" }, new[] { IPAddress.Loopback });

    /// <summary>Install into CurrentUser\Root. Only ever called under the explicit --tls-trust flag.</summary>
    public static void Trust(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
    }

    /// <summary>Remove a certificate with this thumbprint from CurrentUser\Root; true when one was
    /// removed. Only ever called under the explicit --tls-untrust flag.</summary>
    public static bool Untrust(string thumbprint)
    {
        string normalized = LoopbackTlsBinding.NormalizeThumbprint(thumbprint);
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
        if (found.Count == 0) return false;
        store.RemoveRange(found);
        return true;
    }

    /// <summary>Whether a certificate with this thumbprint is present in CurrentUser\Root.</summary>
    public static bool IsTrusted(string thumbprint)
    {
        string normalized = LoopbackTlsBinding.NormalizeThumbprint(thumbprint);
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false).Count > 0;
    }
}
