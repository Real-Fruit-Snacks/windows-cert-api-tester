using System.Net.Http;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

public class MtlsBrowserSessionTests
{
    [Fact]
    public async Task Fetch_presents_client_certificate_and_returns_body()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(
            serverCert, clientCert.Thumbprint!, "<html><body>hello</body></html>");

        using var session = new MtlsBrowserSession(clientCert, ignoreServerCertificateErrors: true);
        var result = await session.FetchAsync(
            "GET", new Uri(server.BaseUrl),
            Array.Empty<KeyValuePair<string, string>>(), null, null, default);

        Assert.Equal(200, result.StatusCode);
        Assert.Contains("hello", Encoding.UTF8.GetString(result.Body));
    }

    [Fact]
    public async Task Fetch_without_required_client_cert_is_rejected()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(
            serverCert, clientCert.Thumbprint!, "ok");

        using var session = new MtlsBrowserSession(clientCertificate: null, ignoreServerCertificateErrors: true);

        await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
            await session.FetchAsync("GET", new Uri(server.BaseUrl),
                Array.Empty<KeyValuePair<string, string>>(), null, null, default));
    }
}
