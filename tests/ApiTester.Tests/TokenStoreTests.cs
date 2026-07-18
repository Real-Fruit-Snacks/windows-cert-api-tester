using System.IO;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

public class TokenStoreTests
{
    private static readonly List<KeyValuePair<string, string>> NoHeaders = new();

    private static SessionToken? Capture(AppState state, string url, string json) =>
        TokenService.Capture(state, url, Encoding.UTF8.GetBytes(json), "application/json", NoHeaders);

    [Fact]
    public void Capture_upserts_per_origin_newest_wins()
    {
        var state = new AppState();
        Capture(state, "https://a.example.com/login", "{\"access_token\":\"first\"}");
        Capture(state, "https://b.example.com/login", "{\"access_token\":\"other\"}");
        Capture(state, "https://a.example.com/login", "{\"access_token\":\"second\"}");

        Assert.Equal(2, state.SessionTokens.Count);
        Assert.Equal("second", TokenService.TokenFor(state, "https://a.example.com/users")!.Token);
        Assert.Equal("other", TokenService.TokenFor(state, "https://b.example.com/users")!.Token);
    }

    [Fact]
    public void TokenFor_is_origin_exact()
    {
        var state = new AppState();
        Capture(state, "https://api.example.com/login", "{\"access_token\":\"t\"}");
        Assert.NotNull(TokenService.TokenFor(state, "https://api.example.com/x"));
        Assert.Null(TokenService.TokenFor(state, "https://other.example.com/x"));      // other host
        Assert.Null(TokenService.TokenFor(state, "https://api.example.com:8443/x"));   // other port
        Assert.Null(TokenService.TokenFor(state, "http://api.example.com/x"));         // other scheme
    }

    [Fact]
    public void TokenFor_skips_expired_and_honors_the_global_toggle()
    {
        var state = new AppState();
        Capture(state, "https://api.example.com/login", "{\"access_token\":\"t\"}");
        state.SessionTokens[0].ExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
        Assert.Null(TokenService.TokenFor(state, "https://api.example.com/x"));
        Assert.NotNull(TokenService.ExpiredTokenFor(state, "https://api.example.com/x"));

        state.SessionTokens[0].ExpiresUtc = DateTime.UtcNow.AddMinutes(10);
        state.AutoTokens = false;
        Assert.Null(TokenService.TokenFor(state, "https://api.example.com/x"));
    }

    [Fact]
    public void AutoAttach_adds_the_header_but_never_overrides_explicit_auth()
    {
        var state = new AppState();
        Capture(state, "https://api.example.com/login", "{\"access_token\":\"tok\"}");

        var headers = new List<KeyValuePair<string, string>>();
        var used = TokenService.AutoAttach(state, "https://api.example.com/users", headers, out _);
        Assert.Equal("tok", used!.Token);
        Assert.Equal("Bearer tok", headers.Single(h => h.Key == "Authorization").Value);

        var explicitAuth = new List<KeyValuePair<string, string>> { new("authorization", "Bearer mine") };
        Assert.Null(TokenService.AutoAttach(state, "https://api.example.com/users", explicitAuth, out _));
        Assert.Single(explicitAuth);
    }

    [Fact]
    public void AutoAttach_surfaces_the_expired_token_for_messaging()
    {
        var state = new AppState();
        Capture(state, "https://api.example.com/login", "{\"access_token\":\"tok\"}");
        state.SessionTokens[0].ExpiresUtc = DateTime.UtcNow.AddMinutes(-1);

        var headers = new List<KeyValuePair<string, string>>();
        Assert.Null(TokenService.AutoAttach(state, "https://api.example.com/users", headers, out var expired));
        Assert.NotNull(expired);
        Assert.Empty(headers);
    }

    [Fact]
    public void Tokens_round_trip_through_the_state_file()
    {
        var state = new AppState();
        Capture(state, "https://api.example.com/login", "{\"access_token\":\"tok\",\"expires_in\":3600}");
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            state.SaveTo(path);
            var loaded = AppState.LoadFrom(path);
            var t = Assert.Single(loaded.SessionTokens);
            Assert.Equal("tok", t.Token);
            Assert.Equal("https://api.example.com:443", t.Origin);
            Assert.NotNull(t.ExpiresUtc);
            Assert.True(loaded.AutoTokens);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Legacy_none_auth_migrates_to_auto_once()
    {
        var state = new AppState();          // SchemaVersion 0, like a legacy file
        state.Tabs.Add(new RequestModel { AuthType = "None" });
        state.History.Add(new HistoryEntry { AuthType = "None" });
        var folder = new CollectionNode { IsFolder = true, Name = "api" };
        folder.Children.Add(new CollectionNode { Name = "r", Request = new RequestModel { AuthType = "None" } });
        state.Collections.Add(folder);

        state.Migrate();
        Assert.Equal("Auto", state.Tabs[0].AuthType);
        Assert.Equal("Auto", state.History[0].AuthType);
        Assert.Equal("Auto", folder.Children[0].Request!.AuthType);
        Assert.Equal(AppState.CurrentSchemaVersion, state.SchemaVersion);

        // A current-version state is left alone — explicit "None" now means "never send".
        state.Tabs[0].AuthType = "None";
        state.Migrate();
        Assert.Equal("None", state.Tabs[0].AuthType);
    }

    [Fact]
    public void Saved_files_are_stamped_current_and_loads_migrate()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var legacy = new AppState();
            legacy.Tabs.Add(new RequestModel { AuthType = "None" });
            // Simulate a legacy file: serialize by hand without SchemaVersion stamping.
            File.WriteAllText(path,
                System.Text.Json.JsonSerializer.Serialize(legacy).Replace($"\"SchemaVersion\":{AppState.CurrentSchemaVersion}", "\"SchemaVersion\":0"));
            var loaded = AppState.LoadFrom(path);
            Assert.Equal("Auto", loaded.Tabs[0].AuthType);

            // SaveTo stamps the version, so an explicit None survives the next load.
            loaded.Tabs[0].AuthType = "None";
            loaded.SaveTo(path);
            Assert.Equal("None", AppState.LoadFrom(path).Tabs[0].AuthType);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Fresh_request_models_default_to_auto() =>
        Assert.Equal("Auto", new RequestModel().AuthType);
}
