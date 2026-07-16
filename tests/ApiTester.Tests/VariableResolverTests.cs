using ApiTester.Core;

namespace ApiTester.Tests;

public class VariableResolverTests
{
    private static Dictionary<string, string> Vars(params (string k, string v)[] pairs)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    [Fact]
    public void Substitutes_a_single_token()
    {
        var (result, unresolved) = VariableResolver.Resolve("{{base}}/api", Vars(("base", "https://h")));
        Assert.Equal("https://h/api", result);
        Assert.Empty(unresolved);
    }

    [Fact]
    public void Substitutes_multiple_and_adjacent_tokens()
    {
        var (result, _) = VariableResolver.Resolve("{{a}}{{b}}/{{a}}", Vars(("a", "1"), ("b", "2")));
        Assert.Equal("12/1", result);
    }

    [Fact]
    public void Trims_whitespace_inside_the_braces()
    {
        var (result, _) = VariableResolver.Resolve("{{ base }}", Vars(("base", "ok")));
        Assert.Equal("ok", result);
    }

    [Fact]
    public void Leaves_unknown_tokens_intact_and_reports_them()
    {
        var (result, unresolved) = VariableResolver.Resolve("{{base}}/{{missing}}", Vars(("base", "h")));
        Assert.Equal("h/{{missing}}", result);
        Assert.Equal(new[] { "missing" }, unresolved);
    }

    [Fact]
    public void No_tokens_returns_input_unchanged()
    {
        var (result, unresolved) = VariableResolver.Resolve("https://plain/url", Vars());
        Assert.Equal("https://plain/url", result);
        Assert.Empty(unresolved);
    }

    [Fact]
    public void Unclosed_braces_are_left_alone()
    {
        var (result, unresolved) = VariableResolver.Resolve("{{oops/api", Vars(("oops", "x")));
        Assert.Equal("{{oops/api", result);
        Assert.Empty(unresolved);
    }
}
