using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

public sealed record CertificateInfo
{
    public required string Subject { get; init; }
    public required string Issuer { get; init; }
    public required string Thumbprint { get; init; }
    public required DateTime NotBefore { get; init; }
    public required DateTime NotAfter { get; init; }
    public required bool HasClientAuthEku { get; init; }
    public required X509Certificate2 Certificate { get; init; }

    public bool IsExpired(DateTime? now = null)
    {
        var n = now ?? DateTime.Now;
        return n < NotBefore || n > NotAfter;
    }
}
