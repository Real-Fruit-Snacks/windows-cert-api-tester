using ApiTester.Core;

namespace ApiTester.Tests;

public class HeaderRulesTests
{
    private static IReadOnlyList<KeyValuePair<string, string>> Headers(
        params (string Name, string Value)[] headers) =>
        headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToList();

    private static IReadOnlyList<string> Values(
        IReadOnlyList<KeyValuePair<string, string>> headers, string name) =>
        headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
               .Select(h => h.Value).ToList();

    private static string? Value(IReadOnlyList<KeyValuePair<string, string>> headers, string name) =>
        Values(headers, name).SingleOrDefault();

    private static IReadOnlyList<KeyValuePair<string, string>> Set(
        params (string Name, string Value)[] rules) => Headers(rules);

    private static IReadOnlyList<string> Remove(params string[] names) => names;

    private static HeaderRules Rules(
        (string, string)[]? setRequest = null,
        string[]? removeRequest = null,
        (string, string)[]? setResponse = null,
        string[]? removeResponse = null)
    {
        var rules = HeaderRules.TryCreate(
            Set(setRequest ?? Array.Empty<(string, string)>()),
            Remove(removeRequest ?? Array.Empty<string>()),
            Set(setResponse ?? Array.Empty<(string, string)>()),
            Remove(removeResponse ?? Array.Empty<string>()),
            out var problem);
        Assert.NotNull(rules);
        Assert.Null(problem);
        return rules!;
    }

    [Fact]
    public void A_set_rule_replaces_an_existing_header_in_place()
    {
        var rules = Rules(setRequest: new[] { ("X-Api-Key", "new") });
        var input = Headers(("Accept", "text/plain"), ("X-Api-Key", "old"), ("Content-Type", "app/json"));

        var result = rules.ApplyToRequest(input);

        Assert.Equal(3, result.Count);
        Assert.Equal("Accept", result[0].Key);
        Assert.Equal(new KeyValuePair<string, string>("X-Api-Key", "new"), result[1]);
        Assert.Equal("Content-Type", result[2].Key);
        Assert.Equal(new[] { "new" }, Values(result, "X-Api-Key"));
    }

    [Fact]
    public void A_set_rule_collapses_duplicate_occurrences_of_the_header_into_one()
    {
        var rules = Rules(setRequest: new[] { ("X-Trace", "t2") });
        var input = Headers(("X-Trace", "t0"), ("Accept", "*/*"), ("X-Trace", "t1"));

        var result = rules.ApplyToRequest(input);

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "t2" }, Values(result, "X-Trace"));
    }

    [Fact]
    public void A_set_rule_for_an_absent_header_appends_it_at_the_end()
    {
        var rules = Rules(setRequest: new[] { ("X-Api-Key", "k1"), ("X-Tenant", "t1") });
        var input = Headers(("Accept", "*/*"));

        var result = rules.ApplyToRequest(input);

        Assert.Equal(new List<KeyValuePair<string, string>>
        {
            new("Accept", "*/*"),
            new("X-Api-Key", "k1"),
            new("X-Tenant", "t1")
        }, result);
    }

    [Fact]
    public void A_remove_rule_drops_every_occurrence_of_the_header()
    {
        var rules = Rules(removeRequest: new[] { "X-Debug" });
        var input = Headers(("X-Debug", "1"), ("Accept", "*/*"), ("X-Debug", "2"));

        var result = rules.ApplyToRequest(input);

        Assert.Empty(Values(result, "X-Debug"));
        Assert.Equal(new[] { new KeyValuePair<string, string>("Accept", "*/*") }, result);
    }

    [Fact]
    public void Remove_beats_set_when_both_name_the_same_header_on_the_request_side()
    {
        var rules = Rules(
            setRequest: new[] { ("X-Api-Key", "new") },
            removeRequest: new[] { "X-Api-Key" });
        var input = Headers(("X-Api-Key", "old"), ("Accept", "*/*"));

        var result = rules.ApplyToRequest(input);

        Assert.Empty(Values(result, "X-Api-Key"));
    }

    [Fact]
    public void Remove_beats_set_when_both_name_the_same_header_on_the_response_side()
    {
        var rules = Rules(
            setResponse: new[] { ("X-Api-Key", "new") },
            removeResponse: new[] { "X-Api-Key" });
        var input = Headers(("X-Api-Key", "old"), ("Content-Type", "application/json"));

        var result = rules.ApplyToResponse(input);

        Assert.Empty(Values(result, "X-Api-Key"));
    }

    [Fact]
    public void Request_rules_do_not_affect_a_response_and_response_rules_do_not_affect_a_request()
    {
        var rules = Rules(
            setRequest: new[] { ("X-Request-Only", "req") },
            setResponse: new[] { ("X-Response-Only", "resp") });
        var input = Headers(("Accept", "*/*"));

        var requestResult = rules.ApplyToRequest(input);
        var responseResult = rules.ApplyToResponse(input);

        Assert.Equal("req", Value(requestResult, "X-Request-Only"));
        Assert.Null(Value(requestResult, "X-Response-Only"));
        Assert.Equal("resp", Value(responseResult, "X-Response-Only"));
        Assert.Null(Value(responseResult, "X-Request-Only"));
    }

    [Fact]
    public void Apply_to_request_returns_the_same_instance_when_there_are_no_request_rules()
    {
        var input = Headers(("Accept", "*/*"));

        Assert.Same(input, Rules().ApplyToRequest(input));
    }

    [Fact]
    public void Apply_to_response_returns_the_same_instance_when_there_are_no_response_rules()
    {
        var input = Headers(("Accept", "*/*"));

        Assert.Same(input, Rules().ApplyToResponse(input));
    }

    [Fact]
    public void Apply_to_request_returns_the_same_instance_when_only_response_rules_are_configured()
    {
        var rules = Rules(setResponse: new[] { ("X-Response-Only", "resp") });
        var input = Headers(("Accept", "*/*"));

        Assert.Same(input, rules.ApplyToRequest(input));
    }

    [Fact]
    public void Applying_a_set_rule_does_not_mutate_the_input_list()
    {
        var rules = Rules(setRequest: new[] { ("X-Api-Key", "new") });
        var input = new List<KeyValuePair<string, string>>
        {
            new("X-Api-Key", "old"), new("Accept", "*/*")
        };
        var before = input.ToList();

        rules.ApplyToRequest(input);

        Assert.Equal(before, input);
    }

    [Fact]
    public void Name_matching_is_case_insensitive_and_the_emitted_name_is_the_rules_spelling()
    {
        var rules = Rules(setRequest: new[] { ("x-api-key", "new") });
        var input = Headers(("X-Api-Key", "old"));

        var result = rules.ApplyToRequest(input);

        Assert.Equal(new[] { new KeyValuePair<string, string>("x-api-key", "new") }, result);
    }

    [Fact]
    public void Headers_no_rule_mentions_keep_their_original_order_and_duplicates()
    {
        var rules = Rules(setRequest: new[] { ("X-Api-Key", "new") });
        var input = Headers(("Accept", "a"), ("Accept", "b"), ("X-Api-Key", "old"), ("Accept", "c"));

        var result = rules.ApplyToRequest(input);

        Assert.Equal(new[] { "a", "b", "c" }, Values(result, "Accept"));
    }

    [Fact]
    public void The_last_set_rule_wins_when_the_same_name_is_set_twice()
    {
        var rules = Rules(setRequest: new[] { ("X-Api-Key", "first"), ("X-Api-Key", "second") });
        var input = Headers(("X-Api-Key", "old"));

        var result = rules.ApplyToRequest(input);

        Assert.Equal(new[] { "second" }, Values(result, "X-Api-Key"));
    }

    [Theory]
    [InlineData("Connection")]
    [InlineData("Keep-Alive")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Content-Length")]
    [InlineData("TE")]
    [InlineData("Trailer")]
    [InlineData("Upgrade")]
    [InlineData("Proxy-Authenticate")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Host")]
    public void Every_refused_header_is_refused_on_every_one_of_the_four_flags(string name)
    {
        AssertRefused(name, "--request-header", setRequest: Set((name, "v")));
        AssertRefused(name, "--remove-request-header", removeRequest: Remove(name));
        AssertRefused(name, "--response-header", setResponse: Set((name, "v")));
        AssertRefused(name, "--remove-response-header", removeResponse: Remove(name));
    }

    private static void AssertRefused(
        string name,
        string flag,
        IReadOnlyList<KeyValuePair<string, string>>? setRequest = null,
        IReadOnlyList<string>? removeRequest = null,
        IReadOnlyList<KeyValuePair<string, string>>? setResponse = null,
        IReadOnlyList<string>? removeResponse = null)
    {
        var rules = HeaderRules.TryCreate(
            setRequest ?? Set(),
            removeRequest ?? Remove(),
            setResponse ?? Set(),
            removeResponse ?? Remove(),
            out var problem);

        Assert.Null(rules);
        Assert.NotNull(problem);
        Assert.Contains(name, problem);
        Assert.Contains(flag, problem);
    }

    [Fact]
    public void Hosts_refusal_message_mentions_the_upstream_uri_rather_than_framing()
    {
        var rules = HeaderRules.TryCreate(Set(("Host", "evil.example")), Remove(), Set(), Remove(),
            out var problem);

        Assert.Null(rules);
        Assert.NotNull(problem);
        Assert.Contains("upstream URI", problem);
    }

    [Fact]
    public void Refusal_is_case_insensitive_and_echoes_the_users_spelling_back()
    {
        var rules = HeaderRules.TryCreate(Set(("transfer-encoding", "v")), Remove(), Set(), Remove(),
            out var problem);

        Assert.Null(rules);
        Assert.NotNull(problem);
        Assert.Contains("'transfer-encoding'", problem);
    }

    [Fact]
    public void A_rule_set_naming_only_ordinary_headers_constructs_fine()
    {
        var rules = HeaderRules.TryCreate(
            Set(("X-Api-Key", "k")), Remove("X-Debug"), Set(("X-Tenant", "t")), Remove("X-Trace"),
            out var problem);

        Assert.NotNull(rules);
        Assert.Null(problem);
    }

    [Fact]
    public void When_several_rules_are_bad_the_first_in_scan_order_wins()
    {
        // Connection in setRequest is scanned first; Host in removeResponse is scanned last, so the
        // reported problem must be the setRequest one even though both are refused.
        var rules = HeaderRules.TryCreate(
            Set(("Connection", "v")), Remove(), Set(), Remove("Host"), out var problem);

        Assert.Null(rules);
        Assert.NotNull(problem);
        Assert.Contains("--request-header", problem);
        Assert.Contains("Connection", problem);
        Assert.DoesNotContain("--remove-response-header", problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_or_whitespace_only_name_is_refused_on_every_one_of_the_four_lists(string name)
    {
        AssertMissingName(name, "--request-header", setRequest: Set((name, "v")));
        AssertMissingName(name, "--remove-request-header", removeRequest: Remove(name));
        AssertMissingName(name, "--response-header", setResponse: Set((name, "v")));
        AssertMissingName(name, "--remove-response-header", removeResponse: Remove(name));
    }

    private static void AssertMissingName(
        string name,
        string flag,
        IReadOnlyList<KeyValuePair<string, string>>? setRequest = null,
        IReadOnlyList<string>? removeRequest = null,
        IReadOnlyList<KeyValuePair<string, string>>? setResponse = null,
        IReadOnlyList<string>? removeResponse = null)
    {
        var rules = HeaderRules.TryCreate(
            setRequest ?? Set(),
            removeRequest ?? Remove(),
            setResponse ?? Set(),
            removeResponse ?? Remove(),
            out var problem);

        Assert.Null(rules);
        Assert.NotNull(problem);
        Assert.Contains(flag, problem);
    }

    [Theory]
    [InlineData("X Y")]
    [InlineData("X:Y")]
    public void A_name_with_a_character_illegal_in_an_http_field_name_is_refused_on_a_set_list(string name)
    {
        var rules = HeaderRules.TryCreate(Set((name, "v")), Remove(), Set(), Remove(), out var problem);

        Assert.Null(rules);
        Assert.NotNull(problem);
        Assert.Contains(name, problem);
        Assert.Contains("--request-header", problem);
    }

    [Theory]
    [InlineData("X Y")]
    [InlineData("X:Y")]
    public void A_name_with_a_character_illegal_in_an_http_field_name_is_refused_on_a_remove_list(string name)
    {
        var rules = HeaderRules.TryCreate(Set(), Remove(name), Set(), Remove(), out var problem);

        Assert.Null(rules);
        Assert.NotNull(problem);
        Assert.Contains(name, problem);
        Assert.Contains("--remove-request-header", problem);
    }

    [Fact]
    public void A_name_using_unusual_but_legal_token_characters_constructs_fine()
    {
        var rules = HeaderRules.TryCreate(
            Set(("X-Api_Key.v1!", "k")), Remove(), Set(), Remove(), out var problem);

        Assert.NotNull(rules);
        Assert.Null(problem);
    }

    [Fact]
    public void Every_hop_by_hop_name_is_also_one_a_user_may_not_manage()
    {
        // A set-flag rule is enough to prove membership in Refused: all four flags consult that one
        // set, and Every_refused_header_is_refused_on_every_one_of_the_four_flags above already pins
        // that the other three flags see the same set — this test is not forgetting them.
        foreach (var name in HopByHop.Names)
        {
            var rules = HeaderRules.TryCreate(Set((name, "v")), Remove(), Set(), Remove(), out _);

            Assert.True(rules is null,
                $"HopByHop names '{name}' but HeaderRules accepts a rule for it. The two sets are " +
                "deliberately separate — HopByHop answers \"never relay this through a proxy\", " +
                "HeaderRules' Refused answers \"a user may not manage this on the command line\" — " +
                "but they must not diverge in this direction: MtlsGateway.ForwardAsync strips " +
                "HopByHop names *after* HeaderRules.ApplyToRequest has run, so a name in HopByHop " +
                "that HeaderRules does not refuse is a rule `certapi serve` accepts on the command " +
                "line and then silently throws away — no error, a gateway that starts normally, and " +
                "an operator who believes a header is being managed when it is not. That is the " +
                $"exact defect v1.61.1 fixed. Add '{name}' to HeaderRules.Refused as well, or take " +
                "it out of HopByHop.");
        }
    }
}
