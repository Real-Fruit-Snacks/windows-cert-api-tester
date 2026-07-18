using System.IO;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class CookieCliTests
{
    private static string WriteTwoRequestSuite(LoopbackMtlsServer server, string clientThumb)
    {
        // Request "a" triggers the server's Set-Cookie; request "b" asserts the cookie was sent back.
        var state = new AppState();
        var folder = new CollectionNode { Name = "suite", IsFolder = true };
        folder.Children.Add(new CollectionNode
        {
            Name = "a",
            Request = new RequestModel { Method = "GET", Path = server.BaseUrl, IgnoreServerCert = true, CertThumbprint = clientThumb }
        });
        var b = new RequestModel { Method = "GET", Path = server.BaseUrl, IgnoreServerCert = true, CertThumbprint = clientThumb };
        b.Assertions.Add(new AssertionRule { Target = AssertTarget.BodyText, Op = AssertOp.Contains, Value = "srv=ok" });
        folder.Children.Add(new CollectionNode { Name = "b", Request = b });
        state.Collections.Add(folder);
        var ws = Path.Combine(Path.GetTempPath(), $"certapi-cookie-{Guid.NewGuid():N}.json");
        state.SaveTo(ws);
        return ws;
    }

    [Fact]
    public async Task Cookies_flag_carries_a_cookie_from_one_request_to_the_next()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CookieClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartCookieEchoAsync(serverCert, clientCert.Thumbprint!);

        var ws = WriteTwoRequestSuite(server, clientCert.Thumbprint!);
        try
        {
            var svc = new CliServices { LiveStatePath = ws, IsGuiRunning = () => false, FindCertificate = _ => clientCert };

            // With --cookies: request b receives srv=ok set by request a → its assertion passes.
            var so = new StringWriter();
            int code = CliApp.Run(new[] { "run", "--all", "--workspace", ws, "--no-record", "--cookies" }, so, new StringWriter(), services: svc);
            Assert.Equal(0, code);

            // Without --cookies: each request gets a fresh jar → no cookie sent → b's assertion fails.
            int code2 = CliApp.Run(new[] { "run", "--all", "--workspace", ws, "--no-record" },
                new StringWriter(), new StringWriter(), services: new CliServices { LiveStatePath = ws, IsGuiRunning = () => false, FindCertificate = _ => clientCert });
            Assert.Equal(1, code2);
        }
        finally { File.Delete(ws); }
    }
}
