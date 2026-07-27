using System.IO;
using System.Text;
using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Proves the v1.62.0 encrypt-at-rest contract: <see cref="StateSecrets"/> and the
/// <see cref="AppState.SaveTo(string)"/>/<see cref="AppState.LoadFrom(string)"/> integration built on
/// it — round trip, migration from a legacy plaintext file, backup-before-first-rewrite, and
/// graceful degradation when a secret cannot be decrypted. "Never lose a user's data" is the whole
/// point of this release, so several of these tests are deliberately exhaustive rather than
/// representative.</summary>
public class SecretsAtRestTests
{
    private const string PlainToken = "session-token-plaintext-abc123";
    private const string PlainCookieValue = "cookie-plaintext-value-xyz789";
    private const string PlainTabSecret = "tab-auth-secret-plaintext";
    private const string PlainHistorySecret = "history-auth-secret-plaintext";
    private const string PlainRequestSecret = "collection-request-secret-plaintext";
    private const string PlainVariableValue = "secret-variable-plaintext-value";

    private static string TempPath(string tag) =>
        Path.Combine(Path.GetTempPath(), $"certapi-secrets-{tag}-{Guid.NewGuid():N}.json");

    // Deletes the workspace file itself, any ".tmp" the atomic write might have left behind on a
    // failure, and every "<path>.<timestamp>.bak" a migrating save produced.
    private static void DeleteWithBackups(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        foreach (var f in Directory.GetFiles(dir, name + ".*.bak")) File.Delete(f);
    }

    /// <summary>A state carrying all six secret-bearing kinds, each holding a distinct known
    /// plaintext value.</summary>
    private static AppState BuildStateWithAllSecretKinds()
    {
        var state = new AppState();
        state.SessionTokens.Add(new SessionToken
        {
            Origin = "https://api.example.com:443", Token = PlainToken, Source = "oauth",
            CapturedUtc = DateTime.UtcNow
        });
        state.SessionCookies.Add(new SessionCookie
        {
            Origin = "https://intranet.corp:443", Name = "SESSIONID", Value = PlainCookieValue,
            Path = "/", Domain = "intranet.corp"
        });
        state.Tabs.Add(new RequestModel
        {
            Method = "POST", BaseUrl = "https://api.example.com", Path = "/users",
            AuthType = "Bearer", AuthSecret = PlainTabSecret, AuthUser = "tab-user"
        });
        state.History.Add(new HistoryEntry
        {
            Method = "GET", Url = "/status", AuthType = "Bearer", AuthSecret = PlainHistorySecret
        });
        var folder = new CollectionNode { IsFolder = true, Name = "api" };
        folder.Children.Add(new CollectionNode
        {
            Name = "Create user",
            Request = new RequestModel
            {
                Method = "POST", Path = "/users", AuthType = "Bearer", AuthSecret = PlainRequestSecret
            }
        });
        state.Collections.Add(folder);
        var env = new ApiEnvironment { Id = "e1", Name = "Dev" };
        env.Variables.Add(new Variable { Key = "access_token", Value = PlainVariableValue, Secret = true });
        env.Variables.Add(new Variable { Key = "host", Value = "dev.local", Secret = false });
        state.Environments.Add(env);
        return state;
    }

    /// <summary>A realistic v1.61.1 workspace: secrets stored in the clear (SchemaVersion 1) plus a
    /// broad spread of non-secret content, so the "never lose a user's data" tests are exhaustive.</summary>
    private static AppState BuildLegacyWorkspace()
    {
        var state = new AppState
        {
            SchemaVersion = 1,
            Theme = "Light",
            TimeoutSeconds = 45,
            AutoTokens = false,
            AutoCookies = false,
            ActiveEnvironmentId = "e1"
        };
        state.SavedBaseUrls.Add("https://api.example.com");

        state.SessionTokens.Add(new SessionToken
        {
            Origin = "https://api.example.com:443", Token = PlainToken, Source = "oauth",
            CapturedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        state.SessionCookies.Add(new SessionCookie
        {
            Origin = "https://intranet.corp:443", Name = "SESSIONID", Value = PlainCookieValue,
            Path = "/", Domain = "intranet.corp", Secure = true, HttpOnly = true
        });

        var tab = new RequestModel
        {
            Method = "POST", BaseUrl = "https://api.example.com", Path = "/users",
            AuthType = "Bearer", AuthSecret = PlainTabSecret, AuthUser = "tab-user",
            Headers = { new HeaderRow { Name = "X-T", Value = "1" } },
            QueryParams = { new ParamRow { Key = "q", Value = "2" } },
            Captures = { new CaptureRule { Enabled = true, Variable = "id", Source = CaptureSource.Body, Path = "data.id" } },
            Assertions = { new AssertionRule { Enabled = true, Target = AssertTarget.Status, Op = AssertOp.Equals, Value = "201" } }
        };
        state.Tabs.Add(tab);

        var folder = new CollectionNode { Id = "f1", IsFolder = true, Name = "api" };
        var leaf = new CollectionNode
        {
            Id = "n1", IsFolder = false, Name = "Create user",
            Request = new RequestModel
            {
                Method = "POST", Path = "/users", AuthType = "Bearer", AuthSecret = PlainRequestSecret
            }
        };
        leaf.RecordResult(201, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            new ResponseSnapshot(201, new List<KeyValuePair<string, string>>(),
                Encoding.UTF8.GetBytes("{\"ok\":true}"), "application/json"));
        folder.Children.Add(leaf);
        state.Collections.Add(folder);

        var env = new ApiEnvironment { Id = "e1", Name = "Dev" };
        env.Variables.Add(new Variable { Key = "access_token", Value = PlainVariableValue, Secret = true });
        env.Variables.Add(new Variable { Key = "host", Value = "dev.local", Secret = false });
        state.Environments.Add(env);

        state.History.Add(new HistoryEntry
        {
            Method = "GET", Url = "/status", StatusCode = 200,
            AuthType = "Bearer", AuthSecret = PlainHistorySecret
        });

        state.Chains.Add(new RequestChain
        {
            Id = "c1", Name = "login then browse", EnvironmentName = "Dev",
            Steps =
            {
                new ChainStep { RequestId = "n1" },
                new ChainStep { RequestId = "n2", StopOnFailure = false }
            }
        });

        return state;
    }

    private static byte[] DeterministicNoise(int seed)
    {
        var bytes = new byte[64];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void All_six_secret_kinds_round_trip_through_save_and_load()
    {
        var path = TempPath("roundtrip");
        try
        {
            BuildStateWithAllSecretKinds().SaveTo(path);

            var back = AppState.LoadFrom(path);

            Assert.Equal(PlainToken, Assert.Single(back.SessionTokens).Token);
            Assert.Equal(PlainCookieValue, Assert.Single(back.SessionCookies).Value);
            Assert.Equal(PlainTabSecret, Assert.Single(back.Tabs).AuthSecret);
            Assert.Equal(PlainHistorySecret, Assert.Single(back.History).AuthSecret);
            var leaf = Assert.Single(Assert.Single(back.Collections).Children);
            Assert.Equal(PlainRequestSecret, leaf.Request!.AuthSecret);
            var secretVar = Assert.Single(back.Environments).Variables.Single(v => v.Secret);
            Assert.Equal(PlainVariableValue, secretVar.Value);
            Assert.Empty(back.SecretWarnings);
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void Saved_file_never_contains_secret_plaintext_and_carries_the_encryption_marker()
    {
        var path = TempPath("encrypted");
        try
        {
            BuildStateWithAllSecretKinds().SaveTo(path);

            var raw = File.ReadAllText(path);
            Assert.DoesNotContain(PlainToken, raw);
            Assert.DoesNotContain(PlainCookieValue, raw);
            Assert.DoesNotContain(PlainTabSecret, raw);
            Assert.DoesNotContain(PlainHistorySecret, raw);
            Assert.DoesNotContain(PlainRequestSecret, raw);
            Assert.DoesNotContain(PlainVariableValue, raw);
            Assert.Contains(SecretProtection.Prefix, raw);
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void Non_secret_fields_stay_in_the_clear_alongside_the_encrypted_secrets()
    {
        var path = TempPath("plaintext-fields");
        try
        {
            BuildStateWithAllSecretKinds().SaveTo(path);

            var raw = File.ReadAllText(path);
            Assert.Contains("access_token", raw);              // the secret variable's Key
            Assert.Contains("host", raw);                      // the non-secret variable's Key
            Assert.Contains("dev.local", raw);                 // the non-secret variable's value
            Assert.Contains("tab-user", raw);                  // AuthUser
            Assert.Contains("https://api.example.com:443", raw); // the token's Origin
            Assert.Contains("SESSIONID", raw);                 // the cookie's Name
            Assert.Contains("/users", raw);                    // a request URL
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void A_legacy_plaintext_workspace_loads_with_every_field_intact()
    {
        var path = TempPath("legacy");
        try
        {
            File.WriteAllText(path,
                JsonSerializer.Serialize(BuildLegacyWorkspace(), new JsonSerializerOptions { WriteIndented = true }));

            var back = AppState.LoadFrom(path);

            Assert.Equal("Light", back.Theme);
            Assert.Equal(45, back.TimeoutSeconds);
            Assert.False(back.AutoTokens);
            Assert.False(back.AutoCookies);
            Assert.Equal("https://api.example.com", Assert.Single(back.SavedBaseUrls));
            Assert.Equal("e1", back.ActiveEnvironmentId);

            var token = Assert.Single(back.SessionTokens);
            Assert.Equal(PlainToken, token.Token);
            Assert.Equal("oauth", token.Source);
            Assert.Equal("https://api.example.com:443", token.Origin);

            var cookie = Assert.Single(back.SessionCookies);
            Assert.Equal(PlainCookieValue, cookie.Value);
            Assert.Equal("SESSIONID", cookie.Name);
            Assert.True(cookie.Secure);
            Assert.True(cookie.HttpOnly);

            var tab = Assert.Single(back.Tabs);
            Assert.Equal(PlainTabSecret, tab.AuthSecret);
            Assert.Equal("tab-user", tab.AuthUser);
            Assert.Equal("X-T", Assert.Single(tab.Headers).Name);
            Assert.Equal("q", Assert.Single(tab.QueryParams).Key);
            Assert.Equal("id", Assert.Single(tab.Captures).Variable);
            Assert.Equal("201", Assert.Single(tab.Assertions).Value);

            var backFolder = Assert.Single(back.Collections);
            Assert.Equal("api", backFolder.Name);
            var backLeaf = Assert.Single(backFolder.Children);
            Assert.Equal(PlainRequestSecret, backLeaf.Request!.AuthSecret);
            Assert.Equal(201, backLeaf.LastStatusCode);
            Assert.True(backLeaf.IsKnownGood);
            Assert.NotNull(backLeaf.KnownGood);

            var backEnv = Assert.Single(back.Environments);
            var secretVar = backEnv.Variables.Single(v => v.Key == "access_token");
            Assert.Equal(PlainVariableValue, secretVar.Value);
            Assert.True(secretVar.Secret);
            var plainVar = backEnv.Variables.Single(v => v.Key == "host");
            Assert.Equal("dev.local", plainVar.Value);
            Assert.False(plainVar.Secret);

            var h = Assert.Single(back.History);
            Assert.Equal(200, h.StatusCode);
            Assert.Equal(PlainHistorySecret, h.AuthSecret);

            var chain = Assert.Single(back.Chains);
            Assert.Equal("login then browse", chain.Name);
            Assert.Equal("Dev", chain.EnvironmentName);
            Assert.Equal(2, chain.Steps.Count);
            Assert.Equal("n1", chain.Steps[0].RequestId);
            Assert.True(chain.Steps[0].StopOnFailure);
            Assert.Equal("n2", chain.Steps[1].RequestId);
            Assert.False(chain.Steps[1].StopOnFailure);

            Assert.Empty(back.SecretWarnings);
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void SaveTo_backs_up_the_legacy_file_once_before_the_first_encrypted_rewrite()
    {
        var path = TempPath("backup");
        try
        {
            File.WriteAllText(path,
                JsonSerializer.Serialize(BuildLegacyWorkspace(), new JsonSerializerOptions { WriteIndented = true }));
            var originalBytes = File.ReadAllBytes(path);

            var loaded = AppState.LoadFrom(path);
            var result = loaded.SaveTo(path);

            Assert.NotNull(result.BackupPath);
            Assert.True(result.SecretsEncrypted);
            Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(result.BackupPath));
            Assert.EndsWith(".bak", result.BackupPath);
            Assert.Equal(originalBytes, File.ReadAllBytes(result.BackupPath!));

            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                Assert.Equal(2, doc.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Contains(SecretProtection.Prefix, File.ReadAllText(path));

            var reloaded = AppState.LoadFrom(path);
            Assert.Equal(PlainToken, Assert.Single(reloaded.SessionTokens).Token);
            Assert.Equal(PlainCookieValue, Assert.Single(reloaded.SessionCookies).Value);
            Assert.Equal(PlainTabSecret, Assert.Single(reloaded.Tabs).AuthSecret);
            Assert.Equal(PlainHistorySecret, Assert.Single(reloaded.History).AuthSecret);
            Assert.Equal(PlainRequestSecret,
                Assert.Single(Assert.Single(reloaded.Collections).Children).Request!.AuthSecret);
            Assert.Equal(PlainVariableValue,
                Assert.Single(reloaded.Environments).Variables.Single(v => v.Secret).Value);

            var second = reloaded.SaveTo(path);
            Assert.Null(second.BackupPath);
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void NeedsSecretUpgrade_reflects_schema_version_and_file_state()
    {
        var path = TempPath("needs-upgrade");
        try
        {
            File.WriteAllText(path,
                JsonSerializer.Serialize(BuildLegacyWorkspace(), new JsonSerializerOptions { WriteIndented = true }));
            Assert.True(AppState.NeedsSecretUpgrade(path));

            var loaded = AppState.LoadFrom(path);
            loaded.SaveTo(path);
            Assert.False(AppState.NeedsSecretUpgrade(path));

            File.WriteAllText(path, "{ not json");
            Assert.True(AppState.NeedsSecretUpgrade(path));

            Assert.False(AppState.NeedsSecretUpgrade(path + "-does-not-exist"));
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void Undecryptable_secrets_are_dropped_or_emptied_without_throwing_and_everything_else_survives()
    {
        var path = TempPath("undecryptable");
        try
        {
            var noise = SecretProtection.Prefix + Convert.ToBase64String(DeterministicNoise(20260727));

            var state = new AppState { SchemaVersion = 2 };
            state.SessionTokens.Add(new SessionToken { Origin = "https://api.example.com:443", Token = noise, Source = "oauth" });
            state.SessionCookies.Add(new SessionCookie { Origin = "https://intranet.corp:443", Name = "SESSIONID", Value = noise });

            state.Tabs.Add(new RequestModel { Method = "POST", Path = "/broken", AuthType = "Bearer", AuthSecret = noise });
            state.Tabs.Add(new RequestModel { Method = "GET", Path = "/fine", AuthType = "Bearer", AuthSecret = "still-plaintext-until-next-save" });

            var folder = new CollectionNode { IsFolder = true, Name = "api" };
            folder.Children.Add(new CollectionNode { Name = "Create user", Request = new RequestModel { Method = "POST", Path = "/users" } });
            state.Collections.Add(folder);

            state.History.Add(new HistoryEntry { Method = "GET", Url = "/ok", StatusCode = 200 });

            var env = new ApiEnvironment { Id = "e1", Name = "Dev" };
            env.Variables.Add(new Variable { Key = "broken_secret", Value = noise, Secret = true });
            env.Variables.Add(new Variable { Key = "host", Value = "dev.local", Secret = false });
            state.Environments.Add(env);

            File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

            var back = AppState.LoadFrom(path);

            Assert.Empty(back.SessionTokens);
            Assert.Empty(back.SessionCookies);
            Assert.Null(back.Tabs[0].AuthSecret);
            Assert.Equal("still-plaintext-until-next-save", back.Tabs[1].AuthSecret);

            var backEnv = Assert.Single(back.Environments);
            var brokenVar = backEnv.Variables.Single(v => v.Key == "broken_secret");
            Assert.Equal("", brokenVar.Value);
            Assert.True(brokenVar.Secret);
            var hostVar = backEnv.Variables.Single(v => v.Key == "host");
            Assert.Equal("dev.local", hostVar.Value);
            Assert.False(hostVar.Secret);

            Assert.Equal("api", Assert.Single(back.Collections).Name);
            Assert.Equal("Create user", Assert.Single(Assert.Single(back.Collections).Children).Name);
            Assert.Equal(200, Assert.Single(back.History).StatusCode);

            Assert.Equal(5, back.SecretWarnings.Count);
            Assert.Contains(back.SecretWarnings, w => w.Contains("captured token for https://api.example.com:443") && w.Contains("dropped"));
            Assert.Contains(back.SecretWarnings, w => w.Contains("captured cookie \"SESSIONID\"") && w.Contains("dropped"));
            Assert.Contains(back.SecretWarnings, w => w.Contains("open tab 1") && w.Contains("POST /broken"));
            Assert.Contains(back.SecretWarnings, w => w.Contains("environment variable \"broken_secret\"") && w.Contains("Dev"));
            Assert.Equal(
                "Secrets are encrypted for your Windows user — a workspace file copied from another user or machine cannot be read.",
                back.SecretWarnings[^1]);
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void When_the_protector_is_unavailable_secrets_are_saved_in_the_clear_without_data_loss()
    {
        var path = TempPath("unavailable");
        try
        {
            var protector = new UnavailableSecretProtector();

            var result = BuildStateWithAllSecretKinds().SaveTo(path, protector);

            Assert.False(result.SecretsEncrypted);
            var raw = File.ReadAllText(path);
            Assert.Contains(PlainToken, raw);
            Assert.Contains(PlainCookieValue, raw);
            Assert.Contains(PlainTabSecret, raw);
            Assert.Contains(PlainHistorySecret, raw);
            Assert.Contains(PlainRequestSecret, raw);
            Assert.Contains(PlainVariableValue, raw);

            var backSameProtector = AppState.LoadFrom(path, protector);
            Assert.Equal(PlainToken, Assert.Single(backSameProtector.SessionTokens).Token);
            Assert.Equal(PlainVariableValue, Assert.Single(backSameProtector.Environments).Variables.Single(v => v.Secret).Value);
            Assert.Empty(backSameProtector.SecretWarnings);

            var backDefaultProtector = AppState.LoadFrom(path);
            Assert.Equal(PlainToken, Assert.Single(backDefaultProtector.SessionTokens).Token);
            Assert.Equal(PlainRequestSecret,
                Assert.Single(Assert.Single(backDefaultProtector.Collections).Children).Request!.AuthSecret);
            Assert.Empty(backDefaultProtector.SecretWarnings);
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void Schema_version_one_does_not_rerun_the_auth_none_to_auto_migration()
    {
        var path = TempPath("guard-v1");
        try
        {
            var state = new AppState { SchemaVersion = 1 };
            state.Tabs.Add(new RequestModel { AuthType = "None" });
            File.WriteAllText(path, JsonSerializer.Serialize(state));

            var back = AppState.LoadFrom(path);
            Assert.Equal("None", back.Tabs[0].AuthType);
        }
        finally { DeleteWithBackups(path); }
    }

    [Fact]
    public void Schema_version_zero_still_migrates_none_auth_to_auto()
    {
        var path = TempPath("guard-v0");
        try
        {
            var state = new AppState { SchemaVersion = 0 };
            state.Tabs.Add(new RequestModel { AuthType = "None" });
            File.WriteAllText(path, JsonSerializer.Serialize(state));

            var back = AppState.LoadFrom(path);
            Assert.Equal("Auto", back.Tabs[0].AuthType);
        }
        finally { DeleteWithBackups(path); }
    }
}
