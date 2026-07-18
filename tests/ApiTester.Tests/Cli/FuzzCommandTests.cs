using System.IO;
using System.Text;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class FuzzCommandTests
{
    // The loopback server returns 200 for every path; we distinguish "found" vs "not found"
    // by pointing the wordlist at the server and using bogus absolute-URL entries for misses.
    private static async Task<(int Code, string Out, string Err)> RunAsync(
        string[] args, string wordlist, string statePath, string? stdin = null)
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        var wlPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        File.WriteAllText(wlPath, wordlist);
        try
        {
            var services = new CliServices
            {
                LiveStatePath = statePath,
                IsGuiRunning = () => false,
                ListCertificates = _ => new[]
                {
                    new CertificateInfo
                    {
                        Subject = "CN=CliClient", Issuer = "CN=CA", Thumbprint = clientCert.Thumbprint!,
                        NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                        HasClientAuthEku = true, Certificate = clientCert
                    }
                }
            };
            var so = new StringWriter();
            var se = new StringWriter();
            var reader = new StringReader(stdin ?? "");
            var full = args.Select(a => a.Replace("{URL}", server.BaseUrl).Replace("{WL}", wlPath)).ToArray();
            int code = CliApp.Run(full, reader, so, se, new MemoryStream(), services);
            return (code, so.ToString(), se.ToString());
        }
        finally { File.Delete(wlPath); }
    }

    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    [Fact]
    public async Task Discovers_reachable_paths_and_hides_404s_by_default()
    {
        var state = TempState();
        try
        {
            // "/" reaches the loopback (200). A bogus off-host absolute URL fails to connect (Error).
            var r = await RunAsync(new[] { "fuzz", "{URL}", "-w", "{WL}", "--cert", "CliClient", "--insecure" },
                "/\nhttps://127.0.0.1:1/nope", state);
            Assert.Equal(0, r.Code);
            Assert.Contains("200", r.Out);
            Assert.Contains("discovered", r.Err.ToLowerInvariant() + r.Out.ToLowerInvariant());
        }
        finally { if (File.Exists(state)) File.Delete(state); }
    }

    [Fact]
    public async Task Json_output_lists_results_and_summary()
    {
        var state = TempState();
        try
        {
            var r = await RunAsync(new[] { "fuzz", "{URL}", "-w", "{WL}", "--cert", "CliClient", "--insecure", "--json", "--all" },
                "/", state);
            Assert.Equal(0, r.Code);
            using var doc = System.Text.Json.JsonDocument.Parse(r.Out);
            Assert.True(doc.RootElement.GetProperty("results").GetArrayLength() >= 1);
            Assert.True(doc.RootElement.GetProperty("summary").GetProperty("total").GetInt32() >= 1);
        }
        finally { if (File.Exists(state)) File.Delete(state); }
    }

    [Fact]
    public async Task Reads_wordlist_from_stdin()
    {
        var state = TempState();
        try
        {
            var r = await RunAsync(new[] { "fuzz", "{URL}", "-w", "-", "--cert", "CliClient", "--insecure", "--all" },
                wordlist: "unused", statePath: state, stdin: "/\n/health");
            Assert.Equal(0, r.Code);
            Assert.Contains("/health", r.Out);
        }
        finally { if (File.Exists(state)) File.Delete(state); }
    }

    [Fact]
    public async Task Save_collection_writes_discovered_endpoints()
    {
        var state = TempState();
        try
        {
            var r = await RunAsync(new[] { "fuzz", "{URL}", "-w", "{WL}", "--cert", "CliClient", "--insecure",
                "--save-collection", "Discovered" }, "/\n/health", state);
            Assert.Equal(0, r.Code);
            var saved = AppState.LoadFrom(state);
            var folder = saved.Collections.FirstOrDefault(c => c.Name == "Discovered");
            Assert.NotNull(folder);
            Assert.True(CountLeaves(folder!) >= 1);
        }
        finally { if (File.Exists(state)) File.Delete(state); }

        static int CountLeaves(CollectionNode n) => n.IsFolder ? n.Children.Sum(CountLeaves) : 1;
    }

    [Fact]
    public async Task All_transport_errors_exits_1()
    {
        var state = TempState();
        try
        {
            // Base URL is the loopback but every entry overrides with an unreachable absolute URL.
            var r = await RunAsync(new[] { "fuzz", "{URL}", "-w", "{WL}", "--cert", "CliClient", "--insecure" },
                "https://127.0.0.1:1/a\nhttps://127.0.0.1:1/b", state);
            Assert.Equal(1, r.Code);
        }
        finally { if (File.Exists(state)) File.Delete(state); }
    }

    [Fact]
    public void Missing_wordlist_file_is_a_data_error()
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(new[] { "fuzz", "https://x.example", "-w", "C:\\no\\such\\file.txt" },
            new StringReader(""), so, se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });
        Assert.Equal(3, code);
    }

    [Fact]
    public void Missing_wordlist_option_is_a_usage_error()
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(new[] { "fuzz", "https://x.example" }, new StringReader(""), so, se, new MemoryStream(),
            new CliServices { LiveStatePath = TempState() });
        Assert.Equal(2, code);
    }

    [Fact]
    public void Help_has_examples()
    {
        Assert.Contains("Examples:", FuzzCommand.Help);
        Assert.Contains("certapi fuzz", FuzzCommand.Help);
        Assert.Contains("--debug", FuzzCommand.Help);
    }
}
