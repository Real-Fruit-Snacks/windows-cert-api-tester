using System.IO;
using System.Net;
using System.Net.Http;
using ApiTester.Cli;

namespace ApiTester.Tests.Cli;

/// <summary>Regression guard for a confirmed defect: `MockCommand.Help` documents an `--http` flag
/// as an explicit selector for the default plain-HTTP mode, but the parser never consumed it, so
/// the token survived to <see cref="Args.Positionals"/> and `certapi mock --http` was rejected as
/// an unknown option — following the tool's own documentation produced a usage error. These tests
/// assert only on what a caller observes: the process exit code, its stderr text, and (for the
/// success case) a real HTTP response from the mock server that `--http` started.</summary>
public class MockTlsModeCliTests
{
    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static (CancellationTokenSource Cts, Task<int> Run, StringWriter Stderr) StartMock(params string[] args)
    {
        var cts = new CancellationTokenSource();
        var stderr = new StringWriter();
        var run = Task.Run(() => CliApp.Run(args, new StringWriter(), stderr,
                                            services: new CliServices { Cancel = cts.Token }));
        return (cts, run, stderr);
    }

    // Retry a call for up to ~5s while the listener finishes binding, so the test never races it.
    private static async Task<HttpResponseMessage> GetWithRetryAsync(HttpClient http, string url)
    {
        Exception? last = null;
        for (int i = 0; i < 50; i++)
        {
            try { return await http.GetAsync(url); }
            catch (HttpRequestException ex) { last = ex; await Task.Delay(100); }
        }
        throw last!;
    }

    [Fact]
    public async Task Http_flag_selects_the_default_plain_http_mode()
    {
        int port = FreePort();
        var (cts, run, stderr) = StartMock("mock", "--http", "--port", port.ToString());
        try
        {
            using var http = new HttpClient();
            var resp = await GetWithRetryAsync(http, $"http://127.0.0.1:{port}/anything");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            string body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("/anything", body);
        }
        finally { cts.Cancel(); await run; }

        Assert.Equal(0, await run);
        Assert.Contains("(Http)", stderr.ToString());
    }

    [Theory]
    [InlineData("--tls")]
    [InlineData("--mtls")]
    public void Http_combined_with_tls_or_mtls_is_a_usage_error(string other)
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(new[] { "mock", "--http", other }, new StringWriter(), stderr,
                               services: new CliServices());

        Assert.Equal(2, code);
        Assert.Contains("mutually exclusive", stderr.ToString());
    }
}
