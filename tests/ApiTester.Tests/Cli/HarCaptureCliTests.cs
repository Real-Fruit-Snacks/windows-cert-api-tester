using System.IO;
using System.Text;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>e2e coverage for <c>--har</c> capture and per-site server-certificate trust consult on
/// <c>certapi send</c> and <c>certapi fuzz</c>. Uses a real <see cref="ApiClient"/> against a real
/// <see cref="LoopbackMtlsServer"/> — the genuine system boundary — never a mock of ApiClient,
/// HarWriter, or TrustService.</summary>
public class HarCaptureCliTests
{
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
    private static string TempHarPath() => Path.Combine(Path.GetTempPath(), $"certapi-har-{Guid.NewGuid():N}.har");

    [Fact]
    public async Task Send_har_writes_a_well_formed_archive_with_the_final_response_status()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        var services = new CliServices
        {
            LiveStatePath = TempState(),
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

        string harPath = TempHarPath();
        try
        {
            var so = new StringWriter();
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "send", server.BaseUrl, "--cert", "CliClient", "--insecure", "--har", harPath },
                so, se, new MemoryStream(), services);

            Assert.Equal(0, code);
            Assert.True(File.Exists(harPath));
            var har = HarReader.Parse(File.ReadAllText(harPath));
            Assert.True(har.Log.Entries.Count >= 1);
            Assert.Equal(200, har.Log.Entries[^1].Response.Status);
            Assert.Contains("wrote HAR to", se.ToString());
        }
        finally { if (File.Exists(harPath)) File.Delete(harPath); }
    }

    [Fact]
    public async Task Send_har_redacts_authorization_by_default_and_keeps_it_with_include_secrets()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        CliServices MakeServices() => new()
        {
            LiveStatePath = TempState(),
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

        string redactedPath = TempHarPath();
        string plainPath = TempHarPath();
        try
        {
            int redactedCode = CliApp.Run(
                new[]
                {
                    "send", server.BaseUrl, "--cert", "CliClient", "--insecure",
                    "-H", "Authorization: Bearer secret", "--har", redactedPath
                },
                new StringWriter(), new StringWriter(), new MemoryStream(), MakeServices());
            Assert.Equal(0, redactedCode);
            var redactedHar = HarReader.Parse(File.ReadAllText(redactedPath));
            var redactedAuth = redactedHar.Log.Entries[^1].Request.Headers
                .Single(h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("[redacted]", redactedAuth.Value);

            int plainCode = CliApp.Run(
                new[]
                {
                    "send", server.BaseUrl, "--cert", "CliClient", "--insecure",
                    "-H", "Authorization: Bearer secret", "--har", plainPath, "--har-include-secrets"
                },
                new StringWriter(), new StringWriter(), new MemoryStream(), MakeServices());
            Assert.Equal(0, plainCode);
            var plainHar = HarReader.Parse(File.ReadAllText(plainPath));
            var plainAuth = plainHar.Log.Entries[^1].Request.Headers
                .Single(h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("Bearer secret", plainAuth.Value);
        }
        finally
        {
            if (File.Exists(redactedPath)) File.Delete(redactedPath);
            if (File.Exists(plainPath)) File.Delete(plainPath);
        }
    }

    [Fact]
    public void Send_har_with_a_nonexistent_directory_is_a_usage_error_and_writes_nothing()
    {
        string harPath = Path.Combine(Path.GetTempPath(), $"certapi-har-missing-dir-{Guid.NewGuid():N}", "session.har");
        var se = new StringWriter();

        int code = CliApp.Run(
            new[] { "send", "https://127.0.0.1:1/", "--har", harPath },
            new StringWriter(), se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });

        Assert.Equal(2, code);
        Assert.Contains("--har", se.ToString());
        Assert.False(File.Exists(harPath));
    }

    [Fact]
    public async Task Fuzz_har_writes_one_entry_per_probe()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        var wlPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        File.WriteAllText(wlPath, "/\n/health");
        string harPath = TempHarPath();
        try
        {
            var services = new CliServices
            {
                LiveStatePath = TempState(),
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

            int code = CliApp.Run(
                new[] { "fuzz", server.BaseUrl, "-w", wlPath, "--cert", "CliClient", "--insecure", "--all", "--har", harPath },
                new StringReader(""), new StringWriter(), new StringWriter(), new MemoryStream(), services);

            Assert.Equal(0, code);
            Assert.True(File.Exists(harPath));
            var har = HarReader.Parse(File.ReadAllText(harPath));
            Assert.Equal(2, har.Log.Entries.Count);
            Assert.All(har.Log.Entries, e => Assert.Equal(200, e.Response.Status));
        }
        finally
        {
            File.Delete(wlPath);
            if (File.Exists(harPath)) File.Delete(harPath);
        }
    }

    [Fact]
    public void Fuzz_har_with_a_nonexistent_directory_is_a_usage_error()
    {
        var wlPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        File.WriteAllText(wlPath, "/health");
        string harPath = Path.Combine(Path.GetTempPath(), $"certapi-har-missing-dir-{Guid.NewGuid():N}", "probes.har");
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "fuzz", "https://x.example", "-w", wlPath, "--har", harPath },
                new StringReader(""), new StringWriter(), se, new MemoryStream(),
                new CliServices { LiveStatePath = TempState() });

            Assert.Equal(2, code);
            Assert.Contains("--har", se.ToString());
            Assert.False(File.Exists(harPath));
        }
        finally { File.Delete(wlPath); }
    }

    [Fact]
    public async Task Send_trusts_a_pinned_server_certificate_without_insecure()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        string workspace = TempState();
        var state = new AppState();
        TrustService.Trust(state, "127.0.0.1", serverCert);
        state.SaveTo(workspace);

        var services = new CliServices
        {
            LiveStatePath = TempState(),
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

        try
        {
            var se = new StringWriter();
            // No --insecure: only the pinned thumbprint in the workspace lets this succeed.
            int code = CliApp.Run(
                new[] { "send", server.BaseUrl, "--cert", "CliClient", "--workspace", workspace },
                new StringWriter(), se, new MemoryStream(), services);

            Assert.Equal(0, code);
            Assert.Contains("trusting pinned certificate for 127.0.0.1", se.ToString());
        }
        finally { if (File.Exists(workspace)) File.Delete(workspace); }
    }

    [Fact]
    public async Task Send_rejects_an_unpinned_selfsigned_server_certificate_without_insecure()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        // An empty workspace: no pin recorded for this host at all.
        string workspace = TempState();
        new AppState().SaveTo(workspace);

        var services = new CliServices
        {
            LiveStatePath = TempState(),
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

        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "send", server.BaseUrl, "--cert", "CliClient", "--workspace", workspace },
                new StringWriter(), se, new MemoryStream(), services);

            Assert.Equal(1, code);
            Assert.DoesNotContain("trusting pinned certificate", se.ToString());
        }
        finally { if (File.Exists(workspace)) File.Delete(workspace); }
    }
}
