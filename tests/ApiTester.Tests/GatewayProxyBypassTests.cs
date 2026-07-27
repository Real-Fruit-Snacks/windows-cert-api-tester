using System.IO;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Proves the proxy bypass list reaches <see cref="MtlsGateway"/> — a second, independent
/// consumer of <see cref="ProxyConfiguration.Apply"/> alongside <c>ApiClient</c> — by checking the
/// loopback proxy's own record of what it was asked to reach, never the gateway's internals.</summary>
public class GatewayProxyBypassTests
{
    private static async Task<string> ReadBody(GatewayResponse r)
    {
        using (r.Lifetime)
        using (var sr = new StreamReader(r.Body))
            return await sr.ReadToEndAsync();
    }

    [Fact]
    public async Task A_bypassed_upstream_never_reaches_the_proxy()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("GatewayBypassClient", ca, false, true);

        await using var upstream = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");
        await using var proxy = await LoopbackConnectProxy.StartAsync();
        int upstreamPort = new Uri(upstream.BaseUrl).Port;

        Assert.True(ProxyBypass.TryParse($"127.0.0.1:{upstreamPort}", out var rules, out var problem));
        Assert.Null(problem);

        var transport = new TransportOptions
        {
            Proxy = ProxyMode.Explicit,
            ProxyUrl = proxy.Url,
            IgnoreServerCertificateErrors = true,
            NoProxy = rules
        };

        using var gw = new MtlsGateway(
            new GatewayRoutes(new[] { new GatewayRoute("/", new Uri(upstream.BaseUrl)) }),
            clientCert, ignoreServerCertificateErrors: true, TimeSpan.FromSeconds(30), transport);

        var resp = await gw.ForwardAsync(
            new GatewayRequest("GET", "/", Array.Empty<KeyValuePair<string, string>>(), null, null), default);

        Assert.Equal(200, resp.StatusCode);
        Assert.Contains("ok", await ReadBody(resp));
        Assert.Equal(0, proxy.ConnectCount);
        Assert.Empty(proxy.Targets);
    }

    /// <summary>The control for the test above: with the same gateway configuration but no bypass
    /// list, the request really does go through the proxy — proving the zero above means "the rule
    /// worked", not "the proxy was never used anyway".</summary>
    [Fact]
    public async Task The_same_gateway_configuration_without_the_bypass_list_goes_through_the_proxy()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("GatewayBypassClient", ca, false, true);

        await using var upstream = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");
        await using var proxy = await LoopbackConnectProxy.StartAsync();
        int upstreamPort = new Uri(upstream.BaseUrl).Port;

        var transport = new TransportOptions
        {
            Proxy = ProxyMode.Explicit,
            ProxyUrl = proxy.Url,
            IgnoreServerCertificateErrors = true
        };

        using var gw = new MtlsGateway(
            new GatewayRoutes(new[] { new GatewayRoute("/", new Uri(upstream.BaseUrl)) }),
            clientCert, ignoreServerCertificateErrors: true, TimeSpan.FromSeconds(30), transport);

        var resp = await gw.ForwardAsync(
            new GatewayRequest("GET", "/", Array.Empty<KeyValuePair<string, string>>(), null, null), default);

        Assert.Equal(200, resp.StatusCode);
        Assert.Contains("ok", await ReadBody(resp));
        Assert.Equal(1, proxy.ConnectCount);
        Assert.Equal(new[] { $"127.0.0.1:{upstreamPort}" }, proxy.Targets);
    }
}
