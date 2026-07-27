using System.Net.Http;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Protects the proxy bypass list on the wire: a rule that should send a host direct must
/// genuinely keep it away from the proxy, not merely relabel a request the handler still tunnels —
/// and a host the list does not name must still ride the proxy exactly as before. Every assertion
/// here is either the loopback proxy's own record of what it was asked to reach, or what a caller
/// reads back through the public ApiClient/ApiResponse surface.</summary>
public class ApiClientProxyBypassTests
{
    /// <summary>Generous: a hung send must fail this test rather than the suite.</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_bypass_rule_keeps_the_named_host_off_the_proxy_while_another_host_still_rides_it()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCertA = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var serverCertB = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var serverA = await LoopbackMtlsServer.StartAsync(
            serverCertA, clientCert.Thumbprint!, "{\"server\":\"a\"}");
        await using var serverB = await LoopbackMtlsServer.StartAsync(
            serverCertB, clientCert.Thumbprint!, "{\"server\":\"b\"}");
        await using var proxy = await LoopbackConnectProxy.StartAsync();

        int portA = new Uri(serverA.BaseUrl).Port;
        int portB = new Uri(serverB.BaseUrl).Port;

        Assert.True(ProxyBypass.TryParse($"127.0.0.1:{portA}", out var rules, out var parseProblem));
        Assert.Null(parseProblem);

        var transport = new TransportOptions
        {
            Proxy = ProxyMode.Explicit,
            ProxyUrl = proxy.Url,
            IgnoreServerCertificateErrors = true,
            NoProxy = rules
        };

        var client = new ApiClient();

        // Server A's port is named by the bypass rule: this request must never reach the proxy at all.
        var respA = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = serverA.BaseUrl, Timeout = SendTimeout },
            clientCert, transport: transport);

        Assert.True(respA.IsSuccess, respA.Error?.Message);
        Assert.Equal(0, proxy.ConnectCount);
        Assert.Empty(proxy.Targets);

        Assert.Equal(ProxyDisposition.BypassedByRule, respA.Connection!.ProxyDisposition);
        Assert.Equal($"127.0.0.1:{portA}", respA.Connection.ProxyBypassRule);
        Assert.False(respA.Connection.ViaProxy);
        Assert.Null(respA.Connection.ProxyUri);

        // Going direct restores the handshake diagnostics the tunnelled path cannot see.
        Assert.NotNull(respA.Connection.TlsProtocol);
        Assert.True(respA.Connection.ClientCertificateSent);

        // Server B's port is not named by the rule: the very same transport must still tunnel it.
        var respB = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = serverB.BaseUrl, Timeout = SendTimeout },
            clientCert, transport: transport);

        Assert.True(respB.IsSuccess, respB.Error?.Message);
        Assert.Equal(1, proxy.ConnectCount);
        Assert.Equal(new[] { $"127.0.0.1:{portB}" }, proxy.Targets);

        Assert.Equal(ProxyDisposition.Proxied, respB.Connection!.ProxyDisposition);
        Assert.Null(respB.Connection.ProxyBypassRule);
        Assert.True(respB.Connection.ViaProxy);
    }

    [Fact]
    public void DecideProxy_reports_BypassedByRule_and_names_the_rule_when_a_rule_matches()
    {
        Assert.True(ProxyBypass.TryParse("internal.corp", out var rules, out _));
        var transport = new TransportOptions
        {
            Proxy = ProxyMode.Explicit,
            ProxyUrl = "http://proxy.example:8080",
            NoProxy = rules
        };

        var (disposition, rule) = ApiClient.DecideProxy("https://internal.corp/", transport);

        Assert.Equal(ProxyDisposition.BypassedByRule, disposition);
        Assert.Equal("internal.corp", rule);
    }

    [Fact]
    public void DecideProxy_reports_Proxied_when_the_bypass_list_does_not_match_the_host()
    {
        Assert.True(ProxyBypass.TryParse("internal.corp", out var rules, out _));
        var transport = new TransportOptions
        {
            Proxy = ProxyMode.Explicit,
            ProxyUrl = "http://proxy.example:8080",
            NoProxy = rules
        };

        var (disposition, rule) = ApiClient.DecideProxy("https://outside.example/", transport);

        Assert.Equal(ProxyDisposition.Proxied, disposition);
        Assert.Null(rule);
    }

    [Fact]
    public void DecideProxy_reports_Direct_for_a_bypass_rule_when_the_proxy_is_off_entirely()
    {
        Assert.True(ProxyBypass.TryParse("internal.corp", out var rules, out _));
        var transport = new TransportOptions { Proxy = ProxyMode.None, NoProxy = rules };

        var (disposition, rule) = ApiClient.DecideProxy("https://internal.corp/", transport);

        // No proxy was in play, so a matching rule changed nothing — the honest answer is Direct,
        // not a claim that a bypass rule did something.
        Assert.Equal(ProxyDisposition.Direct, disposition);
        Assert.Null(rule);
    }

    [Fact]
    public void ValidateTransport_refuses_a_bypass_list_when_the_proxy_is_off_entirely()
    {
        Assert.True(ProxyBypass.TryParse("internal.corp", out var rules, out _));
        var transport = new TransportOptions { Proxy = ProxyMode.None, NoProxy = rules };

        string? problem = ApiClient.ValidateTransport(transport, "https://internal.corp/");

        Assert.Equal(ProxyBypass.NoProxyWithProxyOffMessage, problem);
    }

    [Fact]
    public void ValidateTransport_allows_a_bypass_list_with_the_system_proxy()
    {
        Assert.True(ProxyBypass.TryParse("internal.corp", out var rules, out _));
        var transport = new TransportOptions { Proxy = ProxyMode.System, NoProxy = rules };

        Assert.Null(ApiClient.ValidateTransport(transport, "https://internal.corp/"));
    }

    [Fact]
    public void ValidateTransport_allows_a_bypass_list_with_an_explicit_proxy()
    {
        Assert.True(ProxyBypass.TryParse("internal.corp", out var rules, out _));
        var transport = new TransportOptions
        {
            Proxy = ProxyMode.Explicit,
            ProxyUrl = "http://proxy.example:8080",
            NoProxy = rules
        };

        Assert.Null(ApiClient.ValidateTransport(transport, "https://internal.corp/"));
    }

    [Fact]
    public void ProxyWillBeUsed_is_unchanged_for_an_explicit_proxy_when_NoProxy_is_empty()
    {
        var transport = new TransportOptions { Proxy = ProxyMode.Explicit, ProxyUrl = "http://proxy.example:8080" };

        Assert.True(ApiClient.ProxyWillBeUsed("https://example.com/", transport));
    }

    [Fact]
    public void ProxyWillBeUsed_is_unchanged_for_ProxyMode_None_when_NoProxy_is_empty()
    {
        var transport = new TransportOptions { Proxy = ProxyMode.None };

        Assert.False(ApiClient.ProxyWillBeUsed("https://example.com/", transport));
    }

    [Fact]
    public void HandlerKey_differs_between_transports_that_differ_only_in_their_bypass_list()
    {
        Assert.True(ProxyBypass.TryParse("internal.corp", out var withRule, out _));
        var withoutRule = new TransportOptions { Proxy = ProxyMode.Explicit, ProxyUrl = "http://proxy.example:8080" };
        var withRuleTransport = withoutRule with { NoProxy = withRule };

        var keyWithout = HandlerKey.For(null, withoutRule, null, null);
        var keyWith = HandlerKey.For(null, withRuleTransport, null, null);

        Assert.NotEqual(keyWithout, keyWith);
    }

    [Fact]
    public void HandlerKey_matches_between_transports_with_the_same_bypass_list()
    {
        Assert.True(ProxyBypass.TryParse("internal.corp,10.0.0.0/8", out var rulesA, out _));
        Assert.True(ProxyBypass.TryParse("internal.corp,10.0.0.0/8", out var rulesB, out _));
        var transportA = new TransportOptions { Proxy = ProxyMode.System, NoProxy = rulesA };
        var transportB = new TransportOptions { Proxy = ProxyMode.System, NoProxy = rulesB };

        var keyA = HandlerKey.For(null, transportA, null, null);
        var keyB = HandlerKey.For(null, transportB, null, null);

        Assert.Equal(keyA, keyB);
    }
}
