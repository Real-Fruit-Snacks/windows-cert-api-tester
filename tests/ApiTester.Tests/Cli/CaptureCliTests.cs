using System.IO;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class CaptureCliTests
{
    private static (X509Certificate2 ca, X509Certificate2 server, X509Certificate2 client) Certs()
    {
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        var server = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        var client = SelfSignedCertificateFactory.CreateSignedCertificate("CapClient", ca, false, true);
        return (ca, server, client);
    }

    private static CliServices Services(X509Certificate2 client, string livePath) => new()
    {
        ListCertificates = _ => new[]
        {
            new CertificateInfo { Subject = "CN=CapClient", Issuer = "CN=CA", Thumbprint = client.Thumbprint!,
                NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                HasClientAuthEku = true, Certificate = client }
        },
        IsGuiRunning = () => false,
        LiveStatePath = livePath
    };

    [Fact]
    public async Task Send_capture_writes_a_variable_into_the_workspace()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartAsync(server, client.Thumbprint!, "{\"access_token\":\"tok-123\"}");
            var ws = Path.Combine(Path.GetTempPath(), $"cap-{Guid.NewGuid():N}.json");
            try
            {
                int code = CliApp.Run(new[]
                {
                    "send", upstream.BaseUrl, "--cert", "CapClient", "--insecure",
                    "--capture", "token=access_token", "--workspace", ws
                }, new StringWriter(), new StringWriter(), new MemoryStream(), Services(client, "unused"));
                Assert.Equal(0, code);

                var state = AppState.LoadFrom(ws);
                var env = state.Environments.Single(e => e.Id == state.ActiveEnvironmentId);
                Assert.Equal("tok-123", env.Variables.Single(v => v.Key == "token").Value);
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public void Malformed_capture_is_a_usage_error()
    {
        int code = CliApp.Run(new[] { "send", "https://x", "--capture", "novalue" },
            new StringWriter(), new StringWriter(), new MemoryStream(), new CliServices());
        Assert.Equal(2, code);
    }

    [Fact]
    public void Send_with_a_missing_workspace_and_no_capture_is_a_data_error()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json");
        int code = CliApp.Run(new[] { "send", "https://example.com", "--workspace", missing },
            new StringWriter(), new StringWriter(), new MemoryStream(), new CliServices());
        Assert.Equal(3, code);
    }

    [Fact]
    public void Blank_capture_variable_is_a_usage_error()
    {
        // A non-leading '=' with a whitespace-only name must be rejected (hits the variable-name guard,
        // not the earlier no-'=' check).
        int code = CliApp.Run(new[] { "send", "https://x", "--capture", " =access_token" },
            new StringWriter(), new StringWriter(), new MemoryStream(), new CliServices());
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task Run_applies_a_saved_requests_capture_rules()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartAsync(server, client.Thumbprint!, "{\"access_token\":\"run-tok\"}");
            var ws = Path.Combine(Path.GetTempPath(), $"caprun-{Guid.NewGuid():N}.json");
            try
            {
                // Build a workspace with one saved request that captures the token.
                var state = new AppState();
                var req = new RequestModel { Method = "GET", Path = upstream.BaseUrl, IgnoreServerCert = true, CertThumbprint = client.Thumbprint };
                req.Captures.Add(new CaptureRule { Variable = "token", Source = CaptureSource.Body, Path = "access_token" });
                state.Collections.Add(new CollectionNode { Name = "login", IsFolder = false, Request = req });
                state.SaveTo(ws);

                var services = new CliServices
                {
                    FindCertificate = _ => client,
                    IsGuiRunning = () => false,
                    LiveStatePath = "unused"
                };
                int code = CliApp.Run(new[] { "run", "login", "--workspace", ws }, new StringWriter(), new StringWriter(), services: services);
                Assert.Equal(0, code);

                var back = AppState.LoadFrom(ws);
                var env = back.Environments.Single(e => e.Id == back.ActiveEnvironmentId);
                Assert.Equal("run-tok", env.Variables.Single(v => v.Key == "token").Value);
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task Run_captures_are_not_saved_to_live_state_while_the_gui_is_running()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartAsync(server, client.Thumbprint!, "{\"access_token\":\"live-tok\"}");
            var live = Path.Combine(Path.GetTempPath(), $"caplive-{Guid.NewGuid():N}.json");
            try
            {
                var state = new AppState();
                var req = new RequestModel { Method = "GET", Path = upstream.BaseUrl, IgnoreServerCert = true, CertThumbprint = client.Thumbprint };
                req.Captures.Add(new CaptureRule { Variable = "token", Source = CaptureSource.Body, Path = "access_token" });
                state.Collections.Add(new CollectionNode { Name = "login", IsFolder = false, Request = req });
                state.SaveTo(live);

                var services = new CliServices
                {
                    FindCertificate = _ => client,
                    IsGuiRunning = () => true,          // GUI is up → live-state writes are skipped
                    LiveStatePath = live
                };
                var stderr = new StringWriter();
                int code = CliApp.Run(new[] { "run", "login" }, new StringWriter(), stderr, services: services);
                Assert.Equal(0, code);
                Assert.Contains("GUI is running", stderr.ToString());

                // The captured value must NOT have been written to the live file (the GUI owns it).
                var back = AppState.LoadFrom(live);
                Assert.DoesNotContain(back.Environments.SelectMany(e => e.Variables), v => v.Key == "token");
            }
            finally { File.Delete(live); }
        }
    }
}
