using System.Net.Http;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

public class MockServerTests
{
    [Fact]
    public async Task Http_echo_reflects_the_request()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var resp = await new ApiClient().SendAsync(new ApiRequest
        {
            Method = HttpMethod.Post,
            Url = srv.BaseUrl + "hello?x=1",
            Headers = new[] { new KeyValuePair<string, string>("X-Test", "abc") },
            Body = "{\"ping\":true}",
            ContentType = "application/json"
        }, clientCertificate: null);

        Assert.True(resp.IsSuccess);
        string body = Encoding.UTF8.GetString(resp.Body);
        Assert.Contains("\"method\": \"POST\"", body);
        Assert.Contains("\"path\": \"/hello\"", body);
        Assert.Contains("\"query\": \"x=1\"", body);
        Assert.Contains("\"X-Test\": \"abc\"", body);
        Assert.Contains("ping", body);
        Assert.Contains("certapi mock", body);
    }

    [Fact]
    public async Task Status_route_returns_the_requested_code()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = srv.BaseUrl + "status/503" }, null);
        Assert.Equal(503, resp.StatusCode);
    }

    [Fact]
    public async Task Token_route_returns_an_oauth_token()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var result = await OAuthClient.RequestTokenAsync(new OAuthRequest
        {
            Grant = OAuthGrant.ClientCredentials,
            TokenEndpoint = srv.BaseUrl + "token",
            ClientId = "anything"
        });
        Assert.True(result.Success, result.FailureMessage);
        Assert.Equal("mock-access-token", result.AccessToken);
        Assert.Equal(3600, result.ExpiresInSeconds);
    }

    [Fact]
    public async Task Sse_route_streams_events()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var events = new List<SseEvent>();
        await foreach (var ev in SseClient.StreamAsync(srv.BaseUrl + "sse"))
            events.Add(ev);
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal("tick", e.Event));
    }

    [Fact]
    public async Task Https_echo_works_over_tls()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Mock CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        await using var srv = MockServer.Start(0, MockTlsMode.Https, serverCert);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = srv.BaseUrl },
            clientCertificate: null, ignoreServerCertificateErrors: true);

        Assert.True(resp.IsSuccess);
        Assert.Contains("certapi mock", Encoding.UTF8.GetString(resp.Body));
    }

    [Fact]
    public async Task Mtls_echo_reports_the_presented_client_certificate()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Mock CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Mock Client", ca, false, true);
        await using var srv = MockServer.Start(0, MockTlsMode.Mtls, serverCert);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = srv.BaseUrl },
            clientCert, ignoreServerCertificateErrors: true);

        Assert.True(resp.IsSuccess);
        string body = Encoding.UTF8.GetString(resp.Body);
        Assert.Contains("CN=Mock Client", body);   // the server echoes the presented cert subject
    }

    [Fact]
    public async Task Mtls_rejects_a_request_with_no_client_certificate()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Mock CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        await using var srv = MockServer.Start(0, MockTlsMode.Mtls, serverCert);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = srv.BaseUrl },
            clientCertificate: null, ignoreServerCertificateErrors: true);

        Assert.False(resp.IsSuccess);   // the handshake is refused without a client cert
    }

    [Fact]
    public async Task WebSocket_route_echoes_frames()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        await using var session = new WebSocketSession();
        await session.ConnectAsync("ws://127.0.0.1:" + srv.Port + "/ws");
        await session.SendTextAsync("ahoy");

        string? echoed = null;
        await foreach (var msg in session.ReceiveAllAsync())
        {
            if (msg.IsClose) break;
            echoed = msg.Text;
            break;
        }
        await session.CloseAsync();
        Assert.Equal("ahoy", echoed);
    }
}
