using System;
using System.Collections.Generic;
using System.Linq;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>EffectiveUrl(resolve): {{variable}} tokens in the base URL, the path, and each enabled
/// query parameter's key/value are expanded before the query is percent-encoded, so a resolved
/// value is what gets escaped rather than the literal "{{tok}}" — resolving after composition
/// cannot work, because escaping has already turned "{{tok}}" into "%7B%7Btok%7D%7D".</summary>
public class QueryVariableResolutionTests
{
    private static (Func<string, string> Resolve, List<string> Unresolved) Resolver(params (string Key, string Value)[] vars)
    {
        var map = vars.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        var unresolved = new List<string>();
        string R(string s)
        {
            var (result, missing) = VariableResolver.Resolve(s ?? "", map);
            foreach (var m in missing) if (!unresolved.Contains(m)) unresolved.Add(m);
            return result;
        }
        return (R, unresolved);
    }

    private static RequestModel Model(string baseUrl, string path, params (string Key, string Value)[] queryParams)
    {
        var m = new RequestModel { BaseUrl = baseUrl, Path = path };
        foreach (var (k, v) in queryParams) m.QueryParams.Add(new ParamRow { Key = k, Value = v });
        return m;
    }

    [Fact]
    public void EffectiveUrl_with_no_resolver_still_escapes_a_variable_token()
    {
        var m = Model("https://h", "/api", ("api_key", "{{tok}}"));

        Assert.Equal("https://h/api?api_key=%7B%7Btok%7D%7D", m.EffectiveUrl());
    }

    [Fact]
    public void EffectiveUrl_resolves_a_query_value_before_it_is_escaped()
    {
        var m = Model("https://h", "/api", ("api_key", "{{tok}}"));
        var (resolve, _) = Resolver(("tok", "SECRET123"));

        Assert.Equal("https://h/api?api_key=SECRET123", m.EffectiveUrl(resolve));
    }

    [Fact]
    public void EffectiveUrl_escapes_a_resolved_value_that_contains_query_syntax()
    {
        var m = Model("https://h", "/api", ("api_key", "{{tok}}"));
        var (resolve, _) = Resolver(("tok", "a b&c=d é"));

        string url = m.EffectiveUrl(resolve);

        Assert.Equal("https://h/api?api_key=a%20b%26c%3Dd%20%C3%A9", url);
        Assert.Contains("a%20b%26c%3Dd", url);

        // The round trip is the assertion that proves the query is not broken: parsing what was
        // composed yields back exactly the original, unescaped value.
        var (_, query) = QueryString.Split(url);
        var pair = Assert.Single(QueryString.Parse(query));
        Assert.Equal("api_key", pair.Key);
        Assert.Equal("a b&c=d é", pair.Value);
    }

    [Fact]
    public void EffectiveUrl_resolves_a_query_parameter_key()
    {
        var m = new RequestModel { BaseUrl = "https://h", Path = "/api" };
        m.QueryParams.Add(new ParamRow { Key = "{{keyname}}", Value = "v" });
        var (resolve, _) = Resolver(("keyname", "api_key"));

        Assert.Equal("https://h/api?api_key=v", m.EffectiveUrl(resolve));
    }

    [Fact]
    public void EffectiveUrl_resolves_the_base_url_and_the_path_too()
    {
        var m = Model("https://{{host}}", "/api/{{id}}", ("x", "1"));
        var (resolve, _) = Resolver(("host", "example.test"), ("id", "7"));

        Assert.Equal("https://example.test/api/7?x=1", m.EffectiveUrl(resolve));
    }

    [Fact]
    public void EffectiveUrl_reports_an_unresolved_query_token_to_the_resolver()
    {
        var m = Model("https://h", "/api", ("api_key", "{{tok}}"));
        var (resolve, unresolved) = Resolver();   // no variables supplied

        string url = m.EffectiveUrl(resolve);

        Assert.Equal(new[] { "tok" }, unresolved);
        // The token is left intact by the resolver, so it is still what gets percent-escaped —
        // exactly the pre-fix output, but now the caller learns "tok" was never found.
        Assert.Equal("https://h/api?api_key=%7B%7Btok%7D%7D", url);
    }

    [Fact]
    public void EffectiveUrl_reports_an_unresolved_query_key_token_too()
    {
        var m = new RequestModel { BaseUrl = "https://h", Path = "/api" };
        m.QueryParams.Add(new ParamRow { Key = "{{tok}}", Value = "v" });
        var (resolve, unresolved) = Resolver();   // no variables supplied

        string url = m.EffectiveUrl(resolve);

        Assert.Equal(new[] { "tok" }, unresolved);
        Assert.Equal("https://h/api?%7B%7Btok%7D%7D=v", url);
    }

    [Fact]
    public void EffectiveUrl_drops_a_parameter_whose_key_resolves_to_nothing()
    {
        var m = new RequestModel { BaseUrl = "https://h", Path = "/api" };
        m.QueryParams.Add(new ParamRow { Key = "{{blank}}", Value = "x" });
        m.QueryParams.Add(new ParamRow { Key = "good", Value = "y" });
        var (resolve, _) = Resolver(("blank", ""));

        Assert.Equal("https://h/api?good=y", m.EffectiveUrl(resolve));
    }

    [Fact]
    public void EffectiveUrl_treats_a_resolved_path_exactly_like_a_typed_one()
    {
        // RequestUrl.Effective already drops any query typed straight into the path — combining base +
        // path first and only then splitting off "?..." means a literal "/search?q=1" loses its query
        // today. Resolving the path *before* composition (this contract) makes a variable whose value
        // contains "?q=1" behave exactly like that literal: the query is dropped either way. This test
        // pins that consequence rather than adding special handling to preserve it.
        var resolved = new RequestModel { BaseUrl = "https://h", Path = "{{ep}}" };
        var (resolve, _) = Resolver(("ep", "/search?q=1"));

        var literal = new RequestModel { BaseUrl = "https://h", Path = "/search?q=1" };

        Assert.Equal("https://h/search", resolved.EffectiveUrl(resolve));
        Assert.Equal(literal.EffectiveUrl(), resolved.EffectiveUrl(resolve));
    }
}
