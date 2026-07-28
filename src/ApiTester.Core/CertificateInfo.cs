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

    /// <summary>How many days from now this certificate stops being valid — negative once it has
    /// expired, and never rounded up: 23 hours away is 0 days, because "expires in 1 day" would
    /// overstate the time left on the one day it matters most.</summary>
    public int DaysUntilExpiry(DateTime? now = null) =>
        (int)Math.Floor((NotAfter - (now ?? DateTime.Now)).TotalDays);

    /// <summary>The window in which a certificate is worth warning about before it stops working.
    /// Two weeks is enough notice to get a corporate renewal through a ticket queue, which is the
    /// process this warning exists to start.</summary>
    public const int ExpiryWarningDays = 14;

    /// <summary>True when this certificate still works but is inside
    /// <see cref="ExpiryWarningDays"/> of not working. False once it actually has expired — that is
    /// a different, louder message, and this must not soften it.</summary>
    public bool IsExpiringSoon(DateTime? now = null)
    {
        var n = now ?? DateTime.Now;
        if (IsExpired(n)) return false;
        return DaysUntilExpiry(n) <= ExpiryWarningDays;
    }

    /// <summary>The one wording for both cases, so every caller — the command line, the desktop
    /// certificate row, and anything added later — says the same thing about the same certificate.
    /// Null when there is nothing worth saying.</summary>
    public string? ExpiryWarning(DateTime? now = null)
    {
        var n = now ?? DateTime.Now;
        if (IsExpired(n))
            return n < NotBefore
                ? $"certificate '{Subject}' is not valid yet (not before {NotBefore:yyyy-MM-dd})."
                : $"certificate '{Subject}' is expired (not after {NotAfter:yyyy-MM-dd}).";
        if (!IsExpiringSoon(n)) return null;

        int days = DaysUntilExpiry(n);
        string when = days switch
        {
            <= 0 => "today",
            1 => "tomorrow",
            _ => $"in {days} days"
        };
        return $"certificate '{Subject}' expires {when} (not after {NotAfter:yyyy-MM-dd}).";
    }
}
