using System.IO;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>Proves v1.62.0's `export workspace` secret stripping: by default every secret (captured
/// tokens/cookies, saved auth values, secret environment variables) is stripped before the file is
/// written — an exported workspace is a file people email to each other — while `--include-secrets`
/// keeps them, encrypted for the current Windows user, the same as any other saved workspace.</summary>
public class ExportSecretsCliTests
{
    private static (CliServices Services, string LivePath) FreshServices(bool guiRunning = false)
    {
        var live = Path.Combine(Path.GetTempPath(), $"certapi-exs-{Guid.NewGuid():N}.json");
        return (new CliServices { LiveStatePath = live, IsGuiRunning = () => guiRunning }, live);
    }

    /// <summary>Deletes the given path and any timestamped backup AppState.SaveTo left beside it
    /// (path + "." + timestamp + ".bak").</summary>
    private static void CleanUp(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        foreach (var bak in Directory.GetFiles(dir, name + ".*.bak"))
            File.Delete(bak);
    }

    /// <summary>Builds a workspace holding all six secret-bearing kinds plus non-secret structure,
    /// and saves it (encrypted, as any live save is) to <paramref name="path"/>.</summary>
    private static void SeedComprehensiveWorkspace(string path)
    {
        var state = new AppState();

        state.SessionTokens.Add(new SessionToken
        {
            Origin = "https://api.example.com",
            Token = "tok-abc-123",
            Source = "seed",
            CapturedUtc = DateTime.UtcNow
        });
        state.SessionCookies.Add(new SessionCookie
        {
            Origin = "https://api.example.com",
            Name = "session",
            Value = "cookie-secret-value",
            Path = "/",
            Domain = "api.example.com"
        });

        state.Tabs.Add(new RequestModel { Method = "GET", Path = "/one", AuthType = "Bearer", AuthSecret = "tab-secret-value" });
        state.Tabs.Add(new RequestModel { Method = "POST", Path = "/two" });   // second tab, no secret

        state.History.Add(new HistoryEntry { Method = "GET", Url = "/hist", AuthType = "Bearer", AuthSecret = "history-secret-value" });

        var folder = new CollectionNode { Name = "api", IsFolder = true };
        folder.Children.Add(new CollectionNode
        {
            Name = "call",
            Request = new RequestModel
            {
                Method = "GET", Path = "/nested", BaseUrl = "https://api.example.com",
                AuthType = "Bearer", AuthSecret = "collection-secret-value"
            }
        });
        state.Collections.Add(folder);

        var env = new ApiEnvironment { Name = "env1" };
        env.Variables.Add(new Variable { Key = "plainVar", Value = "plain-value", Secret = false });
        env.Variables.Add(new Variable { Key = "secretVar", Value = "secret-var-value", Secret = true });
        state.Environments.Add(env);

        state.SaveTo(path);
    }

    [Fact]
    public void Default_export_strips_every_secret_and_keeps_everything_else()
    {
        var (services, live) = FreshServices();
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-exs-out-{Guid.NewGuid():N}.json");
        try
        {
            SeedComprehensiveWorkspace(live);

            int code = CliApp.Run(new[] { "export", "workspace", "-o", outFile }, new StringWriter(), new StringWriter(), services: services);
            Assert.Equal(0, code);

            var exported = AppState.LoadFrom(outFile);
            Assert.Empty(exported.SessionTokens);
            Assert.Empty(exported.SessionCookies);

            Assert.Equal(2, exported.Tabs.Count);
            Assert.All(exported.Tabs, t => Assert.Null(t.AuthSecret));
            Assert.Contains(exported.Tabs, t => t.Path == "/one");
            Assert.Contains(exported.Tabs, t => t.Path == "/two");

            var hist = Assert.Single(exported.History);
            Assert.Null(hist.AuthSecret);
            Assert.Equal("/hist", hist.Url);

            var nestedFolder = Assert.Single(exported.Collections);
            Assert.Equal("api", nestedFolder.Name);
            var nestedRequest = Assert.Single(nestedFolder.Children);
            Assert.Equal("call", nestedRequest.Name);
            Assert.Equal("GET", nestedRequest.Request!.Method);
            Assert.Equal("/nested", nestedRequest.Request!.Path);
            Assert.Equal("https://api.example.com", nestedRequest.Request!.BaseUrl);
            Assert.Null(nestedRequest.Request!.AuthSecret);

            var exportedEnv = Assert.Single(exported.Environments);
            Assert.Equal(2, exportedEnv.Variables.Count);
            var plain = Assert.Single(exportedEnv.Variables, v => v.Key == "plainVar");
            Assert.False(plain.Secret);
            Assert.Equal("plain-value", plain.Value);
            var secretVar = Assert.Single(exportedEnv.Variables, v => v.Key == "secretVar");
            Assert.True(secretVar.Secret);
            Assert.Equal("", secretVar.Value);

            var raw = File.ReadAllText(outFile);
            Assert.DoesNotContain("tok-abc-123", raw);
            Assert.DoesNotContain("cookie-secret-value", raw);
            Assert.DoesNotContain("tab-secret-value", raw);
            Assert.DoesNotContain("history-secret-value", raw);
            Assert.DoesNotContain("collection-secret-value", raw);
            Assert.DoesNotContain("secret-var-value", raw);
            Assert.DoesNotContain("enc:v1:", raw);
        }
        finally { CleanUp(live); CleanUp(outFile); }
    }

    [Fact]
    public async Task Exported_workspace_is_still_usable_after_stripping()
    {
        var live = Path.Combine(Path.GetTempPath(), $"certapi-exs-live-{Guid.NewGuid():N}.json");
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-exs-out2-{Guid.NewGuid():N}.json");
        try
        {
            using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
            using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
            using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("RunClient", ca, false, true);
            await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);

            var state = new AppState();
            var folder = new CollectionNode { Name = "suite", IsFolder = true };
            folder.Children.Add(new CollectionNode
            {
                Name = "ok",
                Request = new RequestModel
                {
                    Method = "GET", Path = server.BaseUrl, AuthType = "Bearer", AuthSecret = "top-secret-value",
                    IgnoreServerCert = true, CertThumbprint = clientCert.Thumbprint
                }
            });
            state.Collections.Add(folder);
            state.SaveTo(live);

            var (exportServices, exportUnusedLive) = FreshServices();
            int exportCode = CliApp.Run(
                new[] { "export", "workspace", "-o", outFile, "--workspace", live },
                new StringWriter(), new StringWriter(), services: exportServices);
            Assert.Equal(0, exportCode);

            var runServices = new CliServices { FindCertificate = _ => clientCert, IsGuiRunning = () => false, LiveStatePath = exportUnusedLive };
            int code = CliApp.Run(
                new[] { "run", "suite/ok", "--workspace", outFile },
                new StringWriter(), new StringWriter(), services: runServices);

            Assert.Equal(0, code);
        }
        finally { CleanUp(live); CleanUp(outFile); }
    }

    [Fact]
    public void Include_secrets_keeps_every_secret_encrypted()
    {
        var (services, live) = FreshServices();
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-exs-out3-{Guid.NewGuid():N}.json");
        try
        {
            SeedComprehensiveWorkspace(live);

            int code = CliApp.Run(new[] { "export", "workspace", "-o", outFile, "--include-secrets" }, new StringWriter(), new StringWriter(), services: services);
            Assert.Equal(0, code);

            var exported = AppState.LoadFrom(outFile);
            Assert.Equal("tok-abc-123", Assert.Single(exported.SessionTokens).Token);
            Assert.Equal("cookie-secret-value", Assert.Single(exported.SessionCookies).Value);

            var tabWithSecret = Assert.Single(exported.Tabs, t => t.Path == "/one");
            Assert.Equal("tab-secret-value", tabWithSecret.AuthSecret);

            var hist = Assert.Single(exported.History);
            Assert.Equal("history-secret-value", hist.AuthSecret);

            var nestedRequest = Assert.Single(Assert.Single(exported.Collections).Children);
            Assert.Equal("collection-secret-value", nestedRequest.Request!.AuthSecret);

            var secretVar = Assert.Single(Assert.Single(exported.Environments).Variables, v => v.Key == "secretVar");
            Assert.Equal("secret-var-value", secretVar.Value);

            var raw = File.ReadAllText(outFile);
            Assert.Contains("enc:v1:", raw);
            Assert.DoesNotContain("tok-abc-123", raw);
            Assert.DoesNotContain("cookie-secret-value", raw);
            Assert.DoesNotContain("tab-secret-value", raw);
            Assert.DoesNotContain("history-secret-value", raw);
            Assert.DoesNotContain("collection-secret-value", raw);
            Assert.DoesNotContain("secret-var-value", raw);
        }
        finally { CleanUp(live); CleanUp(outFile); }
    }

    [Fact]
    public void Stderr_names_what_the_default_export_stripped_and_mentions_include_secrets()
    {
        var (services, live) = FreshServices();
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-exs-out4-{Guid.NewGuid():N}.json");
        try
        {
            SeedComprehensiveWorkspace(live);
            var se = new StringWriter();

            int code = CliApp.Run(new[] { "export", "workspace", "-o", outFile }, new StringWriter(), se, services: services);
            Assert.Equal(0, code);

            var text = se.ToString();
            Assert.Contains("captured token", text);
            Assert.Contains("captured cookie", text);
            Assert.Contains("saved auth secret", text);
            Assert.Contains("secret variable", text);
            Assert.Contains("--include-secrets", text);
        }
        finally { CleanUp(live); CleanUp(outFile); }
    }

    [Fact]
    public void Stderr_says_secrets_are_encrypted_when_include_secrets_is_used()
    {
        var (services, live) = FreshServices();
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-exs-out5-{Guid.NewGuid():N}.json");
        try
        {
            SeedComprehensiveWorkspace(live);
            var se = new StringWriter();

            int code = CliApp.Run(new[] { "export", "workspace", "-o", outFile, "--include-secrets" }, new StringWriter(), se, services: services);
            Assert.Equal(0, code);

            var text = se.ToString();
            Assert.Contains("encrypted for your Windows user", text);
        }
        finally { CleanUp(live); CleanUp(outFile); }
    }

    [Fact]
    public void Workspace_with_no_secrets_exports_cleanly_with_nothing_to_strip_wording()
    {
        var (services, live) = FreshServices();
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-exs-out6-{Guid.NewGuid():N}.json");
        try
        {
            var state = new AppState();
            state.Collections.Add(new CollectionNode { Name = "keep", IsFolder = true });
            state.SaveTo(live);
            var se = new StringWriter();

            int code = CliApp.Run(new[] { "export", "workspace", "-o", outFile }, new StringWriter(), se, services: services);

            Assert.Equal(0, code);
            Assert.Contains("no secrets to strip", se.ToString());
            Assert.Equal("keep", Assert.Single(AppState.LoadFrom(outFile).Collections).Name);
        }
        finally { CleanUp(live); CleanUp(outFile); }
    }

    [Fact]
    public void Include_secrets_on_export_openapi_is_a_usage_error()
    {
        var (services, live) = FreshServices();
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-exs-out7-{Guid.NewGuid():N}.json");
        try
        {
            var state = new AppState();
            state.Collections.Add(new CollectionNode
            {
                Name = "req",
                Request = new RequestModel { Method = "GET", Path = "/health", BaseUrl = "https://api" }
            });
            state.SaveTo(live);
            var se = new StringWriter();

            int code = CliApp.Run(new[] { "export", "openapi", "-o", outFile, "--include-secrets" }, new StringWriter(), se, services: services);

            Assert.Equal(2, code);
            Assert.Contains("--include-secrets", se.ToString());
            Assert.DoesNotContain("   at ", se.ToString());
        }
        finally { CleanUp(live); CleanUp(outFile); }
    }
}
