using System.IO;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>e2e coverage for <c>certapi run &lt;file.har&gt;</c> replay, the per-request trust
/// consult on a normal collection run, and <c>--har</c> capture on <c>run</c>. Uses a real
/// <see cref="ApiClient"/> against a real <see cref="LoopbackMtlsServer"/> — never a mock of
/// ApiClient, HarReader, HarWriter, or TrustService.</summary>
public class RunHarCliTests
{
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
    private static string TempHarPath() => Path.Combine(Path.GetTempPath(), $"certapi-run-har-{Guid.NewGuid():N}.har");

    [Fact]
    public async Task Har_replay_sends_each_entry_with_the_client_certificate_and_a_pinned_server_cert()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("ReplayClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        // Pin the server's certificate for this host so the replay succeeds without --insecure —
        // the same harness HarCaptureCliTests uses for the send-side trust tests.
        string workspace = TempState();
        var pinned = new AppState();
        TrustService.Trust(pinned, "127.0.0.1", serverCert);
        pinned.SaveTo(workspace);

        // The client certificate is fed from a file — a captured browser session is replayed
        // outside the Windows certificate store.
        var pfx = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pfx");
        File.WriteAllBytes(pfx, clientCert.Export(X509ContentType.Pkcs12, "pw"));

        // A minimal HAR whose single entry targets the loopback server, built with the real HAR
        // model/writer (not a hand-typed JSON string).
        string harPath = TempHarPath();
        var entry = new HarEntry
        {
            StartedDateTime = DateTimeOffset.UtcNow,
            Request = new HarRequest { Method = "GET", Url = server.BaseUrl },
            Response = new HarResponse { Status = 0 }
        };
        File.WriteAllText(harPath, HarWriter.Write(new[] { entry }, "1.52.0"));

        var services = new CliServices { LiveStatePath = TempState() };
        try
        {
            var so = new StringWriter();
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "run", harPath, "--workspace", workspace, "--cert-file", pfx, "--cert-password", "pw" },
                so, se, services: services);

            Assert.Equal(0, code);
            Assert.Contains("PASS", so.ToString());
        }
        finally
        {
            File.Delete(workspace);
            File.Delete(pfx);
            if (File.Exists(harPath)) File.Delete(harPath);
        }
    }

    [Fact]
    public void A_malformed_har_is_a_data_error_with_no_stack_trace()
    {
        string harPath = Path.Combine(Path.GetTempPath(), $"certapi-run-har-bad-{Guid.NewGuid():N}.har");
        File.WriteAllText(harPath, "{ not json");
        try
        {
            var so = new StringWriter();
            var se = new StringWriter();
            int code = CliApp.Run(new[] { "run", harPath }, so, se, services: new CliServices { LiveStatePath = TempState() });

            Assert.Equal(3, code);
            Assert.NotEmpty(se.ToString().Trim());
            Assert.DoesNotContain("   at ", se.ToString());
        }
        finally { File.Delete(harPath); }
    }

    [Fact]
    public void A_har_with_zero_entries_is_a_data_error()
    {
        string harPath = Path.Combine(Path.GetTempPath(), $"certapi-run-har-empty-{Guid.NewGuid():N}.har");
        File.WriteAllText(harPath, "{\"log\":{\"version\":\"1.2\",\"entries\":[]}}");
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(new[] { "run", harPath }, new StringWriter(), se, services: new CliServices { LiveStatePath = TempState() });

            Assert.Equal(3, code);
            Assert.Contains("no entries", se.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(harPath); }
    }

    [Fact]
    public async Task Run_har_captures_the_suite_into_a_well_formed_archive()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("RunClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        var state = new AppState();
        var folder = new CollectionNode { Name = "suite", IsFolder = true };
        folder.Children.Add(new CollectionNode
        {
            Name = "ok",
            Request = new RequestModel { Method = "GET", Path = server.BaseUrl, IgnoreServerCert = true, CertThumbprint = clientCert.Thumbprint }
        });
        state.Collections.Add(folder);
        string ws = Path.Combine(Path.GetTempPath(), $"certapi-run-har-suite-{Guid.NewGuid():N}.json");
        state.SaveTo(ws);

        var services = new CliServices
        {
            FindCertificate = _ => clientCert,
            IsGuiRunning = () => false,
            LiveStatePath = TempState()
        };

        string harPath = TempHarPath();
        try
        {
            var so = new StringWriter();
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "run", "suite", "--workspace", ws, "--no-record", "--har", harPath }, so, se, services: services);

            Assert.Equal(0, code);
            var har = HarReader.Parse(File.ReadAllText(harPath));
            Assert.Single(har.Log.Entries);
            Assert.Equal(200, har.Log.Entries[0].Response.Status);
        }
        finally
        {
            File.Delete(ws);
            if (File.Exists(harPath)) File.Delete(harPath);
        }
    }
}
