using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>The shared rig for end-to-end `certapi serve` tests. It lives here rather than inside
/// one suite because how `serve` is started — the fixed flags, the stubbed certificate store, the
/// bounded shutdown — is a contract every serve suite depends on, and a copy per suite means a
/// change to that contract can be made in one place and silently missed in another.</summary>
internal static class ServeFixture
{
    /// <summary>Every network wait is bounded, so a gateway that never answers fails the test rather
    /// than hanging the suite.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    public static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public static (X509Certificate2 ca, X509Certificate2 server, X509Certificate2 client) Certs()
    {
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        var server = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        var client = SelfSignedCertificateFactory.CreateSignedCertificate("GatewayClient", ca, false, true);
        return (ca, server, client);
    }

    /// <summary>A client that leaves the gateway's answer alone: redirects are not followed and
    /// cookies are not swallowed by a container, so Location and Set-Cookie can be asserted as sent.</summary>
    public static HttpClient NewClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Limit };

    /// <summary>Every value of a response header, in order — the count matters as much as the value
    /// when a duplicate is the browser-breaking failure.</summary>
    public static string[] HeaderValues(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.ToArray() : Array.Empty<string>();

    /// <summary>Retry while the listener finishes binding.</summary>
    public static async Task<T> Poll<T>(Func<Task<T>> action)
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

/// <summary>`serve` running on a background thread with its real gateway, so the routes, browser
/// options and header rules under test are the ones the command built from its own flags.</summary>
internal sealed class ServeHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task<int> _run;

    public int Port { get; }
    public string Origin => $"http://127.0.0.1:{Port}";

    private ServeHost(X509Certificate2 clientCert, IEnumerable<string> extraArgs)
    {
        Port = ServeFixture.FreePort();
        var services = new CliServices
        {
            Cancel = _cts.Token,
            ListCertificates = _ => new[]
            {
                new CertificateInfo
                {
                    Subject = "CN=GatewayClient", Issuer = "CN=CA", Thumbprint = clientCert.Thumbprint!,
                    NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                    HasClientAuthEku = true, Certificate = clientCert
                }
            }
        };
        var args = new List<string> { "serve", "--port", Port.ToString(),
                                      "--cert", "GatewayClient", "--insecure", "-q" };
        args.AddRange(extraArgs);
        _run = Task.Run(() => CliApp.Run(args.ToArray(), TextWriter.Null, TextWriter.Null, services: services));
    }

    public static ServeHost Start(X509Certificate2 clientCert, params string[] extraArgs) =>
        new(clientCert, extraArgs);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _run.WaitAsync(ServeFixture.Limit); } catch { /* the test's assertions are the verdict */ }
        _cts.Dispose();
    }
}
