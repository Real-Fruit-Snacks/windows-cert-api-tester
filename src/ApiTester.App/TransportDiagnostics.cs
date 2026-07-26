using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>The response-panel text and Network-trace rows derived from a response's transport
/// facts. Pure functions, so the parts worth testing are not trapped inside the window.</summary>
public static class TransportDiagnostics
{
    /// <summary>The Diagnostics view's text for a response.</summary>
    public static string Format(ApiResponse response)
    {
        if (response.Connection is null && response.Redirects.Count == 0 && response.Error is null)
            return "No connection details available.";

        var sb = new StringBuilder();
        if (response.Connection is { } c) AppendConnection(sb, c);
        // A failure that never completed a handshake has no connection to describe — but its error
        // chain is the whole point of the panel, so the blocks below still run.
        else sb.AppendLine("No connection details available.");
        AppendRedirects(sb, response);
        AppendError(sb, response);
        return sb.ToString();
    }

    private static void AppendConnection(StringBuilder sb, ConnectionInfo c)
    {
        sb.AppendLine("CONNECTION");
        sb.AppendLine($"  Via proxy       : {(c.ViaProxy ? "yes" : "no")}");
        // Naming the proxy is what lets a user work out where a client certificate got stripped.
        if (!string.IsNullOrEmpty(c.ProxyUri)) sb.AppendLine($"  Proxy           : {c.ProxyUri}");
        sb.AppendLine($"  TLS protocol    : {c.TlsProtocol ?? "—"}");
        sb.AppendLine($"  Cipher suite    : {c.CipherSuite ?? "—"}");
        sb.AppendLine();
        sb.AppendLine("CLIENT CERTIFICATE");
        sb.AppendLine($"  Offered         : {c.ClientCertificateSubject ?? "none"}");
        string presented = c.ClientCertificateSubject is null ? "n/a"
            : c.ClientCertificateSent ? "yes — presented to the server"
            : c.ViaProxy ? "unknown (connection went through a proxy)"
            : "no — the server did not request a client certificate";
        sb.AppendLine($"  Presented       : {presented}");
        sb.AppendLine();
        sb.AppendLine("SERVER CERTIFICATE");
        sb.AppendLine($"  Subject         : {c.ServerCertificateSubject ?? "—"}");
        sb.AppendLine($"  Issuer          : {c.ServerCertificateIssuer ?? "—"}");
        sb.AppendLine($"  Thumbprint      : {c.ServerCertificateThumbprint ?? "—"}");
        sb.AppendLine($"  Expires         : {c.ServerCertificateNotAfter?.ToString("u") ?? "—"}");
        if (c.ServerCertificateChain.Count > 0)
        {
            sb.AppendLine("  Chain           :");
            foreach (var s in c.ServerCertificateChain) sb.AppendLine($"    • {s}");
        }
    }

    /// <summary>One Network-trace row per redirect hop, oldest first — each hop genuinely was a
    /// request, so it belongs in the trace next to the final one. The hop's destination and its
    /// authorization/scheme facts ride on the reason phrase, which the details pane shows beside
    /// the status.</summary>
    public static IReadOnlyList<NetworkEntry> RedirectEntries(ApiRequest request, ApiResponse response)
    {
        var entries = new List<NetworkEntry>();
        if (response.Redirects.Count == 0) return entries;

        var headers = (request.Headers ?? Array.Empty<KeyValuePair<string, string>>())
            .Select(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                ? new KeyValuePair<string, string>(h.Key, TokenService.MaskAuthorization(h.Value))
                : h).ToList();

        foreach (var h in response.Redirects)
        {
            var facts = HopFacts(h);
            entries.Add(new NetworkEntry
            {
                Method = request.Method.Method,
                Url = h.From,
                StatusCode = h.StatusCode,
                ReasonPhrase = $"→ {h.To}" + (facts is null ? "" : "  — " + facts),
                Source = "Request",
                RequestHeaders = new List<KeyValuePair<string, string>>(headers)
            });
        }
        return entries;
    }

    /// <summary>The hops followed to reach the response, oldest first. A hop that dropped the
    /// Authorization header or downgraded the scheme says so: both mean the request finished
    /// somewhere the user did not name.</summary>
    private static void AppendRedirects(StringBuilder sb, ApiResponse response)
    {
        if (response.Redirects.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine("REDIRECTS");
        foreach (var h in response.Redirects)
        {
            var facts = HopFacts(h);
            sb.AppendLine($"  {h.StatusCode}  {h.From} → {h.To}{(facts is null ? "" : "  — " + facts)}");
        }
    }

    /// <summary>The failure, including the flattened InnerException chain — for a refused client
    /// certificate that chain is where the AuthenticationException and the SChannel status code live,
    /// and none of it appears in the outermost message.</summary>
    private static void AppendError(StringBuilder sb, ApiResponse response)
    {
        if (response.Error is not { } e) return;
        sb.AppendLine();
        sb.AppendLine("ERROR");
        sb.AppendLine($"  Kind            : {e.Kind}");
        sb.AppendLine($"  Message         : {e.Message}");
        if (e.SocketErrorCode is { } se) sb.AppendLine($"  Socket error    : {se}");
        if (e.Detail is { Count: > 0 } detail)
        {
            sb.AppendLine("  Detail          :");
            foreach (var d in detail)
            {
                var native = d.NativeErrorCode is { } n ? $" (native error {n} / 0x{n:X8})" : "";
                sb.AppendLine($"    • {d.ExceptionType}: {d.Message}{native}");
            }
        }
    }

    /// <summary>The two mutual-TLS-relevant facts about a hop, or null when it was unremarkable.</summary>
    private static string? HopFacts(RedirectHop h)
    {
        if (h.AuthorizationDropped && h.SchemeDowngrade) return "authorization dropped, scheme downgrade";
        if (h.AuthorizationDropped) return "authorization dropped";
        if (h.SchemeDowngrade) return "scheme downgrade";
        return null;
    }
}
