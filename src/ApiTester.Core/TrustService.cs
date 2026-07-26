using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

/// <summary>A single pinned server certificate: this exact thumbprint is trusted for this exact
/// host, regardless of the certificate's chain of trust. Narrower than the blanket
/// <see cref="AppState.IgnoreServerCertErrors"/> bypass, which trusts anything for one request.</summary>
public sealed class TrustedServerCert
{
    public string Host { get; set; } = "";          // lowercase host, matches TokenService scoping
    public string Thumbprint { get; set; } = "";    // uppercase SHA-1, as ConnectionInfo reports it
    public string? Subject { get; set; }            // for display only
    public DateTime AddedUtc { get; set; }
}

/// <summary>Per-site server-certificate trust: a host may pin one or more thumbprints (a server
/// can present more than one certificate over its lifetime). Never throws — a bad or missing
/// host/thumbprint simply yields "not trusted" / is silently ignored on write, mirroring
/// <see cref="TokenService"/>'s shape.</summary>
public static class TrustService
{
    /// <summary>True when <paramref name="host"/> has this exact <paramref name="thumbprint"/>
    /// pinned. Both sides are compared case-insensitively.</summary>
    public static bool IsTrusted(AppState state, string host, string thumbprint)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(thumbprint)) return false;
        return state.TrustedServerCerts.Any(t =>
            t.Host.Equals(host, StringComparison.OrdinalIgnoreCase) &&
            t.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Upsert a pin: replaces any existing entry for the same host+thumbprint pair.
    /// A host may carry more than one pinned thumbprint at once — only an exact host+thumbprint
    /// match is replaced. Does nothing when host or thumbprint is null/empty.</summary>
    public static void Trust(AppState state, string host, string thumbprint, string? subject)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(thumbprint)) return;
        string normalizedHost = host.ToLowerInvariant();
        state.TrustedServerCerts.RemoveAll(t =>
            t.Host.Equals(normalizedHost, StringComparison.OrdinalIgnoreCase) &&
            t.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase));
        state.TrustedServerCerts.Add(new TrustedServerCert
        {
            Host = normalizedHost,
            Thumbprint = thumbprint,
            Subject = subject,
            AddedUtc = DateTime.UtcNow
        });
    }

    /// <summary>Convenience overload: pins the certificate's own thumbprint and subject.</summary>
    public static void Trust(AppState state, string host, X509Certificate2 serverCert) =>
        Trust(state, host, serverCert.Thumbprint, serverCert.Subject);

    /// <summary>Remove a pin. With <paramref name="thumbprint"/> null/empty, removes every pin for
    /// <paramref name="host"/>; otherwise removes only the exact host+thumbprint match.</summary>
    public static void Untrust(AppState state, string host, string? thumbprint = null)
    {
        if (string.IsNullOrEmpty(thumbprint))
            state.TrustedServerCerts.RemoveAll(t => t.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
        else
            state.TrustedServerCerts.RemoveAll(t =>
                t.Host.Equals(host, StringComparison.OrdinalIgnoreCase) &&
                t.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase));
    }
}
