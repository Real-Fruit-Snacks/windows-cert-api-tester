using System.Net.Http;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

public class ApiClientRedirectTests
{
    [Fact]
    public async Task Every_redirect_in_a_chain_is_followed_and_recorded_as_a_hop()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartRedirectChainAsync(
            serverCert, clientCert.Thumbprint!, hops: 3);
        string origin = server.BaseUrl.TrimEnd('/');

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal(200, resp.StatusCode);
        Assert.Equal("GET /hop3|", Encoding.UTF8.GetString(resp.Body));

        Assert.Equal(3, resp.Redirects.Count);
        Assert.Equal(new[] { 302, 302, 302 }, resp.Redirects.Select(h => h.StatusCode));
        Assert.Equal(server.BaseUrl, resp.Redirects[0].From);
        Assert.Equal($"{origin}/hop1", resp.Redirects[0].To);
        Assert.Equal($"{origin}/hop1", resp.Redirects[1].From);
        Assert.Equal($"{origin}/hop2", resp.Redirects[1].To);
        Assert.Equal($"{origin}/hop2", resp.Redirects[2].From);
        Assert.Equal($"{origin}/hop3", resp.Redirects[2].To);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    public async Task A_301_or_302_turns_a_POST_into_a_GET_and_drops_the_body(int statusCode)
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartRedirectChainAsync(
            serverCert, clientCert.Thumbprint!, hops: 1, statusCode: statusCode);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest
            {
                Method = HttpMethod.Post,
                Url = server.BaseUrl,
                Body = "{\"posted\":true}",
                ContentType = "application/json"
            },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        // The server echoes "<METHOD> <path>|<body>", so this is what it actually received.
        Assert.Equal("GET /hop1|", Encoding.UTF8.GetString(resp.Body));
        Assert.Single(resp.Redirects);
        Assert.Equal(statusCode, resp.Redirects[0].StatusCode);
    }

    [Fact]
    public async Task A_303_turns_any_method_into_a_GET_and_drops_the_body()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartRedirectChainAsync(
            serverCert, clientCert.Thumbprint!, hops: 1, statusCode: 303);

        // A PUT, not a POST: 303 rewrites every method, which is what separates it from 301/302.
        var resp = await new ApiClient().SendAsync(
            new ApiRequest
            {
                Method = HttpMethod.Put,
                Url = server.BaseUrl,
                Body = "{\"put\":true}",
                ContentType = "application/json"
            },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal("GET /hop1|", Encoding.UTF8.GetString(resp.Body));
        Assert.Equal(303, resp.Redirects.Single().StatusCode);
    }

    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public async Task A_307_or_308_replays_the_same_method_and_body(int statusCode)
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartRedirectChainAsync(
            serverCert, clientCert.Thumbprint!, hops: 1, statusCode: statusCode);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest
            {
                Method = HttpMethod.Post,
                Url = server.BaseUrl,
                Body = "{\"posted\":true}",
                ContentType = "application/json"
            },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal("POST /hop1|{\"posted\":true}", Encoding.UTF8.GetString(resp.Body));
        Assert.Equal(statusCode, resp.Redirects.Single().StatusCode);
    }

    [Fact]
    public async Task Not_following_redirects_returns_the_3xx_response_itself()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartRedirectChainAsync(
            serverCert, clientCert.Thumbprint!, hops: 3);
        string origin = server.BaseUrl.TrimEnd('/');

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true, FollowRedirects = false });

        Assert.Equal(302, resp.StatusCode);
        Assert.Contains(resp.Headers, h =>
            h.Key.Equals("Location", StringComparison.OrdinalIgnoreCase) && h.Value == $"{origin}/hop1");
        Assert.Empty(resp.Redirects);
    }

    [Fact]
    public async Task Passing_the_redirect_limit_fails_with_the_hops_taken_so_far()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartRedirectChainAsync(
            serverCert, clientCert.Thumbprint!, hops: 5);
        string origin = server.BaseUrl.TrimEnd('/');

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true, MaxRedirects = 2 });

        Assert.False(resp.IsSuccess);
        Assert.Equal(ApiErrorKind.TooManyRedirects, resp.Error?.Kind);
        Assert.Null(resp.StatusCode);
        // The two hops that were taken are still reported: they say where the certificate went.
        Assert.Equal(2, resp.Redirects.Count);
        Assert.Equal($"{origin}/hop2", resp.Redirects[1].To);
    }

    [Fact]
    public async Task A_cross_origin_redirect_drops_the_authorization_header_and_says_so()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        // A second loopback server listens on its own port, so it is a genuinely different origin.
        await using var elsewhere = await LoopbackMtlsServer.StartEchoAsync(serverCert, clientCert.Thumbprint!);
        await using var server = await LoopbackMtlsServer.StartRedirectAsync(
            serverCert, clientCert.Thumbprint!, elsewhere.BaseUrl);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest
            {
                Method = HttpMethod.Get,
                Url = server.BaseUrl,
                Headers = new[] { new KeyValuePair<string, string>("Authorization", "Bearer secret-token") }
            },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        var hop = Assert.Single(resp.Redirects);
        Assert.Equal(elsewhere.BaseUrl, hop.To);
        Assert.True(hop.AuthorizationDropped);

        // The echo server replays the raw request, so this is what the other origin really saw.
        string received = Encoding.UTF8.GetString(resp.Body);
        Assert.StartsWith("GET / HTTP/1.1", received);
        Assert.DoesNotContain("Authorization", received, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-token", received);
    }

    [Fact]
    public async Task A_same_origin_redirect_is_not_flagged_as_dropping_the_authorization_header()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        // One server, so the redirect stays on the same scheme, host, and port.
        await using var server = await LoopbackMtlsServer.StartRedirectChainAsync(
            serverCert, clientCert.Thumbprint!, hops: 1);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest
            {
                Method = HttpMethod.Get,
                Url = server.BaseUrl,
                Headers = new[] { new KeyValuePair<string, string>("Authorization", "Bearer secret-token") }
            },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal("GET /hop1|", Encoding.UTF8.GetString(resp.Body));

        var hop = Assert.Single(resp.Redirects);
        var from = new Uri(hop.From);
        var to = new Uri(hop.To);
        Assert.Equal((from.Scheme, from.Host, from.Port), (to.Scheme, to.Host, to.Port));
        Assert.False(hop.AuthorizationDropped);
    }

    [Fact]
    public async Task A_redirect_from_https_to_http_is_flagged_as_a_scheme_downgrade()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        // Port 1 refuses the connection, so the hop is taken but the request never completes —
        // which is the point: the downgrade has to be reported even on a failed chain.
        await using var server = await LoopbackMtlsServer.StartRedirectAsync(
            serverCert, clientCert.Thumbprint!, "http://127.0.0.1:1/");

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.False(resp.IsSuccess);
        Assert.NotNull(resp.Error);

        var hop = Assert.Single(resp.Redirects);
        Assert.True(hop.SchemeDowngrade);
        Assert.Equal(server.BaseUrl, hop.From);
        Assert.Equal("http://127.0.0.1:1/", hop.To);
    }
}
