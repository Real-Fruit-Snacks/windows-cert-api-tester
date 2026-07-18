using ApiTester.Core;

namespace ApiTester.Tests;

public class EndpointListTests
{
    [Fact]
    public void Parses_bare_paths()
    {
        var e = EndpointList.Parse("/api/users\n/api/orders\nhealth");
        Assert.Equal(3, e.Count);
        Assert.Equal("/api/users", e[0].Path);
        Assert.Null(e[0].Method);
        Assert.Equal("health", e[2].Path);
    }

    [Fact]
    public void Skips_blanks_and_comments()
    {
        var e = EndpointList.Parse("# a comment\n\n  \n/api/x\n   # indented comment\n/api/y");
        Assert.Equal(2, e.Count);
        Assert.Equal("/api/x", e[0].Path);
        Assert.Equal("/api/y", e[1].Path);
    }

    [Theory]
    [InlineData("POST /api/users", "POST", "/api/users")]
    [InlineData("get /health", "GET", "/health")]
    [InlineData("Delete  /api/thing", "DELETE", "/api/thing")]
    public void Parses_method_prefix(string line, string method, string path)
    {
        var e = EndpointList.Parse(line);
        Assert.Equal(method, e[0].Method);
        Assert.Equal(path, e[0].Path);
    }

    [Fact]
    public void A_leading_non_method_word_is_part_of_the_path()
    {
        // "api/users" has no method prefix; the whole token is the path.
        var e = EndpointList.Parse("api/users");
        Assert.Null(e[0].Method);
        Assert.Equal("api/users", e[0].Path);
    }

    [Fact]
    public void Keeps_full_url_paths_intact()
    {
        var e = EndpointList.Parse("https://other.example.com/api/x\nGET https://h/y");
        Assert.Equal("https://other.example.com/api/x", e[0].Path);
        Assert.Null(e[0].Method);
        Assert.Equal("GET", e[1].Method);
        Assert.Equal("https://h/y", e[1].Path);
    }

    [Fact]
    public void Dedupes_identical_method_path_pairs()
    {
        var e = EndpointList.Parse("/a\n/a\nPOST /a\nPOST /a\nGET /a");
        // /a (no method), POST /a, GET /a  → 3 distinct
        Assert.Equal(3, e.Count);
    }

    [Fact]
    public void Trims_trailing_whitespace_and_carriage_returns()
    {
        var e = EndpointList.Parse("/api/x  \r\n/api/y\r");
        Assert.Equal("/api/x", e[0].Path);
        Assert.Equal("/api/y", e[1].Path);
    }

    [Fact]
    public void Empty_or_comment_only_text_yields_no_entries()
    {
        Assert.Empty(EndpointList.Parse(""));
        Assert.Empty(EndpointList.Parse("# nothing\n\n#more"));
    }
}
