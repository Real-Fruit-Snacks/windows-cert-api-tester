using System.IO;
using System.Net;
using System.Net.Http;
using ApiTester.Cli;

namespace ApiTester.Tests.Cli;

public class MockHarCliTests
{
    private const string OneEntryHar = """
        {"log":{"version":"1.2","entries":[{"startedDateTime":"2026-01-01T00:00:00Z","time":1,"request":{"method":"GET","url":"https://api.example.com/orders","headers":[],"queryString":[]},"response":{"status":201,"statusText":"Created","headers":[{"name":"X-Order-Source","value":"har-fixture"}],"content":{"size":0,"mimeType":"application/json","text":"{\"id\":42}"}},"timings":{"send":-1,"wait":1,"receive":-1}}]}}
        """;

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

    private static string WriteHar(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"certapi-mockhar-{Guid.NewGuid():N}.har");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public async Task Recorded_response_comes_back_for_a_matching_request()
    {
        string har = WriteHar(OneEntryHar);
        int port = FreePort();
        try
        {
            var (cts, run, stderr) = StartMock("mock", "--har", har, "--port", port.ToString());
            try
            {
                using var http = new HttpClient();
                var resp = await GetWithRetryAsync(http, $"http://127.0.0.1:{port}/orders");

                Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
                Assert.Equal("har-fixture", resp.Headers.GetValues("X-Order-Source").Single());
                string body = await resp.Content.ReadAsStringAsync();
                Assert.Contains("\"id\":42", body);
            }
            finally { cts.Cancel(); await run; }

            string log = stderr.ToString();
            Assert.Contains("replaying 1 recorded responses from", log);
            Assert.Contains("exact-match", log);
        }
        finally { File.Delete(har); }
    }

    [Fact]
    public async Task A_miss_returns_the_configured_no_match_status()
    {
        string har = WriteHar(OneEntryHar);
        int port = FreePort();
        try
        {
            var (cts, run, stderr) = StartMock("mock", "--har", har, "--port", port.ToString(), "--no-match-status", "418");
            try
            {
                using var http = new HttpClient();
                var resp = await GetWithRetryAsync(http, $"http://127.0.0.1:{port}/nowhere");
                Assert.Equal((HttpStatusCode)418, resp.StatusCode);
            }
            finally { cts.Cancel(); await run; }

            Assert.Contains("miss", stderr.ToString());
        }
        finally { File.Delete(har); }
    }

    [Fact]
    public async Task Malformed_archive_is_a_data_error_without_starting_a_listener()
    {
        string har = WriteHar("{ not json ");
        try
        {
            var stderr = new StringWriter();
            var cts = new CancellationTokenSource();
            var run = Task.Run(() => CliApp.Run(new[] { "mock", "--har", har }, new StringWriter(), stderr,
                                                 services: new CliServices { Cancel = cts.Token }));

            int code = await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(3, code);
            string message = stderr.ToString();
            Assert.NotEmpty(message);
            Assert.DoesNotContain("   at ", message);
        }
        finally { File.Delete(har); }
    }

    [Fact]
    public void Missing_har_file_is_a_data_error()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"certapi-mockhar-missing-{Guid.NewGuid():N}.har");
        var stderr = new StringWriter();
        int code = CliApp.Run(new[] { "mock", "--har", missing }, new StringWriter(), stderr,
                               services: new CliServices());
        Assert.Equal(3, code);
    }

    [Fact]
    public void No_match_status_without_har_is_a_usage_error()
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(new[] { "mock", "--no-match-status", "418" }, new StringWriter(), stderr,
                               services: new CliServices());
        Assert.Equal(2, code);
    }

    [Fact]
    public void No_match_status_out_of_range_is_a_usage_error()
    {
        string har = WriteHar(OneEntryHar);
        try
        {
            var stderr = new StringWriter();
            int code = CliApp.Run(new[] { "mock", "--har", har, "--no-match-status", "999" }, new StringWriter(), stderr,
                                   services: new CliServices());
            Assert.Equal(2, code);
        }
        finally { File.Delete(har); }
    }
}
