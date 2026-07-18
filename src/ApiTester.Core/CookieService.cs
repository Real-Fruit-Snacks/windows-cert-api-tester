using System.Net;
using System.Text.Json.Serialization;

namespace ApiTester.Core;

/// <summary>A cookie captured from a browser login session, scoped to the origin it belongs to.</summary>
public sealed class SessionCookie
{
    public string Origin { get; set; } = "";   // scheme://host:port, host lowercase (matches token scoping)
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Path { get; set; } = "/";
    public string Domain { get; set; } = "";
    public DateTime? ExpiresUtc { get; set; }   // null = session cookie
    public bool Secure { get; set; }
    public bool HttpOnly { get; set; }

    [JsonIgnore] public bool IsExpired => ExpiresUtc is { } e && DateTime.UtcNow >= e;
}

/// <summary>Stores browser-captured cookies per origin and replays them on later requests.
/// Mirrors <see cref="TokenService"/>: capture is origin-scoped, attach honors a global switch,
/// and nothing here throws on malformed input.</summary>
public static class CookieService
{
    /// <summary>Replace all stored cookies for <paramref name="origin"/> with the given set
    /// (newest login wins).</summary>
    public static void Capture(AppState state, string origin, IEnumerable<SessionCookie> cookies)
    {
        state.SessionCookies.RemoveAll(c => c.Origin == origin);
        foreach (var c in cookies)
        {
            c.Origin = origin;
            state.SessionCookies.Add(c);
        }
    }

    /// <summary>Live (unexpired) cookies for the URL's origin, honoring the global switch.</summary>
    public static IReadOnlyList<SessionCookie> CookiesFor(AppState state, string url)
    {
        if (!state.AutoCookies || TokenService.OriginOf(url) is not { } origin)
            return Array.Empty<SessionCookie>();
        return state.SessionCookies.Where(c => c.Origin == origin && !c.IsExpired).ToList();
    }

    /// <summary>Add matching live cookies to <paramref name="jar"/> for the URL's origin.
    /// Returns how many were added. A <see cref="CookieContainer"/> enforces Secure/path/domain
    /// rules itself when the request goes out.</summary>
    public static int SeedContainer(AppState state, string url, CookieContainer jar)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return 0;
        int added = 0;
        foreach (var c in CookiesFor(state, url))
        {
            try
            {
                jar.Add(uri, new Cookie(c.Name, c.Value, string.IsNullOrEmpty(c.Path) ? "/" : c.Path)
                {
                    Secure = c.Secure,
                    HttpOnly = c.HttpOnly
                });
                added++;
            }
            catch (CookieException) { /* malformed name/value — skip */ }
        }
        return added;
    }
}
