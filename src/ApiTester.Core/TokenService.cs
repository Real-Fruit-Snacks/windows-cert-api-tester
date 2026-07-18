using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiTester.Core;

/// <summary>A bearer token captured from a response, scoped to the origin it came from.</summary>
public sealed class SessionToken
{
    public string Origin { get; set; } = "";      // scheme://host:port, host lowercase
    public string Token { get; set; } = "";
    public string Source { get; set; } = "";      // e.g. "access_token field", "X-Auth-Token header"
    public DateTime CapturedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }

    [JsonIgnore] public bool IsExpired => ExpiresUtc is { } e && DateTime.UtcNow >= e;
}

/// <summary>Detects bearer tokens in responses and scopes them to the origin they came from.
/// Detection never throws — an undetectable response simply yields null.</summary>
public static class TokenService
{
    private const int MaxScanBytes = 2 * 1024 * 1024;
    private static readonly string[] BodyFields = { "access_token", "id_token", "token", "accessToken", "jwt" };
    private static readonly string[] HeaderNames = { "X-Auth-Token", "X-Access-Token" };

    /// <summary>The token scope for a URL: scheme://host:port, host lowercase. Null when the
    /// URL is not an absolute http(s) URL.</summary>
    public static string? OriginOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
            ? $"{u.Scheme}://{u.Host.ToLowerInvariant()}:{u.Port}"
            : null;

    /// <summary>The display host for user-facing notes ("api.example.com"); the raw input when
    /// it cannot be parsed.</summary>
    public static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    /// <summary>Scan a response for a bearer token: JSON body fields first (top level, then one
    /// level under data/result), then X-Auth-Token / X-Access-Token headers.</summary>
    public static SessionToken? Detect(string url, byte[] body, string? contentType,
        IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        if (OriginOf(url) is not { } origin) return null;

        var (token, source, expires) = DetectInBody(body, contentType);
        if (token is not null)
            return new SessionToken
            {
                Origin = origin, Token = token, Source = source!,
                CapturedUtc = DateTime.UtcNow, ExpiresUtc = expires
            };

        foreach (var name in HeaderNames)
            foreach (var h in headers)
            {
                var value = h.Value.Trim();
                if (h.Key.Equals(name, StringComparison.OrdinalIgnoreCase) && IsTokenShaped(value))
                    return new SessionToken
                    {
                        Origin = origin, Token = value, Source = $"{name} header",
                        CapturedUtc = DateTime.UtcNow
                    };
            }
        return null;
    }

    /// <summary>Mask a token for display: first and last 4 characters around an ellipsis;
    /// short tokens are fully hidden.</summary>
    public static string Mask(string token) =>
        token.Length <= 12 ? new string('•', token.Length) : $"{token[..4]}…{token[^4..]}";

    /// <summary>Render an Authorization header value safely for logs: "Bearer eyJh…f3Qk".</summary>
    public static string MaskAuthorization(string value)
    {
        int sp = value.IndexOf(' ');
        return sp > 0 ? value[..sp] + " " + Mask(value[(sp + 1)..].Trim()) : Mask(value);
    }

    private static (string? Token, string? Source, DateTime? ExpiresUtc) DetectInBody(byte[] body, string? contentType)
    {
        if (body.Length == 0 || body.Length > MaxScanBytes) return default;
        if (contentType is not null && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return default;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return default; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return default;
            if (ScanObject(doc.RootElement) is { } hit) return hit;
            foreach (var nested in new[] { "data", "result" })
                if (TryGetIgnoreCase(doc.RootElement, nested, out var el) && el.ValueKind == JsonValueKind.Object &&
                    ScanObject(el) is { } nestedHit)
                    return nestedHit;
            return default;
        }
    }

    private static (string, string, DateTime?)? ScanObject(JsonElement obj)
    {
        // A non-Bearer token_type (e.g. "mac") disqualifies this object's token.
        if (TryGetIgnoreCase(obj, "token_type", out var tt) && tt.ValueKind == JsonValueKind.String &&
            !tt.GetString()!.Equals("bearer", StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (var field in BodyFields)
        {
            if (!TryGetIgnoreCase(obj, field, out var el) || el.ValueKind != JsonValueKind.String) continue;
            var value = el.GetString()!.Trim();
            if (!IsTokenShaped(value)) continue;
            return (value, $"{field} field", ExpiryOf(obj));
        }
        return null;
    }

    private static DateTime? ExpiryOf(JsonElement obj)
    {
        if (!TryGetIgnoreCase(obj, "expires_in", out var el)) return null;
        double seconds = el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(el.GetString(), out var d) => d,
            _ => 0
        };
        return seconds > 0 ? DateTime.UtcNow.AddSeconds(Math.Min(seconds, 315_360_000)) : null;   // 10-year cap
    }

    private static bool TryGetIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    /// <summary>Header-safe: non-empty, no whitespace or control characters.</summary>
    private static bool IsTokenShaped(string? value) =>
        !string.IsNullOrEmpty(value) && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}
