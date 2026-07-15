using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

public class LoopbackMtlsServerTests
{
    [Fact]
    public async Task Completes_mtls_and_returns_body()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(
            serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { clientCert },
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };
        using var http = new HttpClient(handler);
        var text = await http.GetStringAsync(server.BaseUrl);

        Assert.Contains("ok", text);
    }
}
