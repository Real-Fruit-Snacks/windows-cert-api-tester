using System.IO;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

// Proves the v1.57.0 headline defect is fixed: `certapi run` against a suite of saved requests to
// the same host now shares one pooled connection instead of opening a fresh one per request. Every
// assertion here is what the server itself observed (LoopbackMtlsServer.StartKeepAliveAsync's
// counters) -- never a wall clock, never internal cache state.
public class RunPoolingCliTests
{
    private static string WriteTwoRequestSuite(LoopbackMtlsServer server, string clientThumb)
    {
        var state = new AppState();
        var folder = new CollectionNode { Name = "suite", IsFolder = true };
        folder.Children.Add(new CollectionNode
        {
            Name = "a",
            Request = new RequestModel { Method = "GET", Path = server.BaseUrl, IgnoreServerCert = true, CertThumbprint = clientThumb }
        });
        folder.Children.Add(new CollectionNode
        {
            Name = "b",
            Request = new RequestModel { Method = "GET", Path = server.BaseUrl, IgnoreServerCert = true, CertThumbprint = clientThumb }
        });
        state.Collections.Add(folder);
        var ws = Path.Combine(Path.GetTempPath(), $"certapi-run-pooling-{Guid.NewGuid():N}.json");
        state.SaveTo(ws);
        return ws;
    }

    [Fact]
    public async Task A_two_request_suite_reuses_one_connection()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("RunPoolingClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert);

        var ws = WriteTwoRequestSuite(server, clientCert.Thumbprint!);
        try
        {
            var svc = new CliServices { LiveStatePath = ws, IsGuiRunning = () => false, FindCertificate = _ => clientCert };

            int code = CliApp.Run(new[] { "run", "--all", "--workspace", ws, "--no-record" }, new StringWriter(), new StringWriter(), services: svc);

            Assert.Equal(0, code);
            // The user-visible proof: a two-request suite against one host performs one TLS
            // handshake, not two -- the "20-request suite performs 20 handshakes" defect, fixed.
            Assert.Equal(1, server.ConnectionCount);
            Assert.Equal(2, server.TotalRequestCount);
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public async Task Two_separate_clients_running_the_same_suite_never_share_a_connection()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("RunPoolingClient2", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert);

        var ws = WriteTwoRequestSuite(server, clientCert.Thumbprint!);
        try
        {
            var svc1 = new CliServices { LiveStatePath = ws, IsGuiRunning = () => false, FindCertificate = _ => clientCert };
            int code1 = CliApp.Run(new[] { "run", "--all", "--workspace", ws, "--no-record" }, new StringWriter(), new StringWriter(), services: svc1);
            Assert.Equal(0, code1);
            Assert.Equal(1, server.ConnectionCount);

            // A second, separate CliServices/ApiClient running the identical suite must open its own
            // connection rather than reusing the first client's pool -- proof that the memoized
            // predicate is scoped per run, not accidentally shared across runs.
            var svc2 = new CliServices { LiveStatePath = ws, IsGuiRunning = () => false, FindCertificate = _ => clientCert };
            int code2 = CliApp.Run(new[] { "run", "--all", "--workspace", ws, "--no-record" }, new StringWriter(), new StringWriter(), services: svc2);
            Assert.Equal(0, code2);
            Assert.Equal(2, server.ConnectionCount);
        }
        finally { File.Delete(ws); }
    }
}
