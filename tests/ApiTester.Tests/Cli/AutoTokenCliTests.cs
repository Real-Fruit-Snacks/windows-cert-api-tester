using System.IO;
using System.Text;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class AutoTokenCliTests
{
    /// <summary>Run one CLI invocation against a loopback mTLS server, with the live state
    /// redirected to a temp file so token persistence is observable.</summary>
    private static async Task<(int Code, string Out, string Err)> RunAsync(
        string[] args, string statePath, string responseBody = "{\"ok\":true}", bool guiRunning = false)
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, responseBody);

        var services = new CliServices
        {
            LiveStatePath = statePath,
            IsGuiRunning = () => guiRunning,
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
        var full = args.Select(a => a.Replace("{URL}", server.BaseUrl)).ToArray();
        int code = CliApp.Run(full, so, se, new MemoryStream(), services);
        return (code, so.ToString(), se.ToString());
    }

    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    [Fact]
    public async Task Send_captures_a_token_and_a_follow_on_send_uses_it()
    {
        // TokenService.OriginOf scopes a captured token by scheme+host+PORT (see TokenServiceTests),
        // so this test needs the "login" and "next" sends to hit the exact same origin. RunAsync
        // starts a fresh LoopbackMtlsServer (a new ephemeral port) per call, which would give the
        // two sends different origins and make token reuse unobservable — so, unlike the other
        // cases here, this test drives one shared server across both sends instead of using RunAsync.
        var state = TempState();
        try
        {
            using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
            using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
            using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
            await using var server = await LoopbackMtlsServer.StartAsync(
                serverCert, clientCert.Thumbprint!, "{\"access_token\":\"tok-abc\",\"expires_in\":3600}");

            var services = new CliServices
            {
                LiveStatePath = state,
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

            var soLogin = new StringWriter();
            var seLogin = new StringWriter();
            int loginCode = CliApp.Run(new[] { "send", server.BaseUrl, "--cert", "CliClient", "--insecure" },
                soLogin, seLogin, new MemoryStream(), services);
            Assert.Equal(0, loginCode);
            // LoopbackMtlsServer.BaseUrl is "https://127.0.0.1:<port>/" (not "localhost"), so that's
            // the host TokenService.HostOf reports back in the note.
            Assert.Contains("note: captured bearer token for 127.0.0.1", seLogin.ToString());
            Assert.Contains("access_token", seLogin.ToString());

            var saved = AppState.LoadFrom(state);
            Assert.Equal("tok-abc", Assert.Single(saved.SessionTokens).Token);

            var soNext = new StringWriter();
            var seNext = new StringWriter();
            CliApp.Run(new[] { "send", server.BaseUrl, "--cert", "CliClient", "--insecure" },
                soNext, seNext, new MemoryStream(), services);
            Assert.Contains("note: using captured token for 127.0.0.1", seNext.ToString());
        }
        finally { if (File.Exists(state)) File.Delete(state); }
    }

    [Fact]
    public async Task Explicit_auth_and_no_auto_token_suppress_the_attach()
    {
        var state = TempState();
        try
        {
            await RunAsync(new[] { "send", "{URL}", "--cert", "CliClient", "--insecure" },
                state, responseBody: "{\"access_token\":\"tok\"}");

            var explicitAuth = await RunAsync(
                new[] { "send", "{URL}", "--cert", "CliClient", "--insecure", "--bearer", "mine" }, state);
            Assert.DoesNotContain("using captured token", explicitAuth.Err);

            var disabled = await RunAsync(
                new[] { "send", "{URL}", "--cert", "CliClient", "--insecure", "--no-auto-token" }, state);
            Assert.DoesNotContain("using captured token", disabled.Err);
        }
        finally { if (File.Exists(state)) File.Delete(state); }
    }

    [Fact]
    public async Task Gui_running_blocks_the_live_state_write_with_a_note()
    {
        var state = TempState();
        try
        {
            var r = await RunAsync(new[] { "send", "{URL}", "--cert", "CliClient", "--insecure" },
                state, responseBody: "{\"access_token\":\"tok\"}", guiRunning: true);
            Assert.Contains("the GUI is running", r.Err);
            Assert.False(File.Exists(state));
        }
        finally { if (File.Exists(state)) File.Delete(state); }
    }

    [Fact]
    public async Task Workspace_scoped_tokens_go_to_the_workspace_file()
    {
        var state = TempState();
        var ws = TempState();
        try
        {
            new AppState().SaveTo(ws);
            await RunAsync(new[] { "send", "{URL}", "--cert", "CliClient", "--insecure", "--workspace", ws },
                state, responseBody: "{\"access_token\":\"ws-tok\"}");
            Assert.Equal("ws-tok", Assert.Single(AppState.LoadFrom(ws).SessionTokens).Token);
            Assert.False(File.Exists(state));
        }
        finally { foreach (var f in new[] { state, ws }) if (File.Exists(f)) File.Delete(f); }
    }

    [Fact]
    public async Task Quiet_suppresses_token_notes()
    {
        var state = TempState();
        try
        {
            var r = await RunAsync(new[] { "send", "{URL}", "--cert", "CliClient", "--insecure", "-q" },
                state, responseBody: "{\"access_token\":\"tok\"}");
            Assert.DoesNotContain("captured bearer token", r.Err);
            Assert.True(File.Exists(state));   // still captured, just silently
        }
        finally { if (File.Exists(state)) File.Delete(state); }
    }

    [Fact]
    public async Task Debug_prints_masked_authorization()
    {
        var state = TempState();
        try
        {
            await RunAsync(new[] { "send", "{URL}", "--cert", "CliClient", "--insecure" },
                state, responseBody: "{\"access_token\":\"tok-1234567890abcdef\"}");
            var r = await RunAsync(new[] { "send", "{URL}", "--cert", "CliClient", "--insecure", "--debug" }, state);
            Assert.Contains("debug:", r.Err);
            Assert.DoesNotContain("tok-1234567890abcdef", r.Err);   // never the raw token
        }
        finally { if (File.Exists(state)) File.Delete(state); }
    }

    [Fact]
    public async Task Run_reuses_a_token_captured_earlier_in_the_suite()
    {
        var state = TempState();
        try
        {
            using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
            using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
            using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
            await using var server = await LoopbackMtlsServer.StartAsync(
                serverCert, clientCert.Thumbprint!, "{\"access_token\":\"suite-tok\"}");

            var ws = new AppState();
            var folder = new CollectionNode { Name = "api", IsFolder = true };
            folder.Children.Add(new CollectionNode
            {
                Name = "login", IsFolder = false,
                Request = new RequestModel { Method = "GET", Path = server.BaseUrl, AuthType = "Auto", IgnoreServerCert = true, CertThumbprint = clientCert.Thumbprint }
            });
            folder.Children.Add(new CollectionNode
            {
                Name = "list", IsFolder = false,
                Request = new RequestModel { Method = "GET", Path = server.BaseUrl, AuthType = "Auto", IgnoreServerCert = true, CertThumbprint = clientCert.Thumbprint }
            });
            ws.Collections.Add(folder);
            ws.SaveTo(state);

            var services = new CliServices
            {
                LiveStatePath = state,
                IsGuiRunning = () => false,
                FindCertificate = _ => clientCert
            };
            var so = new StringWriter();
            var se = new StringWriter();
            int code = CliApp.Run(new[] { "run", "api" }, so, se, new MemoryStream(), services);

            Assert.Equal(0, code);
            // LoopbackMtlsServer.BaseUrl is "https://127.0.0.1:<port>/" (not "localhost"), so that's
            // the host TokenService.HostOf reports back in the note (see Task 4's note on this).
            Assert.Contains("api/login: captured bearer token for 127.0.0.1", se.ToString());
            Assert.Contains("api/list: using captured token for 127.0.0.1", se.ToString());
            Assert.Single(AppState.LoadFrom(state).SessionTokens);   // persisted after the suite
        }
        finally { if (File.Exists(state)) File.Delete(state); }
    }

    [Fact]
    public async Task Run_respects_no_auto_token_and_explicit_none()
    {
        var state = TempState();
        try
        {
            using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
            using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
            using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
            await using var server = await LoopbackMtlsServer.StartAsync(
                serverCert, clientCert.Thumbprint!, "{\"access_token\":\"tok\"}");

            var ws = new AppState();
            ws.SessionTokens.Add(new SessionToken
            {
                Origin = TokenService.OriginOf(server.BaseUrl)!, Token = "tok", Source = "seed", CapturedUtc = DateTime.UtcNow
            });
            var folder = new CollectionNode { Name = "api", IsFolder = true };
            folder.Children.Add(new CollectionNode
            {
                Name = "anon", IsFolder = false,
                Request = new RequestModel { Method = "GET", Path = server.BaseUrl, AuthType = "None", IgnoreServerCert = true, CertThumbprint = clientCert.Thumbprint }
            });
            ws.Collections.Add(folder);
            ws.SaveTo(state);

            var services = new CliServices { LiveStatePath = state, IsGuiRunning = () => false, FindCertificate = _ => clientCert };
            var se = new StringWriter();
            CliApp.Run(new[] { "run", "api" }, new StringWriter(), se, new MemoryStream(), services);
            Assert.DoesNotContain("using captured token", se.ToString());   // explicit None never sends

            var se2 = new StringWriter();
            CliApp.Run(new[] { "run", "api", "--no-auto-token" }, new StringWriter(), se2, new MemoryStream(), services);
            Assert.DoesNotContain("using captured token", se2.ToString());
        }
        finally { if (File.Exists(state)) File.Delete(state); }
    }
}
