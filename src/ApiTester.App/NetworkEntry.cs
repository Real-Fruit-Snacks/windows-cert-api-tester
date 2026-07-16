using System;
using System.Collections.Generic;

namespace ApiTester.App;

/// <summary>One row in the Network trace — a single HTTP call (a request you sent, or a resource
/// the Rendered view fetched). Metadata only; response bodies are not kept.</summary>
public sealed class NetworkEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Method { get; init; } = "GET";
    public string Url { get; init; } = "";
    public int? StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    public string? ContentType { get; init; }
    public long Size { get; init; }
    public double ElapsedMs { get; init; }
    public string? ClientCertSubject { get; init; }
    public bool ClientCertPresented { get; init; }
    public string? Error { get; init; }
    public string Source { get; init; } = "Request";  // "Request" or "Rendered"
    public List<KeyValuePair<string, string>> RequestHeaders { get; init; } = new();
    public List<KeyValuePair<string, string>> ResponseHeaders { get; init; } = new();

    public bool UsedClientCert => !string.IsNullOrEmpty(ClientCertSubject);

    public string StatusLabel => Error is not null ? "ERR" : StatusCode?.ToString() ?? "—";

    public string SizeLabel =>
        Error is not null ? "" :
        Size < 1024 ? $"{Size} B" :
        Size < 1024 * 1024 ? $"{Size / 1024.0:F1} KB" :
        $"{Size / (1024.0 * 1024.0):F1} MB";

    public string TimeLabel => Error is not null ? "" : $"{ElapsedMs:F0} ms";

    /// <summary>Short content-type for the table (the subtype, e.g. "json", "html", "css").</summary>
    public string ShortType
    {
        get
        {
            var ct = ContentType;
            if (string.IsNullOrEmpty(ct)) return Error is not null ? "—" : "";
            int semi = ct.IndexOf(';');
            if (semi >= 0) ct = ct[..semi];
            int slash = ct.IndexOf('/');
            return (slash >= 0 ? ct[(slash + 1)..] : ct).Trim();
        }
    }
}
