using ApiTester.Core;

namespace ApiTester.Tests;

public class QueryStringTests
{
    [Fact]
    public void Split_separates_path_and_query()
    {
        var (path, query) = QueryString.Split("https://h/a/b?x=1&y=2");
        Assert.Equal("https://h/a/b", path);
        Assert.Equal("x=1&y=2", query);
    }

    [Fact]
    public void Split_no_query_returns_empty_query()
    {
        var (path, query) = QueryString.Split("https://h/a/b");
        Assert.Equal("https://h/a/b", path);
        Assert.Equal("", query);
    }

    [Fact]
    public void Parse_decodes_pairs_and_plus_as_space()
    {
        var p = QueryString.Parse("q=a+b&tag=%23net&empty=");
        Assert.Equal(3, p.Count);
        Assert.Equal(new("q", "a b"), p[0]);
        Assert.Equal(new("tag", "#net"), p[1]);
        Assert.Equal(new("empty", ""), p[2]);
    }

    [Fact]
    public void Build_encodes_and_roundtrips_through_Parse()
    {
        var pairs = new List<KeyValuePair<string, string>>
        {
            new("q", "a b"), new("tag", "#net"), new("k", "v&v")
        };
        var built = QueryString.Build(pairs);
        Assert.Equal(pairs, QueryString.Parse(built));
    }

    [Fact]
    public void Compose_replaces_existing_query()
    {
        var result = QueryString.Compose("https://h/p?old=1",
            new[] { new KeyValuePair<string, string>("new", "2") });
        Assert.Equal("https://h/p?new=2", result);
    }

    [Fact]
    public void Compose_empty_drops_question_mark()
    {
        Assert.Equal("https://h/p",
            QueryString.Compose("https://h/p?old=1", Array.Empty<KeyValuePair<string, string>>()));
    }
}
