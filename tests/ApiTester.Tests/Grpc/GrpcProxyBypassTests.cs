using System.IO;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Grpc;

/// <summary>The command-line surface of the proxy bypass list for <c>certapi grpc</c>, which parses
/// its own transport flags rather than calling <see cref="TransportFlags.Parse"/>: proves
/// <see cref="TransportFlags.ResolveNoProxy"/> resolves the flag and the NO_PROXY environment
/// fallback the same way the shared commands do, and that <c>grpc</c> itself refuses --noproxy
/// together with --no-proxy before ever opening a connection. The channel's own honoring of the
/// resulting list is proved once, for every consumer, by <c>ProxyConfiguration.Apply</c>'s own tests
/// (ApiClientProxyBypassTests, GatewayProxyBypassTests) — a gRPC-over-CONNECT-proxy test cannot add
/// anything here: the loopback proxy only speaks CONNECT, and the gRPC test server is plaintext h2c,
/// so such a test could not distinguish a bypass from a refusal.</summary>
public class GrpcProxyBypassTests
{
    private static string TempLive() =>
        Path.Combine(Path.GetTempPath(), $"certapi-grpc-noproxy-live-{Guid.NewGuid():N}.json");

    [Fact]
    public void An_explicit_noproxy_flag_bypasses_the_named_host_but_not_another()
    {
        var (fromFlag, _) = TransportFlags.ResolveNoProxy("internal.corp", proxyOff: false);

        Assert.True(ProxyBypass.IsBypassed(fromFlag, new Uri("https://api.internal.corp/")));
        Assert.False(ProxyBypass.IsBypassed(fromFlag, new Uri("https://example.com/")));
    }

    [Fact]
    public void With_no_flag_an_injected_NO_PROXY_variable_is_honored_via_the_environment_list()
    {
        Func<string, string?> env = name => name == "NO_PROXY" ? "internal.corp" : null;

        var (fromFlag, fromEnvironment) = TransportFlags.ResolveNoProxy(null, proxyOff: false, environment: env);

        Assert.Empty(fromFlag);
        Assert.True(ProxyBypass.IsBypassed(fromEnvironment, new Uri("https://api.internal.corp/")));
    }

    [Fact]
    public void Noproxy_together_with_proxy_off_throws_the_shared_usage_message()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            TransportFlags.ResolveNoProxy("internal.corp", proxyOff: true));

        Assert.Equal(ProxyBypass.NoProxyWithProxyOffMessage, ex.Message);
    }

    [Fact]
    public void With_the_proxy_off_an_injected_NO_PROXY_variable_is_not_consulted_and_is_not_an_error()
    {
        Func<string, string?> env = name => name == "NO_PROXY" ? "internal.corp" : null;

        var (fromFlag, fromEnvironment) = TransportFlags.ResolveNoProxy(null, proxyOff: true, environment: env);

        Assert.Empty(fromFlag);
        Assert.Empty(fromEnvironment);
    }

    [Fact]
    public void Grpc_list_with_noproxy_and_no_proxy_together_is_a_usage_error()
    {
        string live = TempLive();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int code = CliApp.Run(
                new[] { "grpc", "list", "https://example.invalid/", "--no-proxy", "--noproxy", "internal.corp" },
                stdout, stderr,
                services: new CliServices { LiveStatePath = live, IsGuiRunning = () => false, Cancel = default });

            Assert.Equal(ExitCodes.Usage, code);
            // The shared message, not merely "unknown option '--noproxy'" — proves grpc really
            // recognizes --noproxy and routes it through the same refusal as send/run/fuzz/serve.
            Assert.Contains(ProxyBypass.NoProxyWithProxyOffMessage, stderr.ToString());
        }
        finally { if (File.Exists(live)) File.Delete(live); }
    }
}
