using ApiTester.App;

namespace ApiTester.Tests;

public class NetworkEntryTests
{
    [Fact]
    public void SizeLabel_scales_units()
    {
        Assert.Equal("500 B", new NetworkEntry { Size = 500 }.SizeLabel);
        Assert.Equal("2.0 KB", new NetworkEntry { Size = 2048 }.SizeLabel);
        Assert.Equal("1.5 MB", new NetworkEntry { Size = (long)(1.5 * 1024 * 1024) }.SizeLabel);
    }

    [Fact]
    public void TimeLabel_rounds_to_whole_ms()
    {
        Assert.Equal("163 ms", new NetworkEntry { ElapsedMs = 163.4 }.TimeLabel);
    }

    [Fact]
    public void StatusLabel_reflects_code_or_error()
    {
        Assert.Equal("200", new NetworkEntry { StatusCode = 200 }.StatusLabel);
        Assert.Equal("ERR", new NetworkEntry { Error = "boom" }.StatusLabel);
        Assert.Equal("—", new NetworkEntry().StatusLabel);
    }

    [Fact]
    public void ShortType_is_the_subtype()
    {
        Assert.Equal("json", new NetworkEntry { ContentType = "application/json; charset=utf-8" }.ShortType);
        Assert.Equal("html", new NetworkEntry { ContentType = "text/html" }.ShortType);
        Assert.Equal("", new NetworkEntry().ShortType);
    }

    [Fact]
    public void UsedClientCert_tracks_the_subject()
    {
        Assert.True(new NetworkEntry { ClientCertSubject = "CN=me" }.UsedClientCert);
        Assert.False(new NetworkEntry().UsedClientCert);
    }

    [Fact]
    public void StatusClass_buckets_by_hundreds()
    {
        Assert.Equal("2xx", new NetworkEntry { StatusCode = 204 }.StatusClass);
        Assert.Equal("3xx", new NetworkEntry { StatusCode = 302 }.StatusClass);
        Assert.Equal("4xx", new NetworkEntry { StatusCode = 404 }.StatusClass);
        Assert.Equal("5xx", new NetworkEntry { StatusCode = 503 }.StatusClass);
        Assert.Equal("ERR", new NetworkEntry { Error = "boom" }.StatusClass);
        Assert.Equal("", new NetworkEntry().StatusClass);
    }

    [Fact]
    public void Matches_filters_by_status_class()
    {
        var ok = new NetworkEntry { StatusCode = 200, Url = "https://x/a" };
        Assert.True(ok.Matches(null, "All", certOnly: false));
        Assert.True(ok.Matches(null, "2xx", certOnly: false));
        Assert.False(ok.Matches(null, "4xx", certOnly: false));
        Assert.True(new NetworkEntry { Error = "boom" }.Matches(null, "ERR", certOnly: false));
    }

    [Fact]
    public void Matches_filters_by_cert_only()
    {
        Assert.True(new NetworkEntry { ClientCertSubject = "CN=me" }.Matches(null, "All", certOnly: true));
        Assert.False(new NetworkEntry().Matches(null, "All", certOnly: true));
    }

    [Fact]
    public void Matches_searches_url_method_status_and_type()
    {
        var e = new NetworkEntry
        {
            Method = "POST",
            Url = "https://api.example.com/users",
            StatusCode = 201,
            ContentType = "application/json"
        };
        Assert.True(e.Matches("example.com", "All", false));
        Assert.True(e.Matches("post", "All", false));
        Assert.True(e.Matches("201", "All", false));
        Assert.True(e.Matches("json", "All", false));
        Assert.False(e.Matches("stylesheet", "All", false));
        Assert.True(e.Matches("  users  ", "All", false));   // whitespace is trimmed
        Assert.True(e.Matches("", "All", false));
    }

    [Fact]
    public void ToCurl_reproduces_the_call()
    {
        var get = new NetworkEntry { Method = "GET", Url = "https://x/a?b=1" };
        Assert.Equal("curl \"https://x/a?b=1\"", get.ToCurl());

        var post = new NetworkEntry
        {
            Method = "POST",
            Url = "https://x/a",
            RequestHeaders = { new("Accept", "application/json"), new("X-Trace", "1") }
        };
        Assert.Equal("curl -X POST \"https://x/a\" -H \"Accept: application/json\" -H \"X-Trace: 1\"", post.ToCurl());
    }

    [Fact]
    public void FormatSize_scales_units()
    {
        Assert.Equal("0 B", NetworkEntry.FormatSize(0));
        Assert.Equal("1023 B", NetworkEntry.FormatSize(1023));
        Assert.Equal("1.0 KB", NetworkEntry.FormatSize(1024));
        Assert.Equal("1.0 MB", NetworkEntry.FormatSize(1024 * 1024));
    }
}
