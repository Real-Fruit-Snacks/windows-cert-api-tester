using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

/// <summary>Thrown when a client certificate file cannot be loaded (missing, wrong password,
/// no private key, or malformed).</summary>
public sealed class CertificateFileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Loads a client certificate (with its private key) from a file — a PKCS#12 <c>.pfx</c>/<c>.p12</c>
/// or a PEM <c>.pem</c>/<c>.crt</c> — for endpoints whose certificate isn't in the Windows store.</summary>
public static class CertificateFileLoader
{
    /// <summary>Load a usable client certificate from <paramref name="certPath"/>. For PEM inputs the
    /// private key may be in the same file or in a separate <paramref name="keyPath"/>. The result is
    /// round-tripped through PKCS#12 so SslStream/SChannel can use the key on Windows.</summary>
    public static X509Certificate2 Load(string certPath, string? password = null, string? keyPath = null)
    {
        if (string.IsNullOrWhiteSpace(certPath) || !File.Exists(certPath))
            throw new CertificateFileException($"Certificate file not found: {certPath}");
        if (keyPath is { Length: > 0 } && !File.Exists(keyPath))
            throw new CertificateFileException($"Key file not found: {keyPath}");

        string ext = Path.GetExtension(certPath).ToLowerInvariant();
        X509Certificate2 result;
        try
        {
            if (ext is ".pfx" or ".p12")
                result = X509CertificateLoader.LoadPkcs12FromFile(certPath, password, X509KeyStorageFlags.Exportable);
            else
            {
                // PEM / CRT: the cert plus its key (in the same file, or a separate key file). CreateFromPem
                // yields an ephemeral key SChannel can't use, so re-import through PKCS#12 (Exportable).
                using var pem = keyPath is { Length: > 0 }
                    ? X509Certificate2.CreateFromPemFile(certPath, keyPath)
                    : X509Certificate2.CreateFromPemFile(certPath);
                result = X509CertificateLoader.LoadPkcs12(
                    pem.Export(X509ContentType.Pkcs12), (string?)null, X509KeyStorageFlags.Exportable);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new CertificateFileException(Hint(ext, certPath, password, ex.Message), ex);
        }

        // A client certificate is useless without its private key — fail clearly rather than
        // presenting a key-less certificate that the handshake would silently reject.
        if (!result.HasPrivateKey)
        {
            result.Dispose();
            throw new CertificateFileException(Hint(ext, certPath, password,
                "the file has no usable private key"));
        }
        return result;
    }

    private static string Hint(string ext, string certPath, string? password, string message)
    {
        string baseMsg = $"Could not load certificate '{Path.GetFileName(certPath)}': {message}";
        return ext is ".pfx" or ".p12"
            ? baseMsg + (password is null ? " (a password may be required — pass one)" : "")
            : baseMsg + ". A client certificate needs its private key — use a .pfx, a PEM containing the key, or a separate key file.";
    }
}
