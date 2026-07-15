using ApiTester.Core;

namespace ApiTester.Tests;

public class RequestUrlTests
{
    [Fact]
    public void Effective_combines_base_path_and_enabled_params()
    {
        var url = RequestUrl.Effective("https://h", "/api/x",
            new[] { new KeyValuePair<string, string>("a", "1"), new("b", "2") });
        Assert.Equal("https://h/api/x?a=1&b=2", url);
    }

    [Fact]
    public void Effective_no_params_has_no_question_mark()
    {
        Assert.Equal("https://h/api/x",
            RequestUrl.Effective("https://h", "/api/x", Array.Empty<KeyValuePair<string, string>>()));
    }

    [Fact]
    public void Effective_honors_absolute_url_in_path()
    {
        var url = RequestUrl.Effective("https://ignored", "https://other/thing",
            new[] { new KeyValuePair<string, string>("a", "1") });
        Assert.Equal("https://other/thing?a=1", url);
    }

    [Fact]
    public void SplitForEditing_pulls_query_out_of_a_url()
    {
        var (path, ps) = RequestUrl.SplitForEditing("/api/x?a=1&b=2");
        Assert.Equal("/api/x", path);
        Assert.Equal(2, ps.Count);
        Assert.Equal(new("a", "1"), ps[0]);
        Assert.Equal(new("b", "2"), ps[1]);
    }
}
