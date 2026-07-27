using System.IO;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>Proves the CLI host wiring for v1.62.0's per-user secret encryption: a headless load
/// decrypts a captured token transparently, a save through the CLI writes ciphertext rather than
/// plaintext, an undecryptable secret degrades to a stderr warning instead of a crash, and a
/// genuinely unreadable workspace still maps to the existing exit-3 data-error contract.</summary>
public class SecretsCliTests
{
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    /// <summary>Deletes the workspace file itself and any timestamped backup AppState.SaveTo left
    /// beside it (path + "." + timestamp + ".bak").</summary>
    private static void CleanUp(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        foreach (var bak in Directory.GetFiles(dir, name + ".*.bak"))
            File.Delete(bak);
    }

    [Fact]
    public async Task Send_reuses_an_encrypted_captured_token()
    {
        var ws = TempState();
        try
        {
            using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
            using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
            using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
            await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

            var seed = new AppState();
            seed.SessionTokens.Add(new SessionToken
            {
                Origin = TokenService.OriginOf(server.BaseUrl)!,
                Token = "tok-abc",
                Source = "seed",
                CapturedUtc = DateTime.UtcNow
            });
            seed.SaveTo(ws);
            // The whole point of this slice: the token never touches disk in the clear.
            Assert.DoesNotContain("tok-abc", File.ReadAllText(ws));

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
            var so = new StringWriter();
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "send", server.BaseUrl, "--cert", "CliClient", "--insecure", "--workspace", ws },
                so, se, new MemoryStream(), services);

            Assert.Equal(0, code);
            // LoopbackMtlsServer.BaseUrl is "https://127.0.0.1:<port>/", so that's the host
            // TokenService.HostOf reports back in the note (see AutoTokenCliTests).
            Assert.Contains("note: using captured token for 127.0.0.1", se.ToString());
        }
        finally { CleanUp(ws); }
    }

    [Fact]
    public async Task Token_save_writes_an_encrypted_token_that_a_later_load_can_read()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var srv = await LoopbackMtlsServer.StartOAuthTokenAsync(serverCert, clientCert.Thumbprint!, "app", "s3cret");

        var ws = TempState();   // does not exist yet — --save creates it, like send --capture
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
            var so = new StringWriter();
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "token", "--token-url", srv.BaseUrl, "--cert", "CliClient", "--insecure",
                        "--client-id", "app", "--client-secret", "s3cret",
                        "--save", "--for", "https://api.example.com", "--workspace", ws },
                new StringReader(""), so, se, new MemoryStream(), services);

            Assert.Equal(0, code);
            Assert.DoesNotContain("at-client_credentials", File.ReadAllText(ws));

            var token = Assert.Single(AppState.LoadFrom(ws).SessionTokens);
            Assert.Equal("at-client_credentials", token.Token);
        }
        finally { CleanUp(ws); }
    }

    [Fact]
    public async Task An_undecryptable_token_degrades_headlessly_instead_of_crashing()
    {
        var ws = TempState();
        try
        {
            using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
            using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
            using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
            await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

            // 64 deterministic noise bytes: decodes as valid Base64 of at least 32 bytes (so
            // SecretProtection.LooksProtected sees it as ciphertext), but is not a real DPAPI blob,
            // so unprotecting it on this machine genuinely fails.
            var noise = new byte[64];
            for (int i = 0; i < noise.Length; i++) noise[i] = (byte)(i * 7 + 11);

            var handWritten = new AppState { SchemaVersion = AppState.CurrentSchemaVersion };
            handWritten.SessionTokens.Add(new SessionToken
            {
                Origin = TokenService.OriginOf(server.BaseUrl)!,
                Token = "enc:v1:" + Convert.ToBase64String(noise),
                Source = "seed",
                CapturedUtc = DateTime.UtcNow
            });
            var folder = new CollectionNode { Name = "api", IsFolder = true };
            folder.Children.Add(new CollectionNode
            {
                Name = "get",
                Request = new RequestModel
                {
                    Method = "GET", Path = server.BaseUrl, AuthType = "None",
                    IgnoreServerCert = true, CertThumbprint = clientCert.Thumbprint
                }
            });
            handWritten.Collections.Add(folder);
            handWritten.History.Add(new HistoryEntry { Method = "GET", Url = server.BaseUrl });
            File.WriteAllText(ws, System.Text.Json.JsonSerializer.Serialize(handWritten));

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
            var so = new StringWriter();
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "send", server.BaseUrl, "--cert", "CliClient", "--insecure", "--workspace", ws },
                so, se, new MemoryStream(), services);

            Assert.Equal(0, code);
            Assert.Contains("warning: ", se.ToString());
            Assert.Contains("captured token", se.ToString());
            Assert.DoesNotContain("   at ", se.ToString());   // never a stack trace
            Assert.DoesNotContain("using captured token", se.ToString());   // the token was dropped

            // The good data alongside the undecryptable secret survives untouched.
            var reloaded = AppState.LoadFrom(ws);
            Assert.Single(reloaded.Collections);
            Assert.Single(reloaded.History);
        }
        finally { CleanUp(ws); }
    }

    [Fact]
    public async Task Run_works_from_an_encrypted_workspace()
    {
        var ws = TempState();
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
            state.SaveTo(ws);
            Assert.DoesNotContain("top-secret-value", File.ReadAllText(ws));

            var services = new CliServices
            {
                FindCertificate = _ => clientCert,
                IsGuiRunning = () => false,
                LiveStatePath = TempState()
            };
            var so = new StringWriter();
            var se = new StringWriter();
            int code = CliApp.Run(new[] { "run", "suite/ok", "--workspace", ws }, so, se, services: services);

            Assert.Equal(0, code);
        }
        finally { CleanUp(ws); }
    }

    [Fact]
    public async Task A_migrating_save_announces_the_backup_it_took()
    {
        var ws = TempState();
        try
        {
            using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
            using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
            using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
            await using var server = await LoopbackMtlsServer.StartAsync(
                serverCert, clientCert.Thumbprint!, "{\"access_token\":\"tok-new\"}");

            // A genuinely plaintext version-1 file: written with JsonSerializer directly, never
            // through SaveTo, so its secret is not yet in the encrypted "enc:v1:" form.
            var legacy = new AppState { SchemaVersion = 1 };
            legacy.SessionTokens.Add(new SessionToken
            {
                Origin = "https://old.example.com",
                Token = "old-plaintext-token",
                Source = "seed",
                CapturedUtc = DateTime.UtcNow
            });
            legacy.SavedBaseUrls.Add("https://old.example.com");
            File.WriteAllText(ws, System.Text.Json.JsonSerializer.Serialize(legacy));
            var originalBytes = File.ReadAllBytes(ws);

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
            var so = new StringWriter();
            var se = new StringWriter();
            // The send captures a fresh token from the server, which dirties state and forces a
            // save — the save that must migrate this legacy file to the encrypted format.
            int code = CliApp.Run(
                new[] { "send", server.BaseUrl, "--cert", "CliClient", "--insecure", "--workspace", ws },
                so, se, new MemoryStream(), services);

            Assert.Equal(0, code);
            string stderr = se.ToString();
            Assert.Contains("note: ", stderr);
            Assert.Contains("now encrypted", stderr);

            var dir = Path.GetDirectoryName(ws)!;
            var name = Path.GetFileName(ws);
            var backups = Directory.GetFiles(dir, name + ".*.bak");
            var backup = Assert.Single(backups);
            Assert.Contains(Path.GetFileName(backup), stderr);
            Assert.Equal(originalBytes, File.ReadAllBytes(backup));

            var reloaded = AppState.LoadFrom(ws);
            Assert.Contains("https://old.example.com", reloaded.SavedBaseUrls);
            Assert.Contains(reloaded.SessionTokens, t => t.Token == "old-plaintext-token");
        }
        finally { CleanUp(ws); }
    }

    [Fact]
    public async Task A_save_that_migrates_nothing_says_nothing_about_a_backup()
    {
        var ws = TempState();
        try
        {
            using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
            using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
            using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
            await using var server = await LoopbackMtlsServer.StartAsync(
                serverCert, clientCert.Thumbprint!, "{\"access_token\":\"tok-new\"}");

            // Already at the current schema version, written through SaveTo — no migration is due.
            var seed = new AppState();
            seed.SavedBaseUrls.Add("https://already-current.example.com");
            seed.SaveTo(ws);

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
            var so = new StringWriter();
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "send", server.BaseUrl, "--cert", "CliClient", "--insecure", "--workspace", ws },
                so, se, new MemoryStream(), services);

            Assert.Equal(0, code);
            string stderr = se.ToString();
            Assert.DoesNotContain("now encrypted", stderr);
            Assert.DoesNotContain("was copied to", stderr);

            var dir = Path.GetDirectoryName(ws)!;
            var name = Path.GetFileName(ws);
            Assert.Empty(Directory.GetFiles(dir, name + ".*.bak"));
        }
        finally { CleanUp(ws); }
    }

    [Fact]
    public void An_unreadable_workspace_still_exits_3_with_no_stack_trace()
    {
        var ws = TempState();
        try
        {
            File.WriteAllText(ws, "{ not json");

            var so = new StringWriter();
            var se = new StringWriter();
            int code = CliApp.Run(new[] { "run", "--all", "--workspace", ws }, so, se,
                services: new CliServices { LiveStatePath = TempState() });

            Assert.Equal(3, code);
            Assert.DoesNotContain("   at ", se.ToString());
            Assert.Single(se.ToString().TrimEnd('\r', '\n').Split('\n'));
        }
        finally { CleanUp(ws); }
    }
}
