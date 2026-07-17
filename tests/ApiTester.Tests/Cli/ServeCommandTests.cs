using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class ServeCommandTests
{
    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    // Runs `serve` on a background thread with a gateway pointed at the given loopback mTLS server,
    // returns a cancel action + the completion task so the test can stop it.
    private static (CancellationTokenSource cts, Task<int> run, int port) StartServe(
        LoopbackMtlsServer upstream, X509Certificate2 clientCert, string? token = null)
    {
        int port = FreePort();
        var cts = new CancellationTokenSource();
        var services = new CliServices
        {
            Cancel = cts.Token,
            ListCertificates = _ => new[]
            {
                new CertificateInfo
                {
                    Subject = "CN=GatewayClient", Issuer = "CN=CA", Thumbprint = clientCert.Thumbprint!,
                    NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                    HasClientAuthEku = true, Certificate = clientCert
                }
            },
            // Ignore the requested upstream URL; always forward to the test loopback server.
            GatewayFactory = (_, cert, _, timeout) =>
                new MtlsGateway(new Uri(upstream.BaseUrl), cert, ignoreServerCertificateErrors: true, timeout)
        };

        var args = new List<string> { "serve", "https://placeholder", "--port", port.ToString(),
                                      "--cert", "GatewayClient", "--insecure", "-q" };
        if (token is not null) { args.Add("--token"); args.Add(token); }

        var run = Task.Run(() => CliApp.Run(args.ToArray(), TextWriter.Null, TextWriter.Null, services: services));
        return (cts, run, port);
    }

    private static (X509Certificate2 ca, X509Certificate2 server, X509Certificate2 client) Certs()
    {
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        var server = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        var client = SelfSignedCertificateFactory.CreateSignedCertificate("GatewayClient", ca, false, true);
        return (ca, server, client);
    }

    [Fact]
    public async Task Forwards_a_local_request_to_the_mtls_upstream()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartAsync(server, client.Thumbprint!, "{\"served\":true}");
            var (cts, run, port) = StartServe(upstream, client);
            try
            {
                using var http = new HttpClient();
                // Give the listener a moment to bind.
                string body = await Poll(async () => await http.GetStringAsync($"http://127.0.0.1:{port}/todo"));
                Assert.Contains("served", body);
            }
            finally { cts.Cancel(); await run; }
        }
    }

    [Fact]
    public async Task Missing_token_is_rejected_with_401()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartAsync(server, client.Thumbprint!, "ok");
            var (cts, run, port) = StartServe(upstream, client, token: "s3cret");
            try
            {
                using var http = new HttpClient();
                var resp = await Poll(async () =>
                {
                    var r = await http.GetAsync($"http://127.0.0.1:{port}/x");
                    return r;   // any HTTP answer means the listener is up
                });
                Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            }
            finally { cts.Cancel(); await run; }
        }
    }

    [Fact]
    public void Missing_port_is_a_usage_error()
    {
        int code = CliApp.Run(new[] { "serve", "https://x" }, TextWriter.Null, new StringWriter(), services: new CliServices());
        Assert.Equal(2, code);
    }

    // Retry a call for up to ~5s while the listener finishes binding.
    private static async Task<T> Poll<T>(Func<Task<T>> action)
    {
        Exception? last = null;
        for (int i = 0; i < 50; i++)
        {
            try { return await action(); }
            catch (Exception ex) { last = ex; await Task.Delay(100); }
        }
        throw last!;
    }
}
