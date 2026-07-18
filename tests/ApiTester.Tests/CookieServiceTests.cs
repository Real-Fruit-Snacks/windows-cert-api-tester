using System.Net;
using ApiTester.Core;
using Xunit;

namespace ApiTester.Tests;

public class CookieServiceTests
{
    private static SessionCookie Cookie(string name, string value, string origin = "https://api.corp:443",
        bool secure = false, DateTime? expires = null) =>
        new() { Origin = origin, Name = name, Value = value, Path = "/", Domain = "api.corp",
                Secure = secure, ExpiresUtc = expires };

    [Fact]
    public void Capture_upserts_per_origin_newest_wins()
    {
        var s = new AppState();
        CookieService.Capture(s, "https://api.corp:443", new[] { Cookie("SID", "one") });
        CookieService.Capture(s, "https://api.corp:443", new[] { Cookie("SID", "two"), Cookie("X", "y") });
        Assert.Equal(2, s.SessionCookies.Count);
        Assert.DoesNotContain(s.SessionCookies, c => c.Value == "one");
    }

    [Fact]
    public void Capture_isolates_origins()
    {
        var s = new AppState();
        CookieService.Capture(s, "https://a.corp:443", new[] { Cookie("SID", "a", "https://a.corp:443") });
        CookieService.Capture(s, "https://b.corp:443", new[] { Cookie("SID", "b", "https://b.corp:443") });
        Assert.Equal(2, s.SessionCookies.Count);
    }

    [Fact]
    public void CookiesFor_returns_matching_origin_only()
    {
        var s = new AppState();
        CookieService.Capture(s, "https://api.corp:443", new[] { Cookie("SID", "a") });
        Assert.Single(CookieService.CookiesFor(s, "https://api.corp/things"));
        Assert.Empty(CookieService.CookiesFor(s, "https://other.corp/things"));
    }

    [Fact]
    public void CookiesFor_skips_expired()
    {
        var s = new AppState();
        CookieService.Capture(s, "https://api.corp:443",
            new[] { Cookie("SID", "a", expires: DateTime.UtcNow.AddMinutes(-1)) });
        Assert.Empty(CookieService.CookiesFor(s, "https://api.corp/x"));
    }

    [Fact]
    public void CookiesFor_honors_AutoCookies_off()
    {
        var s = new AppState { AutoCookies = false };
        CookieService.Capture(s, "https://api.corp:443", new[] { Cookie("SID", "a") });
        Assert.Empty(CookieService.CookiesFor(s, "https://api.corp/x"));
    }

    [Fact]
    public void SeedContainer_adds_matching_cookies_and_returns_count()
    {
        var s = new AppState();
        CookieService.Capture(s, "https://api.corp:443", new[] { Cookie("SID", "a"), Cookie("T", "b") });
        var jar = new CookieContainer();
        int added = CookieService.SeedContainer(s, "https://api.corp/things", jar);
        Assert.Equal(2, added);
        var header = jar.GetCookieHeader(new Uri("https://api.corp/things"));
        Assert.Contains("SID=a", header);
        Assert.Contains("T=b", header);
    }

    [Fact]
    public void SeedContainer_secure_cookie_not_sent_over_http()
    {
        var s = new AppState();
        CookieService.Capture(s, "http://api.corp:80", new[] { Cookie("SID", "a", "http://api.corp:80", secure: true) });
        var jar = new CookieContainer();
        CookieService.SeedContainer(s, "http://api.corp/x", jar);
        Assert.Equal("", jar.GetCookieHeader(new Uri("http://api.corp/x")));
    }
}
