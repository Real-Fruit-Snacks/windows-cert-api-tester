using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

public class TokenServiceTests
{
    private static readonly List<KeyValuePair<string, string>> NoHeaders = new();

    private static SessionToken? Detect(string json, string? contentType = "application/json",
        List<KeyValuePair<string, string>>? headers = null, string url = "https://api.example.com/login") =>
        TokenService.Detect(url, Encoding.UTF8.GetBytes(json), contentType, headers ?? NoHeaders);

    [Fact]
    public void Origin_is_scheme_host_port_lowercase()
    {
        Assert.Equal("https://api.example.com:443", TokenService.OriginOf("https://API.Example.com/login?x=1"));
        Assert.Equal("https://api.example.com:8443", TokenService.OriginOf("https://api.example.com:8443/"));
        Assert.Equal("http://localhost:5000", TokenService.OriginOf("http://localhost:5000/a"));
        Assert.Null(TokenService.OriginOf("not a url"));
        Assert.Null(TokenService.OriginOf("ftp://example.com/x"));
    }

    [Theory]
    [InlineData("{\"access_token\":\"tok-1\"}", "tok-1", "access_token field")]
    [InlineData("{\"id_token\":\"tok-2\"}", "tok-2", "id_token field")]
    [InlineData("{\"token\":\"tok-3\"}", "tok-3", "token field")]
    [InlineData("{\"accessToken\":\"tok-4\"}", "tok-4", "accessToken field")]
    [InlineData("{\"jwt\":\"tok-5\"}", "tok-5", "jwt field")]
    [InlineData("{\"Access_Token\":\"tok-6\"}", "tok-6", "access_token field")]   // case-insensitive
    public void Detects_each_body_field(string json, string token, string source)
    {
        var t = Detect(json);
        Assert.NotNull(t);
        Assert.Equal(token, t!.Token);
        Assert.Equal(source, t.Source);
        Assert.Equal("https://api.example.com:443", t.Origin);
    }

    [Fact]
    public void Access_token_wins_over_other_fields()
    {
        var t = Detect("{\"token\":\"b\",\"access_token\":\"a\"}");
        Assert.Equal("a", t!.Token);
    }

    [Theory]
    [InlineData("{\"data\":{\"access_token\":\"nested\"}}")]
    [InlineData("{\"result\":{\"token\":\"nested\"}}")]
    public void Detects_one_level_under_data_or_result(string json) =>
        Assert.Equal("nested", Detect(json)!.Token);

    [Fact]
    public void Expires_in_sets_expiry()
    {
        var t = Detect("{\"access_token\":\"a\",\"expires_in\":3600}");
        Assert.NotNull(t!.ExpiresUtc);
        Assert.InRange((t.ExpiresUtc!.Value - DateTime.UtcNow).TotalMinutes, 58, 61);
        Assert.False(t.IsExpired);

        var s = Detect("{\"access_token\":\"a\",\"expires_in\":\"1800\"}");   // numeric string
        Assert.InRange((s!.ExpiresUtc!.Value - DateTime.UtcNow).TotalMinutes, 28, 31);
    }

    [Fact]
    public void Non_bearer_token_type_disqualifies()
    {
        Assert.Null(Detect("{\"access_token\":\"a\",\"token_type\":\"mac\"}"));
        Assert.NotNull(Detect("{\"access_token\":\"a\",\"token_type\":\"Bearer\"}"));
        Assert.NotNull(Detect("{\"access_token\":\"a\",\"token_type\":\"bearer\"}"));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"access_token\":42}")]
    [InlineData("{\"access_token\":\"\"}")]
    [InlineData("{\"access_token\":\"has space\"}")]
    [InlineData("{\"access_token\":\"has\\nnewline\"}")]
    [InlineData("{}")]
    public void Rejects_malformed_or_unsafe_values(string json) => Assert.Null(Detect(json));

    [Fact]
    public void Skips_non_json_content_types_and_huge_bodies()
    {
        Assert.Null(Detect("{\"access_token\":\"a\"}", contentType: "text/html"));
        Assert.NotNull(Detect("{\"access_token\":\"a\"}", contentType: null));   // unknown type: try anyway
        var huge = new byte[2 * 1024 * 1024 + 1];
        Assert.Null(TokenService.Detect("https://api.example.com/x", huge, "application/json", NoHeaders));
    }

    [Fact]
    public void Detects_header_tokens_with_body_taking_precedence()
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("x-auth-token", "hdr-tok")
        };
        var t = Detect("{}", headers: headers);
        Assert.Equal("hdr-tok", t!.Token);
        Assert.Equal("X-Auth-Token header", t.Source);

        var both = Detect("{\"access_token\":\"body-tok\"}", headers: headers);
        Assert.Equal("body-tok", both!.Token);

        var second = Detect("{}", headers: new() { new("X-Access-Token", "acc") });
        Assert.Equal("X-Access-Token header", second!.Source);
    }

    [Fact]
    public void Unparseable_url_yields_nothing() =>
        Assert.Null(Detect("{\"access_token\":\"a\"}", url: "::bad::"));

    [Fact]
    public void Mask_hides_the_middle()
    {
        Assert.Equal("eyJh…f3Qk", TokenService.Mask("eyJhbbbbbbbbbbbbbbbbf3Qk"));
        Assert.Equal("••••••", TokenService.Mask("secret"));                  // short: fully hidden
        Assert.Equal("Bearer eyJh…f3Qk", TokenService.MaskAuthorization("Bearer eyJhbbbbbbbbbbbbbbbbf3Qk"));
        Assert.Equal("Basic ••••••", TokenService.MaskAuthorization("Basic secret"));
    }

    [Fact]
    public void HostOf_extracts_the_display_host()
    {
        Assert.Equal("api.example.com", TokenService.HostOf("https://api.example.com:8443/login"));
        Assert.Equal("::bad::", TokenService.HostOf("::bad::"));
    }
}
