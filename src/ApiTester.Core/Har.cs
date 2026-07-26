using System.Text.Json.Serialization;

namespace ApiTester.Core;

/// <summary>The root of an HTTP Archive (HAR) 1.2 document — <c>{ "log": { … } }</c>. Models the
/// subset the tool produces and consumes: capture (GUI + CLI), replay (import + run), and (later)
/// mock-from-HAR and HAR-to-OpenAPI.</summary>
public sealed class Har
{
    public HarLog Log { get; set; } = new();
}

public sealed class HarLog
{
    public string Version { get; set; } = "1.2";
    public HarCreator Creator { get; set; } = new("certapi", "");
    public List<HarEntry> Entries { get; set; } = new();
}

public sealed record HarCreator(string Name, string Version);

public sealed class HarEntry
{
    public DateTimeOffset StartedDateTime { get; set; }

    /// <summary>Total time for the request, in milliseconds.</summary>
    public double Time { get; set; }

    public HarRequest Request { get; set; } = new();
    public HarResponse Response { get; set; } = new();
    public HarTimings Timings { get; set; } = new();

    /// <summary>certapi's own extension block — the mTLS facts a browser's HAR can't carry.
    /// Serialized as <c>_certapi</c>, the HAR convention for a tool-specific extension namespace.</summary>
    [JsonPropertyName("_certapi")]
    public HarCertapi? Certapi { get; set; }
}

public sealed class HarRequest
{
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public string HttpVersion { get; set; } = "HTTP/1.1";
    public List<HarNameValue> Headers { get; set; } = new();
    public List<HarNameValue> QueryString { get; set; } = new();
    public HarPostData? PostData { get; set; }
}

public sealed class HarResponse
{
    public int Status { get; set; }
    public string StatusText { get; set; } = "";
    public string HttpVersion { get; set; } = "HTTP/1.1";
    public List<HarNameValue> Headers { get; set; } = new();
    public HarContent Content { get; set; } = new();

    /// <summary>The Location header value for a 3xx response, else "".</summary>
    public string RedirectUrl { get; set; } = "";
}

public sealed record HarNameValue(string Name, string Value);

public sealed class HarPostData
{
    public string MimeType = "";
    public string Text = "";
}

public sealed class HarContent
{
    public long Size { get; set; }
    public string MimeType { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>"base64" for a binary body, else null.</summary>
    public string? Encoding { get; set; }
}

public sealed class HarTimings
{
    public double Send;
    public double Wait;
    public double Receive;
}

/// <summary>certapi's own extension block on each entry — the mTLS facts a browser's HAR can't carry.</summary>
public sealed class HarCertapi
{
    public bool ClientCertificateSent { get; set; }
    public string? ClientCertificateSubject { get; set; }
    public string? TlsProtocol { get; set; }
    public string? ServerCertificateThumbprint { get; set; }
    public bool ViaProxy { get; set; }
}

/// <summary>A HAR file that is malformed or does not describe a HAR archive.</summary>
public sealed class HarFormatException : Exception
{
    public HarFormatException(string message) : base(message) { }
    public HarFormatException(string message, Exception innerException) : base(message, innerException) { }
}
