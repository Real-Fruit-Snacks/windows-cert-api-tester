using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Protects the NO_PROXY-style bypass matcher: a rule that matches too much silently sends
/// internal traffic direct, and one that matches too little silently leaks it through the proxy. Every
/// case here is pinned in both directions.</summary>
public class ProxyBypassTests
{
    [Fact]
    public void An_exact_host_entry_bypasses_that_host()
    {
        Assert.True(ProxyBypass.TryParse("internal.corp", out var rules, out var problem));
        Assert.Null(problem);

        Assert.True(ProxyBypass.IsBypassed(rules, new Uri("https://internal.corp/")));
    }

    [Theory]
    [InlineData("internal.corp", "api.internal.corp", true)]     // bare host also matches a subdomain
    [InlineData("internal.corp", "internal.corp", true)]         // and the exact host
    [InlineData("internal.corp", "notinternal.corp", false)]     // must NOT match a look-alike suffix
    [InlineData("internal.corp", "internal.corp.evil.com", false)]
    [InlineData(".corp", "corp", true)]                           // leading dot matches the bare domain
    [InlineData(".corp", "internal.corp", true)]                  // and any subdomain
    [InlineData(".corp", "internal.corp.evil.com", false)]        // but not a domain that merely contains it
    [InlineData("corp", "corp", true)]                            // no leading dot behaves the same as bare host
    [InlineData("corp", "internal.corp", true)]
    [InlineData("*.corp", "corp", true)]                          // wildcard suffix behaves like leading dot
    [InlineData("*.corp", "internal.corp", true)]
    [InlineData("*.corp", "internal.corp.evil.com", false)]
    public void Domain_rule_matching_is_pinned_in_both_directions(string entry, string host, bool expected)
    {
        Assert.True(ProxyBypass.TryParse(entry, out var rules, out var problem));
        Assert.Null(problem);

        Assert.Equal(expected, ProxyBypass.IsBypassed(rules, new Uri($"https://{host}/")));
    }

    [Theory]
    [InlineData("*", "example.com", true)]
    [InlineData("*", "1.2.3.4", true)]
    public void A_lone_star_matches_every_host_including_an_ip_literal(string entry, string host, bool expected)
    {
        Assert.True(ProxyBypass.TryParse(entry, out var rules, out _));

        Assert.Equal(expected, ProxyBypass.IsBypassed(rules, new Uri($"https://{host}/")));
    }

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4", true)]     // matches itself
    [InlineData("1.2.3.4", "1.2.3.5", false)]    // not another address
    [InlineData("1.2.3.4", "example.com", false)] // not a DNS name
    [InlineData("1.2.3.4", "x.1.2.3.4", false)]  // an address rule never suffix-matches
    public void An_address_rule_matches_only_that_exact_address(string entry, string host, bool expected)
    {
        Assert.True(ProxyBypass.TryParse(entry, out var rules, out var problem));
        Assert.Null(problem);

        Assert.Equal(expected, ProxyBypass.IsBypassed(rules, new Uri($"https://{host}/")));
    }

    [Theory]
    [InlineData("10.0.0.0/8", "10.1.2.3", true)]
    [InlineData("10.0.0.0/8", "11.1.2.3", false)]
    [InlineData("10.0.0.0/8", "example.com", false)]      // a DNS name never matches a range
    [InlineData("fd00::/8", "fd12::1", true)]
    public void A_cidr_range_matches_addresses_it_contains(string entry, string host, bool expected)
    {
        Assert.True(ProxyBypass.TryParse(entry, out var rules, out var problem));
        Assert.Null(problem);

        // An IPv6 literal needs brackets to be a legal URL authority; an IPv4 one or a DNS name does not.
        string authority = host.Contains(':') ? $"[{host}]" : host;
        Assert.Equal(expected, ProxyBypass.IsBypassed(rules, new Uri($"https://{authority}/")));
    }

    [Fact]
    public void A_range_with_host_bits_set_is_refused_naming_the_entry()
    {
        Assert.False(ProxyBypass.TryParse("10.0.0.5/8", out var rules, out var problem));

        Assert.Empty(rules);
        Assert.NotNull(problem);
        Assert.Contains("10.0.0.5/8", problem);
    }

    [Fact]
    public void A_port_specific_entry_matches_only_that_port()
    {
        Assert.True(ProxyBypass.TryParse("internal.corp:8443", out var rules, out _));

        Assert.True(ProxyBypass.IsBypassed(rules, new Uri("https://internal.corp:8443/")));
        Assert.False(ProxyBypass.IsBypassed(rules, new Uri("https://internal.corp/")));
    }

    [Fact]
    public void A_port_specific_host_entry_also_matches_a_subdomain_on_that_port()
    {
        Assert.True(ProxyBypass.TryParse("internal.corp:8443", out var rules, out _));

        Assert.True(ProxyBypass.IsBypassed(rules, new Uri("https://api.internal.corp:8443/")));
    }

    [Theory]
    [InlineData("example.com", 8080, true)]
    [InlineData("example.com", 8081, false)]
    [InlineData("1.2.3.4", 8080, true)]
    public void A_star_with_a_port_matches_any_host_only_on_that_port(string host, int port, bool expected)
    {
        Assert.True(ProxyBypass.TryParse("*:8080", out var rules, out _));

        Assert.Equal(expected, ProxyBypass.IsBypassed(rules, new Uri($"https://{host}:{port}/")));
    }

    [Fact]
    public void Rule_and_host_case_differences_do_not_affect_matching()
    {
        Assert.True(ProxyBypass.TryParse("INTERNAL.Corp", out var rules, out _));

        Assert.True(ProxyBypass.IsBypassed(rules, new Uri("https://Api.Internal.CORP/")));
    }

    [Fact]
    public void A_trailing_dot_on_the_rule_side_is_ignored()
    {
        Assert.True(ProxyBypass.TryParse("internal.corp.", out var rules, out _));

        Assert.True(ProxyBypass.IsBypassed(rules, new Uri("https://internal.corp/")));
    }

    [Fact]
    public void A_trailing_dot_on_the_host_side_is_ignored()
    {
        Assert.True(ProxyBypass.TryParse("internal.corp", out var rules, out _));

        // Uri normalizes a trailing-dot host in the authority, so exercise the rule directly.
        var rule = Assert.Single(rules);
        Assert.True(rule.Matches("internal.corp.", 443));
    }

    [Fact]
    public void A_bracketed_ipv6_literal_with_a_port_matches_that_address_and_port()
    {
        Assert.True(ProxyBypass.TryParse("[::1]:8443", out var rules, out var problem));
        Assert.Null(problem);

        Assert.True(ProxyBypass.IsBypassed(rules, new Uri("https://[::1]:8443/")));
        Assert.False(ProxyBypass.IsBypassed(rules, new Uri("https://[::1]:9000/")));
    }

    [Fact]
    public void A_bare_ipv6_literal_with_no_port_matches_that_address_on_any_port()
    {
        Assert.True(ProxyBypass.TryParse("::1", out var rules, out var problem));
        Assert.Null(problem);

        Assert.True(ProxyBypass.IsBypassed(rules, new Uri("https://[::1]:8443/")));
        Assert.True(ProxyBypass.IsBypassed(rules, new Uri("https://[::1]/")));
    }

    [Fact]
    public void Separator_noise_is_skipped_rather_than_treated_as_a_typo()
    {
        Assert.True(ProxyBypass.TryParse("a,,b", out var rules, out var problem));
        Assert.Null(problem);
        Assert.Equal(2, rules.Count);

        Assert.True(ProxyBypass.TryParse("a,b,", out var trailing, out _));
        Assert.Equal(2, trailing.Count);
    }

    [Fact]
    public void A_spec_of_only_commas_yields_no_rules()
    {
        Assert.True(ProxyBypass.TryParse(",,,", out var rules, out var problem));

        Assert.Null(problem);
        Assert.Empty(rules);
    }

    [Fact]
    public void An_interior_wildcard_is_refused_naming_the_entry()
    {
        Assert.False(ProxyBypass.TryParse("a*b.corp", out var rules, out var problem));

        Assert.Empty(rules);
        Assert.NotNull(problem);
        Assert.Contains("a*b.corp", problem);
    }

    [Theory]
    [InlineData("internal.corp:0")]
    [InlineData("internal.corp:70000")]
    public void A_bad_port_is_refused_naming_the_entry(string entry)
    {
        Assert.False(ProxyBypass.TryParse(entry, out var rules, out var problem));

        Assert.Empty(rules);
        Assert.NotNull(problem);
        Assert.Contains(entry, problem);
    }

    [Fact]
    public void Format_normalizes_case_and_reparses_to_an_equal_list()
    {
        Assert.True(ProxyBypass.TryParse("A.Corp, .B.com", out var rules, out _));

        string formatted = ProxyBypass.Format(rules);
        Assert.Equal("a.corp,.b.com", formatted);

        Assert.True(ProxyBypass.TryParse(formatted, out var reparsed, out _));
        Assert.Equal(rules.Select(r => r.Text), reparsed.Select(r => r.Text));
    }

    [Fact]
    public void Format_of_an_empty_list_is_the_empty_string()
    {
        Assert.Equal("", ProxyBypass.Format(ProxyBypass.None));
    }

    [Fact]
    public void EnvironmentSpec_reads_NO_PROXY_when_present()
    {
        string? spec = ProxyBypass.EnvironmentSpec(name => name == "NO_PROXY" ? "internal.corp" : null);

        Assert.Equal("internal.corp", spec);
    }

    [Fact]
    public void EnvironmentSpec_falls_back_to_lowercase_no_proxy()
    {
        string? spec = ProxyBypass.EnvironmentSpec(name => name == "no_proxy" ? "internal.corp" : null);

        Assert.Equal("internal.corp", spec);
    }

    [Fact]
    public void EnvironmentSpec_prefers_NO_PROXY_when_both_are_set()
    {
        string? spec = ProxyBypass.EnvironmentSpec(name => name switch
        {
            "NO_PROXY" => "upper.corp",
            "no_proxy" => "lower.corp",
            _ => null
        });

        Assert.Equal("upper.corp", spec);
    }

    [Fact]
    public void EnvironmentSpec_is_null_when_neither_variable_is_set()
    {
        string? spec = ProxyBypass.EnvironmentSpec(_ => null);

        Assert.Null(spec);
    }

    [Fact]
    public void EnvironmentSpec_is_null_when_the_variable_is_whitespace_only()
    {
        string? spec = ProxyBypass.EnvironmentSpec(name => name == "NO_PROXY" ? "   " : null);

        Assert.Null(spec);
    }

    [Fact]
    public void ParseLenient_drops_entries_it_cannot_understand()
    {
        var rules = ProxyBypass.ParseLenient("internal.corp,a*b.corp,10.0.0.0/8");

        Assert.Equal(new[] { "internal.corp", "10.0.0.0/8" }, rules.Select(r => r.Text));
    }
}
