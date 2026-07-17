using ApiTester.Core;

namespace ApiTester.Cli;

public static class CertPicker
{
    public static CertificateInfo Resolve(IReadOnlyList<CertificateInfo> certs, string query, TextWriter stderr)
    {
        string normalized = Normalize(query);
        var byThumb = certs.Where(c => Normalize(c.Thumbprint) == normalized).ToList();
        var hits = byThumb.Count > 0
            ? byThumb
            : certs.Where(c => c.Subject.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        if (hits.Count == 0)
            throw new CliDataException(
                $"No client certificate matches '{query}'. Run 'certapi certs' to list what's available.");
        if (hits.Count > 1)
            throw new CliDataException(
                $"'{query}' matches {hits.Count} certificates – be more specific:\n" +
                string.Join("\n", hits.Select(h => $"  {h.Subject}  {h.Thumbprint}")));

        var hit = hits[0];
        if (hit.IsExpired())
            stderr.WriteLine($"warning: certificate '{hit.Subject}' is expired (not after {hit.NotAfter:yyyy-MM-dd}).");
        return hit;
    }

    private static string Normalize(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
