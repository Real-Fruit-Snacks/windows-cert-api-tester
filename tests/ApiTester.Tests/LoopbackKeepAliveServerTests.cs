using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Proves the keep-alive test harness itself — never ApiClient — can see what only a raw
/// SocketsHttpHandler/HttpClient can show: that a connection was reused, and which client certificate
/// arrived on which connection. These stay green regardless of what happens to ApiClient later,
/// because they never touch it.</summary>
public class LoopbackKeepAliveServerTests
{
    [Fact]
    public async Task One_handler_serves_two_requests_on_one_connection()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert);

        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { clientCert },
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };
        using var http = new HttpClient(handler);

        string first = await http.GetStringAsync(server.BaseUrl);
        string second = await http.GetStringAsync(server.BaseUrl);

        Assert.Equal(1, server.ConnectionCount);
        Assert.Equal(2, server.TotalRequestCount);
        Assert.Contains("conn=1 req=1", first);
        Assert.Contains("conn=1 req=2", second);
        Assert.Equal(new[] { clientCert.Thumbprint }, server.ClientCertificateThumbprintsByConnection);
    }

    [Fact]
    public async Task Two_handlers_are_two_connections_each_with_its_own_client_certificate()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, true, false, new[] { "localhost" });
        using var clientCertA = SelfSignedCertificateFactory.CreateSignedCertificate("ClientA", ca, false, true);
        using var clientCertB = SelfSignedCertificateFactory.CreateSignedCertificate("ClientB", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert);

        var handlerA = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { clientCertA },
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };
        using var httpA = new HttpClient(handlerA);

        var handlerB = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { clientCertB },
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };
        using var httpB = new HttpClient(handlerB);

        string bodyA = await httpA.GetStringAsync(server.BaseUrl);
        string bodyB = await httpB.GetStringAsync(server.BaseUrl);

        Assert.Equal(2, server.ConnectionCount);
        Assert.Equal(2, server.TotalRequestCount);
        Assert.Contains("req=1", bodyA);
        Assert.Contains("req=1", bodyB);
        Assert.Equal(
            new HashSet<string?> { clientCertA.Thumbprint, clientCertB.Thumbprint },
            server.ClientCertificateThumbprintsByConnection.ToHashSet());
    }

    [Fact]
    public async Task A_pooled_connection_echoes_the_cookie_header_it_received()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert);

        // UseCookies = false: nothing automatic, so the second request's Cookie header is proof that
        // this specific request carried it — not that a cookie jar reattached it behind the scenes.
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { clientCert },
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };
        using var http = new HttpClient(handler);

        using var firstResponse = await http.GetAsync(server.BaseUrl);
        string first = await firstResponse.Content.ReadAsStringAsync();

        using var secondRequest = new HttpRequestMessage(HttpMethod.Get, server.BaseUrl);
        secondRequest.Headers.Add("Cookie", "srv=ok");
        using var secondResponse = await http.SendAsync(secondRequest);
        string second = await secondResponse.Content.ReadAsStringAsync();

        Assert.Contains("cookie=none", first);
        Assert.True(firstResponse.Headers.TryGetValues("Set-Cookie", out var setCookie));
        Assert.Contains("srv=ok", string.Join(";", setCookie!));
        Assert.Contains("cookie=srv=ok", second);
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task Disposing_the_server_while_a_connection_is_held_open_completes()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        var server = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert);

        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { clientCert },
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };
        var http = new HttpClient(handler);
        await http.GetStringAsync(server.BaseUrl);
        // http/handler are deliberately left open: the whole point is proving DisposeAsync returns
        // promptly even while a pooling client still holds the connection.

        var disposeTask = server.DisposeAsync().AsTask();
        // A liveness bound, not a performance claim: this only fails if disposal never returns.
        Task completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(disposeTask, completed);

        http.Dispose();
    }
}
