using ApiTester.Core;

namespace ApiTester.Cli;

public static class OutputText
{
    public static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes} B" :
        bytes < 1024 * 1024 ? $"{bytes / 1024.0:F1} KB" :
        $"{bytes / (1024.0 * 1024.0):F1} MB";

    /// <summary>One stderr line: "200 OK · 118 B · 42 ms · Tls13 · client cert presented".</summary>
    public static string MetaLine(ApiResponse r)
    {
        if (r.Error is not null) return $"error [{r.Error.Kind}]: {r.Error.Message}";
        var parts = new List<string>
        {
            $"{r.StatusCode} {r.ReasonPhrase}".Trim(),
            Size(r.Body.LongLength),
            $"{r.Elapsed.TotalMilliseconds:F0} ms"
        };
        if (r.Connection?.TlsProtocol is { } tls) parts.Add(tls);
        if (r.Connection?.ClientCertificateSent == true) parts.Add("client cert presented");
        return string.Join(" · ", parts);
    }
}
