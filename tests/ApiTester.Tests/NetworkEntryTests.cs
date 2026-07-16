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
}
