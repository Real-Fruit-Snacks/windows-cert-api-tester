using System.IO;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class RunCommandTests
{
    private sealed class Loopback : IAsyncDisposable
    {
        public LoopbackMtlsServer Server = null!;
        public System.Security.Cryptography.X509Certificates.X509Certificate2 Client = null!;
        private System.Security.Cryptography.X509Certificates.X509Certificate2 _ca = null!, _server = null!;

        public static async Task<Loopback> StartAsync()
        {
            var l = new Loopback();
            l._ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
            l._server = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", l._ca, true, false, new[] { "localhost" });
            l.Client = SelfSignedCertificateFactory.CreateSignedCertificate("RunClient", l._ca, false, true);
            l.Server = await LoopbackMtlsServer.StartAsync(l._server, l.Client.Thumbprint!);
            return l;
        }
        public async ValueTask DisposeAsync() { await Server.DisposeAsync(); _ca.Dispose(); _server.Dispose(); Client.Dispose(); }
    }

    private static string WriteState(string url, string? certThumb)
    {
        var state = new AppState();
        var folder = new CollectionNode { Name = "suite", IsFolder = true };
        folder.Children.Add(new CollectionNode
        {
            Name = "ok",
            Request = new RequestModel { Method = "GET", Path = url, IgnoreServerCert = true, CertThumbprint = certThumb }
        });
        folder.Children.Add(new CollectionNode
        {
            Name = "down",
            Request = new RequestModel { Method = "GET", Path = "https://127.0.0.1:1/", TimeoutSeconds = 2 }
        });
        state.Collections.Add(folder);
        var path = Path.Combine(Path.GetTempPath(), $"certapi-run-{Guid.NewGuid():N}.json");
        state.SaveTo(path);
        return path;
    }

    private static CliServices Services(Loopback l, bool guiRunning, string livePath) => new()
    {
        FindCertificate = _ => l.Client,
        IsGuiRunning = () => guiRunning,
        LiveStatePath = livePath
    };

    [Fact]
    public async Task Suite_prints_pass_fail_and_exits_1_when_any_endpoint_fails()
    {
        await using var l = await Loopback.StartAsync();
        var live = WriteState(l.Server.BaseUrl, l.Client.Thumbprint);
        try
        {
            var so = new StringWriter(); var se = new StringWriter();
            int code = CliApp.Run(new[] { "run", "suite" }, so, se, services: Services(l, false, live));
            Assert.Equal(1, code);
            Assert.Contains("PASS", so.ToString());
            Assert.Contains("FAIL", so.ToString());
            Assert.Contains("1 passed", so.ToString());
            Assert.Contains("1 failed", so.ToString());
        }
        finally { File.Delete(live); }
    }

    [Fact]
    public async Task Live_state_runs_record_results_unless_the_gui_is_open()
    {
        await using var l = await Loopback.StartAsync();
        var live = WriteState(l.Server.BaseUrl, l.Client.Thumbprint);
        try
        {
            CliApp.Run(new[] { "run", "suite/ok" }, new StringWriter(), new StringWriter(),
                       services: Services(l, guiRunning: false, live));
            var afterRecord = AppState.LoadFrom(live);
            Assert.Equal(200, afterRecord.Collections[0].Children[0].LastStatusCode);

            var se = new StringWriter();
            CliApp.Run(new[] { "run", "suite/down" }, new StringWriter(), se,
                       services: Services(l, guiRunning: true, live));
            var afterSkip = AppState.LoadFrom(live);
            Assert.Null(afterSkip.Collections[0].Children[1].LastCheckedUtc);   // not recorded
            Assert.Contains("GUI", se.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(live); }
    }

    [Fact]
    public async Task Workspace_runs_are_read_only_unless_record()
    {
        await using var l = await Loopback.StartAsync();
        var ws = WriteState(l.Server.BaseUrl, l.Client.Thumbprint);
        try
        {
            var services = Services(l, false, Path.Combine(Path.GetTempPath(), "unused-live.json"));
            CliApp.Run(new[] { "run", "suite/ok", "--workspace", ws }, new StringWriter(), new StringWriter(), services: services);
            Assert.Null(AppState.LoadFrom(ws).Collections[0].Children[0].LastCheckedUtc);

            CliApp.Run(new[] { "run", "suite/ok", "--workspace", ws, "--record" }, new StringWriter(), new StringWriter(), services: services);
            Assert.Equal(200, AppState.LoadFrom(ws).Collections[0].Children[0].LastStatusCode);
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public async Task Json_output_is_machine_readable()
    {
        await using var l = await Loopback.StartAsync();
        var live = WriteState(l.Server.BaseUrl, l.Client.Thumbprint);
        try
        {
            var so = new StringWriter();
            CliApp.Run(new[] { "run", "suite/ok", "--json" }, so, new StringWriter(), services: Services(l, false, live));
            using var doc = System.Text.Json.JsonDocument.Parse(so.ToString());
            var result = Assert.Single(doc.RootElement.GetProperty("results").EnumerateArray());
            Assert.True(result.GetProperty("passed").GetBoolean());
            Assert.Equal("suite/ok", result.GetProperty("path").GetString());
        }
        finally { File.Delete(live); }
    }
}
