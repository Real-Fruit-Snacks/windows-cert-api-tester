using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>e2e coverage for the command-line surfaces of response diffing: baseline resolution for
/// <c>certapi send --diff</c>, the exit codes it produces, <c>certapi run --diff-har</c>, and the
/// known-good snapshot <c>run</c> records for <c>--diff known-good</c> to compare against. Uses a
/// real <see cref="ApiClient"/> against a real <see cref="LoopbackMtlsServer"/> and real files on
/// disk — never a fake ApiClient and never a stubbed ResponseDiff.</summary>
public class DiffCliTests
{
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
    private static string TempPath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"certapi-diff-{Guid.NewGuid():N}{extension}");

    /// <summary>The CLI seams a test is allowed to move: a temp state file, a certificate lookup that
    /// finds the loopback client cert, and "the GUI is not running". Everything else — the client, the
    /// diff engine, the files — is the real thing.</summary>
    private static CliServices ServicesFor(X509Certificate2 clientCert, string liveStatePath) => new()
    {
        LiveStatePath = liveStatePath,
        IsGuiRunning = () => false,
        FindCertificate = _ => clientCert,
        ListCertificates = _ => new[]
        {
            new CertificateInfo
            {
                Subject = "CN=DiffClient", Issuer = "CN=CA", Thumbprint = clientCert.Thumbprint!,
                NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                HasClientAuthEku = true, Certificate = clientCert
            }
        }
    };

    private static (int Code, string Out, string Err) Run(CliServices services, params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(args, so, se, new MemoryStream(), services);
        return (code, so.ToString(), se.ToString());
    }

    /// <summary>Rewrite a captured baseline archive through the real HAR reader/writer, so a test
    /// that wants "the same capture but with one thing different" gets exactly that.</summary>
    private static void EditHar(string path, Action<HarEntry> edit)
    {
        var har = HarReader.Parse(File.ReadAllText(path));
        foreach (var entry in har.Log.Entries) edit(entry);
        File.WriteAllText(path, HarWriter.Write(har.Log.Entries, "test"));
    }

    // ---- baseline resolution -------------------------------------------------------------

    [Fact]
    public void Diff_against_a_missing_baseline_file_is_a_data_error()
    {
        var se = new StringWriter();
        int code = CliApp.Run(
            new[] { "send", "https://127.0.0.1:1/", "--diff", TempPath(".har") },
            new StringWriter(), se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });

        Assert.Equal(3, code);
        Assert.Contains("not found", se.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", se.ToString());
    }

    [Fact]
    public void Diff_against_a_malformed_har_baseline_is_a_data_error()
    {
        string har = TempPath(".har");
        File.WriteAllText(har, "{ not json");
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "send", "https://127.0.0.1:1/", "--diff", har },
                new StringWriter(), se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });

            Assert.Equal(3, code);
            Assert.DoesNotContain("   at ", se.ToString());
        }
        finally { File.Delete(har); }
    }

    [Fact]
    public void Diff_against_a_baseline_that_is_neither_har_nor_json_is_a_data_error()
    {
        string txt = TempPath(".txt");
        File.WriteAllText(txt, "200 OK");
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "send", "https://127.0.0.1:1/", "--diff", txt },
                new StringWriter(), se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });

            Assert.Equal(3, code);
            Assert.Contains(".har", se.ToString());
            Assert.Contains("known-good", se.ToString());
        }
        finally { File.Delete(txt); }
    }

    [Fact]
    public void Diff_known_good_with_no_matching_saved_request_is_a_data_error()
    {
        string ws = TempPath(".json");
        new AppState().SaveTo(ws);
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "send", "https://127.0.0.1:1/", "--workspace", ws, "--diff", "known-good" },
                new StringWriter(), se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });

            Assert.Equal(3, code);
            Assert.Contains("no saved request", se.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public void Diff_known_good_without_a_recorded_response_names_the_request()
    {
        string ws = TempPath(".json");
        var state = new AppState();
        var folder = new CollectionNode { Name = "suite", IsFolder = true };
        folder.Children.Add(new CollectionNode
        {
            Name = "health check",
            Request = new RequestModel { Method = "GET", Path = "https://127.0.0.1:1/" }
        });
        state.Collections.Add(folder);
        state.SaveTo(ws);
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "send", "https://127.0.0.1:1/", "--workspace", ws, "--diff", "known-good" },
                new StringWriter(), se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });

            Assert.Equal(3, code);
            Assert.Contains("health check", se.ToString());
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public void Diff_against_a_json_baseline_of_an_unrecognised_shape_is_a_data_error()
    {
        string baseline = TempPath(".json");
        File.WriteAllText(baseline, "{\"hello\":\"world\"}");
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "send", "https://127.0.0.1:1/", "--diff", baseline },
                new StringWriter(), se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });

            Assert.Equal(3, code);
            Assert.Contains("not a response this tool can read", se.ToString());
        }
        finally { File.Delete(baseline); }
    }

    [Theory]
    [InlineData("--diff-fail")]
    [InlineData("--diff-ignore", "data.timestamp")]
    [InlineData("--diff-ignore-header", "X-Trace")]
    public void A_diff_modifier_without_diff_is_a_usage_error(string flag, string? value = null)
    {
        var args = new List<string> { "send", "https://127.0.0.1:1/", flag };
        if (value is not null) args.Add(value);

        var se = new StringWriter();
        int code = CliApp.Run(args.ToArray(), new StringWriter(), se, new MemoryStream(),
                              new CliServices { LiveStatePath = TempState() });

        Assert.Equal(2, code);
        // Not merely "unknown option": the flag exists, and the message has to say what it needs.
        Assert.Contains("needs --diff", se.ToString());
    }

    [Fact]
    public void Diff_combined_with_all_ips_is_a_usage_error()
    {
        string har = TempPath(".har");
        File.WriteAllText(har, HarWriter.Write(Array.Empty<HarEntry>(), "test"));
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "send", "https://127.0.0.1:1/", "--all-ips", "--diff", har },
                new StringWriter(), se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });

            Assert.Equal(2, code);
            Assert.Contains("--all-ips", se.ToString());
            Assert.Contains("--diff", se.ToString());
        }
        finally { File.Delete(har); }
    }

    // ---- diffing a live send -------------------------------------------------------------

    [Fact]
    public async Task A_response_matching_its_captured_baseline_reports_no_differences_under_diff_fail()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("DiffClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        var services = ServicesFor(clientCert, TempState());
        string har = TempPath(".har");
        try
        {
            var capture = Run(services, "send", server.BaseUrl, "--cert", "DiffClient", "--insecure", "--har", har);
            Assert.Equal(0, capture.Code);

            var diff = Run(services, "send", server.BaseUrl, "--cert", "DiffClient", "--insecure",
                           "--diff", har, "--diff-fail");

            Assert.Equal(0, diff.Code);
            Assert.Contains("no differences", diff.Err);
        }
        finally { if (File.Exists(har)) File.Delete(har); }
    }

    [Fact]
    public async Task Diff_against_a_har_baseline_reports_the_changed_path_and_only_diff_fail_changes_the_exit_code()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("DiffClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(
            serverCert, clientCert.Thumbprint!, "{\"ok\":true,\"generatedAt\":\"now\"}");

        var services = ServicesFor(clientCert, TempState());
        string har = TempPath(".har");
        try
        {
            Assert.Equal(0, Run(services, "send", server.BaseUrl, "--cert", "DiffClient", "--insecure", "--har", har).Code);
            // The recorded body drifts; the recorded headers do not, so the body path is the only
            // difference the run can report.
            EditHar(har, e => e.Response.Content.Text = "{\"ok\":true,\"generatedAt\":\"then\"}");

            var reported = Run(services, "send", server.BaseUrl, "--cert", "DiffClient", "--insecure", "--diff", har);
            Assert.Equal(0, reported.Code);
            Assert.Contains("body generatedAt", reported.Err);

            var failed = Run(services, "send", server.BaseUrl, "--cert", "DiffClient", "--insecure",
                             "--diff", har, "--diff-fail");
            Assert.Equal(1, failed.Code);
            Assert.Contains("body generatedAt", failed.Err);
        }
        finally { if (File.Exists(har)) File.Delete(har); }
    }

    [Fact]
    public async Task Diff_ignore_suppresses_a_body_path_so_diff_fail_exits_zero()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("DiffClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(
            serverCert, clientCert.Thumbprint!, "{\"ok\":true,\"generatedAt\":\"now\"}");

        var services = ServicesFor(clientCert, TempState());
        string har = TempPath(".har");
        try
        {
            Assert.Equal(0, Run(services, "send", server.BaseUrl, "--cert", "DiffClient", "--insecure", "--har", har).Code);
            EditHar(har, e => e.Response.Content.Text = "{\"ok\":true,\"generatedAt\":\"then\"}");

            var ignored = Run(services, "send", server.BaseUrl, "--cert", "DiffClient", "--insecure",
                              "--diff", har, "--diff-fail", "--diff-ignore", "generatedAt");

            Assert.Equal(0, ignored.Code);
            Assert.Contains("no differences", ignored.Err);
        }
        finally { if (File.Exists(har)) File.Delete(har); }
    }

    [Fact]
    public async Task Diff_ignore_header_adds_to_the_volatile_defaults_rather_than_replacing_them()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("DiffClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartWithHeadersAsync(
            serverCert, clientCert.Thumbprint!,
            new[]
            {
                new KeyValuePair<string, string>("X-Trace", "trace-live"),
                new KeyValuePair<string, string>("X-Request-Id", "req-live")
            },
            "{\"ok\":true}");

        var services = ServicesFor(clientCert, TempState());
        string har = TempPath(".har");
        try
        {
            Assert.Equal(0, Run(services, "send", server.BaseUrl, "--cert", "DiffClient", "--insecure", "--har", har).Code);
            // Both recorded headers drift: one the user names, one only the defaults cover.
            EditHar(har, e => e.Response.Headers = e.Response.Headers
                .Select(h => h.Name.Equals("X-Trace", StringComparison.OrdinalIgnoreCase) ? h with { Value = "trace-recorded" }
                           : h.Name.Equals("X-Request-Id", StringComparison.OrdinalIgnoreCase) ? h with { Value = "req-recorded" }
                           : h)
                .ToList());

            // Without the flag the named header is a real difference — so the pass below is the
            // flag's doing, not an accident of the fixture.
            var reported = Run(services, "send", server.BaseUrl, "--cert", "DiffClient", "--insecure",
                               "--diff", har, "--diff-fail");
            Assert.Equal(1, reported.Code);
            Assert.Contains("header X-Trace", reported.Err);
            // X-Request-Id is volatile by default, so it never showed up even in the failing run.
            Assert.DoesNotContain("X-Request-Id", reported.Err);

            var ignored = Run(services, "send", server.BaseUrl, "--cert", "DiffClient", "--insecure",
                              "--diff", har, "--diff-fail", "--diff-ignore-header", "X-Trace");
            Assert.Equal(0, ignored.Code);
            Assert.Contains("no differences", ignored.Err);
        }
        finally { if (File.Exists(har)) File.Delete(har); }
    }

    [Fact]
    public async Task Json_envelope_carries_the_diff_and_round_trips_as_a_baseline()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("DiffClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(
            serverCert, clientCert.Thumbprint!, "{\"ok\":true,\"generatedAt\":\"now\"}");

        var services = ServicesFor(clientCert, TempState());
        string envelope = TempPath(".json");
        string har = TempPath(".har");
        try
        {
            // The tool's own --json output, written to a file and fed straight back in as a baseline:
            // the workflow the envelope shape exists to support.
            var captured = Run(services, "send", server.BaseUrl, "--cert", "DiffClient", "--insecure", "--json");
            Assert.Equal(0, captured.Code);
            File.WriteAllText(envelope, captured.Out);

            var same = Run(services, "send", server.BaseUrl, "--cert", "DiffClient", "--insecure",
                           "--diff", envelope, "--diff-fail");
            Assert.Equal(0, same.Code);
            Assert.Contains("no differences", same.Err);

            // And with a baseline that really differs, the envelope reports the diff structurally.
            Assert.Equal(0, Run(services, "send", server.BaseUrl, "--cert", "DiffClient", "--insecure", "--har", har).Code);
            EditHar(har, e => e.Response.Content.Text = "{\"ok\":true,\"generatedAt\":\"then\"}");

            var reported = Run(services, "send", server.BaseUrl, "--cert", "DiffClient", "--insecure",
                               "--diff", har, "--json");
            Assert.Equal(0, reported.Code);
            using var doc = JsonDocument.Parse(reported.Out);
            var diff = doc.RootElement.GetProperty("diff");
            Assert.False(diff.GetProperty("identical").GetBoolean());
            Assert.Equal("generatedAt", diff.GetProperty("body")[0].GetProperty("path").GetString());
            Assert.Equal("Changed", diff.GetProperty("body")[0].GetProperty("kind").GetString());
        }
        finally
        {
            if (File.Exists(envelope)) File.Delete(envelope);
            if (File.Exists(har)) File.Delete(har);
        }
    }

    [Fact]
    public async Task A_bare_serialized_snapshot_resolves_as_a_json_baseline()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("DiffClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        var services = ServicesFor(clientCert, TempState());
        string baseline = TempPath(".json");
        File.WriteAllText(baseline, JsonSerializer.Serialize(new ResponseSnapshot(
            200, Array.Empty<KeyValuePair<string, string>>(),
            Encoding.UTF8.GetBytes("{\"ok\":false}"), "application/json")));
        try
        {
            var reported = Run(services, "send", server.BaseUrl, "--cert", "DiffClient", "--insecure", "--diff", baseline);

            Assert.Equal(0, reported.Code);
            Assert.Contains("body ok changed: false -> true", reported.Err);
        }
        finally { File.Delete(baseline); }
    }

    [Fact]
    public async Task The_json_envelope_reports_attempts_only_when_a_retry_happened()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("DiffClient", ca, false, true);
        await using var flaky = await LoopbackMtlsServer.StartFlakyAsync(serverCert, clientCert.Thumbprint!, failures: 1);
        await using var steady = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        var services = ServicesFor(clientCert, TempState());

        var retried = Run(services, "send", flaky.BaseUrl, "--cert", "DiffClient", "--insecure",
                          "--retry", "2", "--retry-delay", "1", "--json");
        Assert.Equal(0, retried.Code);
        using var retriedDoc = JsonDocument.Parse(retried.Out);
        Assert.Equal(2, retriedDoc.RootElement.GetProperty("attempts").GetInt32());

        var first = Run(services, "send", steady.BaseUrl, "--cert", "DiffClient", "--insecure", "--json");
        Assert.Equal(0, first.Code);
        using var firstDoc = JsonDocument.Parse(first.Out);
        Assert.False(firstDoc.RootElement.TryGetProperty("attempts", out _));
    }

    // ---- run --diff-har ------------------------------------------------------------------

    /// <summary>A workspace whose only content is a pin for the loopback server's certificate — the
    /// HAR replay path has no --insecure, so a pin is how a test lets the replay connect at all.</summary>
    private static string PinnedWorkspace(X509Certificate2 serverCert)
    {
        string path = TempPath(".json");
        var state = new AppState();
        TrustService.Trust(state, "127.0.0.1", serverCert);
        state.SaveTo(path);
        return path;
    }

    [Fact]
    public async Task Run_diff_har_passes_every_entry_whose_response_still_matches_the_capture()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("DiffClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(
            serverCert, clientCert.Thumbprint!, "{\"ok\":true,\"generatedAt\":\"now\"}");

        var services = ServicesFor(clientCert, TempState());
        string ws = PinnedWorkspace(serverCert);
        string har = TempPath(".har");
        try
        {
            Assert.Equal(0, Run(services, "send", server.BaseUrl, "--cert", "DiffClient", "--insecure", "--har", har).Code);

            var replay = Run(services, "run", "--diff-har", har, "--workspace", ws, "--cert", "DiffClient");

            Assert.Equal(0, replay.Code);
            Assert.Contains("PASS", replay.Out);
            Assert.Contains("1 passed · 0 failed", replay.Out);
        }
        finally
        {
            File.Delete(ws);
            if (File.Exists(har)) File.Delete(har);
        }
    }

    [Fact]
    public async Task Run_diff_har_fails_the_entry_whose_recorded_body_no_longer_matches()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("DiffClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(
            serverCert, clientCert.Thumbprint!, "{\"ok\":true,\"generatedAt\":\"now\"}");

        var services = ServicesFor(clientCert, TempState());
        string ws = PinnedWorkspace(serverCert);
        string har = TempPath(".har");
        try
        {
            Assert.Equal(0, Run(services, "send", server.BaseUrl, "--cert", "DiffClient", "--insecure", "--har", har).Code);
            EditHar(har, e => e.Response.Content.Text = "{\"ok\":true,\"generatedAt\":\"then\"}");

            var replay = Run(services, "run", "--diff-har", har, "--workspace", ws, "--cert", "DiffClient");

            // A 200 that no longer says the same thing is a regression, not a pass.
            Assert.Equal(1, replay.Code);
            Assert.Contains("FAIL", replay.Out);
            Assert.Contains("0 passed · 1 failed", replay.Out);
            Assert.Contains("body generatedAt", replay.Err);

            // …and the same run is a pass once the drifting path is declared uninteresting.
            var ignored = Run(services, "run", "--diff-har", har, "--workspace", ws, "--cert", "DiffClient",
                              "--diff-ignore", "generatedAt");
            Assert.Equal(0, ignored.Code);
            Assert.Contains("PASS", ignored.Out);
        }
        finally
        {
            File.Delete(ws);
            if (File.Exists(har)) File.Delete(har);
        }
    }

    [Fact]
    public void Run_diff_har_cannot_be_combined_with_a_positional_or_all()
    {
        string har = TempPath(".har");
        File.WriteAllText(har, HarWriter.Write(Array.Empty<HarEntry>(), "test"));
        try
        {
            foreach (var extra in new[] { new[] { "suite" }, new[] { "--all" } })
            {
                var se = new StringWriter();
                int code = CliApp.Run(
                    new[] { "run", "--diff-har", har }.Concat(extra).ToArray(),
                    new StringWriter(), se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });

                Assert.Equal(2, code);
                Assert.Contains("--diff-har", se.ToString());
            }
        }
        finally { File.Delete(har); }
    }

    [Fact]
    public void Run_diff_ignore_without_diff_har_is_a_usage_error()
    {
        var se = new StringWriter();
        int code = CliApp.Run(
            new[] { "run", "--all", "--diff-ignore", "data.timestamp" },
            new StringWriter(), se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });

        Assert.Equal(2, code);
        Assert.Contains("needs --diff-har", se.ToString());
    }

    // ---- known-good recording ------------------------------------------------------------

    /// <summary>A live-state workspace holding one saved request pointed at the loopback server.</summary>
    private static string LiveStateWith(string url, X509Certificate2 clientCert)
    {
        string path = TempState();
        var state = new AppState();
        var folder = new CollectionNode { Name = "suite", IsFolder = true };
        folder.Children.Add(new CollectionNode
        {
            Name = "health check",
            Request = new RequestModel
            {
                Method = "GET", Path = url, IgnoreServerCert = true, CertThumbprint = clientCert.Thumbprint
            }
        });
        state.Collections.Add(folder);
        state.SaveTo(path);
        return path;
    }

    [Fact]
    public async Task Run_records_a_known_good_snapshot_on_a_successful_saved_request()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("DiffClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        string live = LiveStateWith(server.BaseUrl, clientCert);
        try
        {
            var run = Run(ServicesFor(clientCert, live), "run", "suite/health check");
            Assert.Equal(0, run.Code);

            var reloaded = AppState.LoadFrom(live);
            var node = reloaded.Collections[0].Children[0];
            Assert.NotNull(node.KnownGood);
            Assert.Equal(200, node.KnownGood!.StatusCode);
            Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(node.KnownGood.Body));
        }
        finally { File.Delete(live); }
    }

    [Fact]
    public async Task Run_records_no_known_good_snapshot_for_a_server_error()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("DiffClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: 5, failStatus: 500);

        string live = LiveStateWith(server.BaseUrl, clientCert);
        try
        {
            var run = Run(ServicesFor(clientCert, live), "run", "suite/health check");
            Assert.Equal(1, run.Code);

            var reloaded = AppState.LoadFrom(live);
            var node = reloaded.Collections[0].Children[0];
            Assert.Equal(500, node.LastStatusCode);
            Assert.Null(node.KnownGood);
        }
        finally { File.Delete(live); }
    }

    [Fact]
    public async Task A_send_diffs_against_the_known_good_a_previous_run_recorded()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("DiffClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        string live = LiveStateWith(server.BaseUrl, clientCert);
        var services = ServicesFor(clientCert, live);
        try
        {
            Assert.Equal(0, Run(services, "run", "suite/health check").Code);

            var diff = Run(services, "send", server.BaseUrl, "--cert", "DiffClient", "--insecure",
                           "--diff", "known-good", "--diff-fail");

            Assert.Equal(0, diff.Code);
            Assert.Contains("diff vs known-good:", diff.Err);
            Assert.Contains("no differences", diff.Err);
        }
        finally { File.Delete(live); }
    }
}
