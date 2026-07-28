using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Covers the proxy report's decidable parts as data — the emptiness rule, the
/// two-engine comparison, and the disagreement contract — with both engines injected, so no test
/// needs a PAC server, a registry write, or a network. The real WinHTTP path is exercised by the
/// CLI acceptance sweep against whatever this machine is actually configured with.</summary>
public class ProxyIntrospectionTests
{
    private static ProxySettings None => new(false, null, false, null, null);

    [Fact]
    public void Settings_with_nothing_configured_are_empty()
    {
        Assert.True(None.IsEmpty);
        // A proxy that is switched on but names no server is still nothing to route through.
        Assert.True(new ProxySettings(false, null, true, "", null).IsEmpty);
        Assert.True(new ProxySettings(false, "   ", false, null, null).IsEmpty);
    }

    [Theory]
    [InlineData(true, null, false, null)]              // WPAD alone is a configuration
    [InlineData(false, "http://wpad/proxy.pac", false, null)]
    [InlineData(false, null, true, "proxy.corp:8080")]
    public void Settings_with_anything_configured_are_not_empty(
        bool autoDetect, string? pac, bool enabled, string? server)
    {
        Assert.False(new ProxySettings(autoDetect, pac, enabled, server, null).IsEmpty);
    }

    [Fact]
    public void Decide_reports_both_engines_and_agrees_when_they_match()
    {
        var decision = ProxyIntrospection.Decide(
            "https://api.example.com/x", None,
            winHttp: (_, _) => ("PROXY proxy.corp:8080", null),
            dotNet: _ => "PROXY proxy.corp:8080");

        Assert.Equal("PROXY proxy.corp:8080", decision.WinHttpProxy);
        Assert.Equal("PROXY proxy.corp:8080", decision.DotNetProxy);
        Assert.False(decision.Disagrees);
    }

    [Fact]
    public void Decide_flags_a_genuine_disagreement_between_the_engines()
    {
        // The case worth catching: a browser (WinHTTP/PAC) and certapi (.NET) taking different
        // routes to the same host, which looks like a broken tool until someone sees this.
        var decision = ProxyIntrospection.Decide(
            "https://api.example.com/x", None,
            winHttp: (_, _) => ("PROXY pac-chosen.corp:8080", null),
            dotNet: _ => "http://static.corp:3128/");

        Assert.True(decision.Disagrees);
    }

    [Fact]
    public void Both_saying_direct_is_agreement_however_it_is_spelled()
    {
        // WinHTTP answers DIRECT as a null; .NET answers it as a null too. Neither is a mismatch,
        // and neither is the literal string "DIRECT" some engines print.
        var nulls = ProxyIntrospection.Decide("https://a/x", None,
            winHttp: (_, _) => (null, null), dotNet: _ => null);
        Assert.False(nulls.Disagrees);

        var spelled = ProxyIntrospection.Decide("https://a/x", None,
            winHttp: (_, _) => ("DIRECT", null), dotNet: _ => null);
        Assert.False(spelled.Disagrees);
    }

    [Fact]
    public void A_winhttp_error_is_reported_and_never_counted_as_a_disagreement()
    {
        // WPAD failing to find a script is ordinary on a home network; it must not be dressed up
        // as the two engines conflicting, which would be a different (and wrong) diagnosis.
        var decision = ProxyIntrospection.Decide(
            "https://api.example.com/x", new ProxySettings(true, null, false, null, null),
            winHttp: (_, _) => (null, "WPAD found no proxy-configuration script on this network."),
            dotNet: _ => "PROXY static.corp:8080");

        Assert.NotNull(decision.WinHttpError);
        Assert.False(decision.Disagrees);
    }

    [Fact]
    public void Case_and_spacing_differences_between_the_engines_are_not_a_disagreement()
    {
        var decision = ProxyIntrospection.Decide("https://a/x", None,
            winHttp: (_, _) => ("PROXY Proxy.Corp:8080", null),
            dotNet: _ => "  proxy proxy.corp:8080  ");

        Assert.False(decision.Disagrees);
    }

    [Fact]
    public void ReadSettings_never_throws_on_this_machine()
    {
        // Whatever this machine is configured with, reading it is a diagnostic and must not be
        // what fails: the contract is "returns something", not "returns a particular thing".
        var settings = ProxyIntrospection.ReadSettings();

        Assert.NotNull(settings);
    }

    [Fact]
    public void Decide_with_no_automatic_configuration_asks_winhttp_for_nothing_and_reports_direct()
    {
        // The real engine short-circuits when neither WPAD nor a PAC URL is set: there is nothing
        // for a script engine to evaluate, so DIRECT is the answer and not an error.
        var decision = ProxyIntrospection.Decide("https://api.example.com/x", None, dotNet: _ => null);

        Assert.Null(decision.WinHttpError);
        Assert.Null(decision.WinHttpProxy);
        Assert.False(decision.Disagrees);
    }
}
