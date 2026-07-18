# Auto Token Reuse, Collection Defaults, and CLI Polish — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bearer tokens returned by one request are automatically captured and reused (host-scoped) by follow-on requests in the GUI, CLI, and MCP server; collection endpoints inherit website/cert instead of forcing re-selection; every CLI help screen gets verbose examples; and `--debug` / `--log-file` diagnostics work on every command.

**Architecture:** A new `TokenService` in `ApiTester.Core` owns detect → store → apply; each surface (GUI send, `certapi send`/`run`, MCP tools) calls it explicitly so every auth decision is visible and can print a note. Tokens live in `AppState.SessionTokens` (persisted like everything else). A `SchemaVersion` migration re-labels legacy auth `"None"` as the new `"Auto"` mode. CLI diagnostics are a `CliLog` sink on `CliServices`, created centrally in `CliApp` from global `--debug`/`--log-file` flags; the log file also tees everything written to stderr.

**Tech Stack:** .NET 9, WPF (net9.0-windows), System.Text.Json, xunit. No new packages.

**Spec:** `docs/superpowers/specs/2026-07-17-auto-token-and-cli-polish-design.md`

## Global Constraints

- Windows-only solution; build with `dotnet build ApiTester.sln`, test with `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj`.
- Commit messages: single imperative sentence, sentence case, **no AI attribution / no Co-Authored-By trailers** (repo owner preference).
- Auth mode strings are exactly: `"Auto"`, `"None"`, `"Bearer"`, `"Basic"`.
- Token scope (origin) format is exactly `scheme://host:port`, host lowercase (e.g. `https://api.example.com:443`).
- Detection body fields, in precedence order: `access_token`, `id_token`, `token`, `accessToken`, `jwt` (case-insensitive property match); nested one level under `data` or `result`. Header names: `X-Auth-Token`, then `X-Access-Token`.
- Detection scan cap: 2 MB. Detection must never throw.
- Existing output contracts (stdout = body, stderr = metadata; exit codes 0/1/2/3) are unchanged.
- Existing stderr note strings must keep their exact wording (tests assert them), e.g. `note: the GUI is running — captured values were not saved (it would overwrite them on close).`
- New user-facing note strings (use them verbatim everywhere):
  - `note: captured bearer token for <host> (<source><expiry>)` — `<expiry>` is `, expires in N min` or empty
  - `note: using captured token for <host>`
  - `note: the captured token for <host> has expired — sending without it`

---

### Task 1: TokenService detection (Core)

**Files:**
- Create: `src/ApiTester.Core/TokenService.cs`
- Test: `tests/ApiTester.Tests/TokenServiceTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces (later tasks call these exact members):
  - `class SessionToken { string Origin; string Token; string Source; DateTime CapturedUtc; DateTime? ExpiresUtc; bool IsExpired /*JsonIgnore*/ }`
  - `static string? TokenService.OriginOf(string url)`
  - `static string TokenService.HostOf(string url)`
  - `static SessionToken? TokenService.Detect(string url, byte[] body, string? contentType, IReadOnlyList<KeyValuePair<string,string>> headers)`
  - `static string TokenService.Mask(string token)`
  - `static string TokenService.MaskAuthorization(string value)`

- [ ] **Step 1: Write the failing tests**

Create `tests/ApiTester.Tests/TokenServiceTests.cs`:

```csharp
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

public class TokenServiceTests
{
    private static readonly List<KeyValuePair<string, string>> NoHeaders = new();

    private static SessionToken? Detect(string json, string? contentType = "application/json",
        List<KeyValuePair<string, string>>? headers = null, string url = "https://api.example.com/login") =>
        TokenService.Detect(url, Encoding.UTF8.GetBytes(json), contentType, headers ?? NoHeaders);

    [Fact]
    public void Origin_is_scheme_host_port_lowercase()
    {
        Assert.Equal("https://api.example.com:443", TokenService.OriginOf("https://API.Example.com/login?x=1"));
        Assert.Equal("https://api.example.com:8443", TokenService.OriginOf("https://api.example.com:8443/"));
        Assert.Equal("http://localhost:5000", TokenService.OriginOf("http://localhost:5000/a"));
        Assert.Null(TokenService.OriginOf("not a url"));
        Assert.Null(TokenService.OriginOf("ftp://example.com/x"));
    }

    [Theory]
    [InlineData("{\"access_token\":\"tok-1\"}", "tok-1", "access_token field")]
    [InlineData("{\"id_token\":\"tok-2\"}", "tok-2", "id_token field")]
    [InlineData("{\"token\":\"tok-3\"}", "tok-3", "token field")]
    [InlineData("{\"accessToken\":\"tok-4\"}", "tok-4", "accessToken field")]
    [InlineData("{\"jwt\":\"tok-5\"}", "tok-5", "jwt field")]
    [InlineData("{\"Access_Token\":\"tok-6\"}", "tok-6", "access_token field")]   // case-insensitive
    public void Detects_each_body_field(string json, string token, string source)
    {
        var t = Detect(json);
        Assert.NotNull(t);
        Assert.Equal(token, t!.Token);
        Assert.Equal(source, t.Source);
        Assert.Equal("https://api.example.com:443", t.Origin);
    }

    [Fact]
    public void Access_token_wins_over_other_fields()
    {
        var t = Detect("{\"token\":\"b\",\"access_token\":\"a\"}");
        Assert.Equal("a", t!.Token);
    }

    [Theory]
    [InlineData("{\"data\":{\"access_token\":\"nested\"}}")]
    [InlineData("{\"result\":{\"token\":\"nested\"}}")]
    public void Detects_one_level_under_data_or_result(string json) =>
        Assert.Equal("nested", Detect(json)!.Token);

    [Fact]
    public void Expires_in_sets_expiry()
    {
        var t = Detect("{\"access_token\":\"a\",\"expires_in\":3600}");
        Assert.NotNull(t!.ExpiresUtc);
        Assert.InRange((t.ExpiresUtc!.Value - DateTime.UtcNow).TotalMinutes, 58, 61);
        Assert.False(t.IsExpired);

        var s = Detect("{\"access_token\":\"a\",\"expires_in\":\"1800\"}");   // numeric string
        Assert.InRange((s!.ExpiresUtc!.Value - DateTime.UtcNow).TotalMinutes, 28, 31);
    }

    [Fact]
    public void Non_bearer_token_type_disqualifies()
    {
        Assert.Null(Detect("{\"access_token\":\"a\",\"token_type\":\"mac\"}"));
        Assert.NotNull(Detect("{\"access_token\":\"a\",\"token_type\":\"Bearer\"}"));
        Assert.NotNull(Detect("{\"access_token\":\"a\",\"token_type\":\"bearer\"}"));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"access_token\":42}")]
    [InlineData("{\"access_token\":\"\"}")]
    [InlineData("{\"access_token\":\"has space\"}")]
    [InlineData("{\"access_token\":\"has\\nnewline\"}")]
    [InlineData("{}")]
    public void Rejects_malformed_or_unsafe_values(string json) => Assert.Null(Detect(json));

    [Fact]
    public void Skips_non_json_content_types_and_huge_bodies()
    {
        Assert.Null(Detect("{\"access_token\":\"a\"}", contentType: "text/html"));
        Assert.NotNull(Detect("{\"access_token\":\"a\"}", contentType: null));   // unknown type: try anyway
        var huge = new byte[2 * 1024 * 1024 + 1];
        Assert.Null(TokenService.Detect("https://api.example.com/x", huge, "application/json", NoHeaders));
    }

    [Fact]
    public void Detects_header_tokens_with_body_taking_precedence()
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("x-auth-token", "hdr-tok")
        };
        var t = Detect("{}", headers: headers);
        Assert.Equal("hdr-tok", t!.Token);
        Assert.Equal("X-Auth-Token header", t.Source);

        var both = Detect("{\"access_token\":\"body-tok\"}", headers: headers);
        Assert.Equal("body-tok", both!.Token);

        var second = Detect("{}", headers: new() { new("X-Access-Token", "acc") });
        Assert.Equal("X-Access-Token header", second!.Source);
    }

    [Fact]
    public void Unparseable_url_yields_nothing() =>
        Assert.Null(Detect("{\"access_token\":\"a\"}", url: "::bad::"));

    [Fact]
    public void Mask_hides_the_middle()
    {
        Assert.Equal("eyJh…f3Qk", TokenService.Mask("eyJhbbbbbbbbbbbbbbbbf3Qk"));
        Assert.Equal("••••••", TokenService.Mask("secret"));                  // short: fully hidden
        Assert.Equal("Bearer eyJh…f3Qk", TokenService.MaskAuthorization("Bearer eyJhbbbbbbbbbbbbbbbbf3Qk"));
        Assert.Equal("Basic ••••••", TokenService.MaskAuthorization("Basic secret"));
    }

    [Fact]
    public void HostOf_extracts_the_display_host()
    {
        Assert.Equal("api.example.com", TokenService.HostOf("https://api.example.com:8443/login"));
        Assert.Equal("::bad::", TokenService.HostOf("::bad::"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~TokenServiceTests"`
Expected: build error — `TokenService` / `SessionToken` do not exist.

- [ ] **Step 3: Implement `TokenService`**

Create `src/ApiTester.Core/TokenService.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiTester.Core;

/// <summary>A bearer token captured from a response, scoped to the origin it came from.</summary>
public sealed class SessionToken
{
    public string Origin { get; set; } = "";      // scheme://host:port, host lowercase
    public string Token { get; set; } = "";
    public string Source { get; set; } = "";      // e.g. "access_token field", "X-Auth-Token header"
    public DateTime CapturedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }

    [JsonIgnore] public bool IsExpired => ExpiresUtc is { } e && DateTime.UtcNow >= e;
}

/// <summary>Detects bearer tokens in responses and scopes them to the origin they came from.
/// Detection never throws — an undetectable response simply yields null.</summary>
public static class TokenService
{
    private const int MaxScanBytes = 2 * 1024 * 1024;
    private static readonly string[] BodyFields = { "access_token", "id_token", "token", "accessToken", "jwt" };
    private static readonly string[] HeaderNames = { "X-Auth-Token", "X-Access-Token" };

    /// <summary>The token scope for a URL: scheme://host:port, host lowercase. Null when the
    /// URL is not an absolute http(s) URL.</summary>
    public static string? OriginOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
            ? $"{u.Scheme}://{u.Host.ToLowerInvariant()}:{u.Port}"
            : null;

    /// <summary>The display host for user-facing notes ("api.example.com"); the raw input when
    /// it cannot be parsed.</summary>
    public static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    /// <summary>Scan a response for a bearer token: JSON body fields first (top level, then one
    /// level under data/result), then X-Auth-Token / X-Access-Token headers.</summary>
    public static SessionToken? Detect(string url, byte[] body, string? contentType,
        IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        if (OriginOf(url) is not { } origin) return null;

        var (token, source, expires) = DetectInBody(body, contentType);
        if (token is not null)
            return new SessionToken
            {
                Origin = origin, Token = token, Source = source!,
                CapturedUtc = DateTime.UtcNow, ExpiresUtc = expires
            };

        foreach (var name in HeaderNames)
            foreach (var h in headers)
                if (h.Key.Equals(name, StringComparison.OrdinalIgnoreCase) && IsTokenShaped(h.Value.Trim()))
                    return new SessionToken
                    {
                        Origin = origin, Token = h.Value.Trim(), Source = $"{name} header",
                        CapturedUtc = DateTime.UtcNow
                    };
        return null;
    }

    /// <summary>Mask a token for display: first and last 4 characters around an ellipsis;
    /// short tokens are fully hidden.</summary>
    public static string Mask(string token) =>
        token.Length <= 12 ? new string('•', token.Length) : $"{token[..4]}…{token[^4..]}";

    /// <summary>Render an Authorization header value safely for logs: "Bearer eyJh…f3Qk".</summary>
    public static string MaskAuthorization(string value)
    {
        int sp = value.IndexOf(' ');
        return sp > 0 ? value[..sp] + " " + Mask(value[(sp + 1)..].Trim()) : Mask(value);
    }

    private static (string? Token, string? Source, DateTime? ExpiresUtc) DetectInBody(byte[] body, string? contentType)
    {
        if (body.Length == 0 || body.Length > MaxScanBytes) return default;
        if (contentType is not null && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return default;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return default; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return default;
            if (ScanObject(doc.RootElement) is { } hit) return hit;
            foreach (var nested in new[] { "data", "result" })
                if (TryGetIgnoreCase(doc.RootElement, nested, out var el) && el.ValueKind == JsonValueKind.Object &&
                    ScanObject(el) is { } nestedHit)
                    return nestedHit;
            return default;
        }
    }

    private static (string, string, DateTime?)? ScanObject(JsonElement obj)
    {
        // A non-Bearer token_type (e.g. "mac") disqualifies this object's token.
        if (TryGetIgnoreCase(obj, "token_type", out var tt) && tt.ValueKind == JsonValueKind.String &&
            !tt.GetString()!.Equals("bearer", StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (var field in BodyFields)
        {
            if (!TryGetIgnoreCase(obj, field, out var el) || el.ValueKind != JsonValueKind.String) continue;
            var value = el.GetString()!.Trim();
            if (!IsTokenShaped(value)) continue;
            return (value, $"{field} field", ExpiryOf(obj));
        }
        return null;
    }

    private static DateTime? ExpiryOf(JsonElement obj)
    {
        if (!TryGetIgnoreCase(obj, "expires_in", out var el)) return null;
        double seconds = el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(el.GetString(), out var d) => d,
            _ => 0
        };
        return seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : null;
    }

    private static bool TryGetIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    /// <summary>Header-safe: non-empty, no whitespace or control characters.</summary>
    private static bool IsTokenShaped(string? value) =>
        !string.IsNullOrEmpty(value) && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~TokenServiceTests"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ApiTester.Core/TokenService.cs tests/ApiTester.Tests/TokenServiceTests.cs
git commit -m "Add bearer-token detection to the core library"
```

---

### Task 2: Session-token store, auth-mode migration, and apply (Core)

**Files:**
- Modify: `src/ApiTester.Core/AppState.cs` (AppState class, ~line 10-65)
- Modify: `src/ApiTester.Core/TokenService.cs` (add store/apply members)
- Modify: `src/ApiTester.Core/RequestModel.cs` (default auth + LoadFrom mapping, lines 17 and 118)
- Test: `tests/ApiTester.Tests/TokenStoreTests.cs`

**Interfaces:**
- Consumes: Task 1's `SessionToken`, `TokenService.Detect`, `TokenService.OriginOf`.
- Produces (later tasks call these exact members):
  - `AppState.SessionTokens : List<SessionToken>`; `AppState.AutoTokens : bool` (default true); `AppState.SchemaVersion : int`; `const int AppState.CurrentSchemaVersion = 1`; `void AppState.Migrate()`
  - `static SessionToken? TokenService.Capture(AppState state, string url, byte[] body, string? contentType, IReadOnlyList<KeyValuePair<string,string>> headers)`
  - `static SessionToken? TokenService.TokenFor(AppState state, string url)`
  - `static SessionToken? TokenService.ExpiredTokenFor(AppState state, string url)`
  - `static SessionToken? TokenService.AutoAttach(AppState state, string url, List<KeyValuePair<string,string>> headers, out SessionToken? expired)`
  - `RequestModel` default `AuthType == "Auto"`.

- [ ] **Step 1: Write the failing tests**

Create `tests/ApiTester.Tests/TokenStoreTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~TokenStoreTests"`
Expected: build error — `SessionTokens`, `Migrate`, `Capture` etc. missing.

- [ ] **Step 3: Extend `AppState`**

In `src/ApiTester.Core/AppState.cs`, add to the `AppState` class after `public string? ActiveEnvironmentId { get; set; }` (line 32):

```csharp
    public List<SessionToken> SessionTokens { get; set; } = new();

    /// <summary>Master switch for automatic token capture/attach. Detection results are still
    /// stored while off, so turning it back on works immediately.</summary>
    public bool AutoTokens { get; set; } = true;

    /// <summary>File-format version. 0 = files from before the Auto/None auth split.</summary>
    public int SchemaVersion { get; set; }

    public const int CurrentSchemaVersion = 1;
```

Change `LoadFrom` (line 47-48) to migrate:

```csharp
    /// <summary>Load from an explicit file. Throws on missing/corrupt files — callers decide.</summary>
    public static AppState LoadFrom(string path)
    {
        var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path)) ?? new AppState();
        state.Migrate();
        return state;
    }
```

Add `Migrate` after `LoadFrom`:

```csharp
    /// <summary>Upgrade older states in place. Version 0 → 1: auth "None" predates the
    /// Auto/None split and meant "nothing configured", so it becomes "Auto".</summary>
    public void Migrate()
    {
        if (SchemaVersion >= CurrentSchemaVersion) return;
        foreach (var t in Tabs) MigrateAuth(t);
        foreach (var h in History) if (h.AuthType == "None") h.AuthType = "Auto";
        foreach (var c in Collections) MigrateNode(c);
        SchemaVersion = CurrentSchemaVersion;

        static void MigrateAuth(RequestModel m) { if (m.AuthType == "None") m.AuthType = "Auto"; }
        static void MigrateNode(CollectionNode n)
        {
            if (n.Request is { } r) MigrateAuth(r);
            foreach (var child in n.Children) MigrateNode(child);
        }
    }
```

In `SaveTo` (line 57), stamp the version as the first statement:

```csharp
    public void SaveTo(string path)
    {
        SchemaVersion = CurrentSchemaVersion;   // a written file is by definition current
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
```

- [ ] **Step 4: Add store/apply members to `TokenService`**

Append inside the `TokenService` class in `src/ApiTester.Core/TokenService.cs` (after `MaskAuthorization`):

```csharp
    /// <summary>Detect a token in a response and upsert it into the state's per-origin store
    /// (newest wins). Returns the stored token, or null when none was found.</summary>
    public static SessionToken? Capture(AppState state, string url, byte[] body, string? contentType,
        IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        if (Detect(url, body, contentType, headers) is not { } found) return null;
        state.SessionTokens.RemoveAll(t => t.Origin == found.Origin);
        state.SessionTokens.Add(found);
        return found;
    }

    /// <summary>The live (unexpired) captured token for a URL's origin, honoring the global
    /// auto-token switch. Null when there is none.</summary>
    public static SessionToken? TokenFor(AppState state, string url)
    {
        if (!state.AutoTokens || OriginOf(url) is not { } origin) return null;
        var t = state.SessionTokens.FirstOrDefault(x => x.Origin == origin);
        return t is { IsExpired: false } ? t : null;
    }

    /// <summary>The expired token for a URL's origin, if any — lets callers explain why no
    /// auth went out.</summary>
    public static SessionToken? ExpiredTokenFor(AppState state, string url) =>
        OriginOf(url) is { } origin
            ? state.SessionTokens.FirstOrDefault(t => t.Origin == origin && t.IsExpired)
            : null;

    /// <summary>Attach "Authorization: Bearer …" for the URL's origin when no explicit
    /// Authorization header is present and a live token exists. Returns the token used
    /// (null otherwise), surfacing an expired token for messaging.</summary>
    public static SessionToken? AutoAttach(AppState state, string url,
        List<KeyValuePair<string, string>> headers, out SessionToken? expired)
    {
        expired = null;
        if (headers.Any(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))) return null;
        if (TokenFor(state, url) is { } live)
        {
            headers.Add(new("Authorization", "Bearer " + live.Token));
            return live;
        }
        if (state.AutoTokens) expired = ExpiredTokenFor(state, url);
        return null;
    }
```

- [ ] **Step 5: Update `RequestModel` defaults**

In `src/ApiTester.Core/RequestModel.cs`:

Line 17, change the default:

```csharp
    private string _authType = "Auto";
```

Line 118 (inside `LoadFrom`), change the mapping so explicit None survives while everything else defaults to Auto:

```csharp
        AuthType = e.AuthType switch { "Bearer" => "Bearer", "Basic" => "Basic", "None" => "None", _ => "Auto" };
```

- [ ] **Step 6: Run the new tests, then the full suite**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~TokenStoreTests"`
Expected: all PASS.

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj`
Expected: PASS. If `AppModelTests`, `DtoTests`, or `StoreRoundTripTests` assert the old fresh-model default `AuthType == "None"`, update those assertions to `"Auto"` — that default change is intentional.

- [ ] **Step 7: Commit**

```bash
git add src/ApiTester.Core/AppState.cs src/ApiTester.Core/TokenService.cs src/ApiTester.Core/RequestModel.cs tests/ApiTester.Tests/TokenStoreTests.cs
git commit -m "Store, migrate, and apply session tokens in app state"
```

---

### Task 3: `--debug` and `--log-file` global flags (CLI)

**Files:**
- Create: `src/ApiTester.Cli/CliLog.cs`
- Modify: `src/ApiTester.Cli/CliApp.cs` (both `Run` overloads and `CliServices`)
- Test: `tests/ApiTester.Tests/Cli/CliLogTests.cs`

**Interfaces:**
- Consumes: `CliUsageException`, `ExitCodes` (existing).
- Produces (later tasks call these exact members):
  - `class CliLog : IDisposable { static CliLog None; static CliLog Create(bool debug, string? logFilePath, TextWriter stderr); bool DebugEnabled; void Debug(string message); void Note(string line); string Describe(Exception ex); TextWriter WrapStderr(TextWriter stderr); }`
  - `static class GlobalOptions { static (string[] Rest, bool Debug, string? LogFile) Extract(string[] args); }`
  - `CliServices.Log : CliLog` (settable, defaults to `CliLog.None`).

- [ ] **Step 1: Write the failing tests**

Create `tests/ApiTester.Tests/Cli/CliLogTests.cs`:

```csharp
using System.IO;
using ApiTester.Cli;

namespace ApiTester.Tests.Cli;

public class CliLogTests
{
    [Fact]
    public void Extract_pulls_global_flags_from_anywhere()
    {
        var (rest, debug, logFile) = GlobalOptions.Extract(
            new[] { "send", "--debug", "https://x.example", "--log-file", "run.log", "--pretty" });
        Assert.True(debug);
        Assert.Equal("run.log", logFile);
        Assert.Equal(new[] { "send", "https://x.example", "--pretty" }, rest);

        var none = GlobalOptions.Extract(new[] { "certs" });
        Assert.False(none.Debug);
        Assert.Null(none.LogFile);
    }

    [Fact]
    public void Extract_rejects_a_dangling_log_file() =>
        Assert.Throws<CliUsageException>(() => GlobalOptions.Extract(new[] { "certs", "--log-file" }));

    [Fact]
    public void Debug_lines_reach_stderr_only_when_enabled()
    {
        var se = new StringWriter();
        using var quiet = CliLog.Create(debug: false, logFilePath: null, se);
        quiet.Debug("hidden");
        Assert.Equal("", se.ToString());

        using var loud = CliLog.Create(debug: true, logFilePath: null, se);
        loud.Debug("visible");
        Assert.Contains("debug: visible", se.ToString());
    }

    [Fact]
    public void Log_file_receives_debug_and_teed_stderr_lines()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".log");
        try
        {
            var se = new StringWriter();
            using (var log = CliLog.Create(debug: false, path, se))
            {
                log.Debug("a diagnostic");
                var tee = log.WrapStderr(se);
                tee.WriteLine("note: something happened");
            }
            var text = File.ReadAllText(path);
            Assert.Contains("[debug] a diagnostic", text);
            Assert.Contains("[stderr] note: something happened", text);
            Assert.Contains("something happened", se.ToString());   // still reaches real stderr

            using (var again = CliLog.Create(debug: false, path, new StringWriter()))
                again.Debug("appended");
            Assert.Contains("appended", File.ReadAllText(path));    // append, not truncate
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Unopenable_log_file_warns_but_does_not_fail()
    {
        var se = new StringWriter();
        var bad = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "nested", "x.log");
        // Make the *file path itself* invalid by pointing at an existing file as a directory.
        var blocker = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(blocker, "");
        try
        {
            using var log = CliLog.Create(debug: true, Path.Combine(blocker, "x.log"), se);
            log.Debug("still works");
            Assert.Contains("warning: could not open log file", se.ToString());
            Assert.Contains("debug: still works", se.ToString());
        }
        finally { File.Delete(blocker); }
    }

    [Fact]
    public void Describe_shows_the_stack_only_under_debug()
    {
        Exception ex;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception caught) { ex = caught; }

        using var quiet = CliLog.Create(false, null, TextWriter.Null);
        Assert.Equal("boom", quiet.Describe(ex));

        using var loud = CliLog.Create(true, null, TextWriter.Null);
        Assert.Contains("InvalidOperationException", loud.Describe(ex));
        Assert.Contains("CliLogTests", loud.Describe(ex));   // stack frame present
    }

    [Fact]
    public void CliApp_understands_the_global_flags_end_to_end()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".log");
        try
        {
            var so = new StringWriter();
            var se = new StringWriter();
            int code = CliApp.Run(new[] { "--debug", "--log-file", path, "help" }, so, se);
            Assert.Equal(0, code);
            Assert.Contains("Usage: certapi", so.ToString());
            Assert.True(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~CliLogTests"`
Expected: build error — `CliLog` / `GlobalOptions` do not exist.

- [ ] **Step 3: Implement `CliLog` and `GlobalOptions`**

Create `src/ApiTester.Cli/CliLog.cs`:

```csharp
namespace ApiTester.Cli;

/// <summary>Diagnostic sink for the global --debug / --log-file options. Debug lines go to
/// stderr when debug is on and to the log file always; the log file also receives every line
/// the command writes to stderr (via <see cref="WrapStderr"/>). Logging never throws — a
/// broken log file must not break the command.</summary>
public sealed class CliLog : IDisposable
{
    public static CliLog None { get; } = new(debug: false, file: null, stderr: TextWriter.Null);

    private readonly TextWriter _stderr;
    private readonly StreamWriter? _file;
    private readonly object _lock = new();

    public bool DebugEnabled { get; }

    private CliLog(bool debug, StreamWriter? file, TextWriter stderr)
    {
        DebugEnabled = debug;
        _file = file;
        _stderr = stderr;
    }

    /// <summary>Open the sink. A log file that cannot be opened is a one-line warning, not an error.</summary>
    public static CliLog Create(bool debug, string? logFilePath, TextWriter stderr)
    {
        StreamWriter? file = null;
        if (logFilePath is not null)
        {
            try
            {
                if (Path.GetDirectoryName(logFilePath) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
                file = new StreamWriter(logFilePath, append: true) { AutoFlush = true };
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"warning: could not open log file '{logFilePath}': {ex.Message}");
            }
        }
        return new CliLog(debug, file, stderr);
    }

    /// <summary>A debug diagnostic: stderr under --debug, log file always.</summary>
    public void Debug(string message)
    {
        if (DebugEnabled) _stderr.WriteLine("debug: " + message);
        ToFile("debug", message);
    }

    /// <summary>Record a line the command wrote to stderr (notes, warnings, errors).</summary>
    public void Note(string line) => ToFile("stderr", line);

    /// <summary>The error text for an exception: full chain and stack under --debug.</summary>
    public string Describe(Exception ex) => DebugEnabled ? ex.ToString() : ex.Message;

    /// <summary>Wrap stderr so every completed line is also recorded in the log file.</summary>
    public TextWriter WrapStderr(TextWriter stderr) => _file is null ? stderr : new TeeWriter(stderr, this);

    private void ToFile(string level, string message)
    {
        if (_file is null) return;
        lock (_lock)
        {
            try { _file.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} [{level}] {message}"); }
            catch { /* never break the command over logging */ }
        }
    }

    public void Dispose() => _file?.Dispose();

    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly CliLog _log;
        private readonly System.Text.StringBuilder _line = new();

        public TeeWriter(TextWriter inner, CliLog log) { _inner = inner; _log = log; }

        public override System.Text.Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            _inner.Write(value);
            if (value == '\n')
            {
                _log.Note(_line.ToString().TrimEnd('\r'));
                _line.Clear();
            }
            else _line.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var c in value) Write(c);
        }

        public override void Flush() => _inner.Flush();
    }
}

/// <summary>Extracts the global --debug / --log-file options, which are valid anywhere on any
/// command line, before command dispatch.</summary>
public static class GlobalOptions
{
    public static (string[] Rest, bool Debug, string? LogFile) Extract(string[] args)
    {
        var rest = new List<string>(args.Length);
        bool debug = false;
        string? logFile = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--debug", StringComparison.OrdinalIgnoreCase)) { debug = true; continue; }
            if (args[i].Equals("--log-file", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) throw new CliUsageException("Option --log-file needs a value.");
                logFile = args[++i];
                continue;
            }
            rest.Add(args[i]);
        }
        return (rest.ToArray(), debug, logFile);
    }
}
```

- [ ] **Step 4: Wire the flags through `CliApp` and `CliServices`**

In `src/ApiTester.Cli/CliApp.cs`, add to `CliServices` (after the `GatewayFactory` property, line 26):

```csharp
    /// <summary>Diagnostic sink for --debug / --log-file; set per invocation by CliApp.</summary>
    public CliLog Log { get; set; } = CliLog.None;
```

Replace the **outer** `Run` overload (lines 48-60) with:

```csharp
    public static int Run(string[] args, TextReader input, TextWriter stdout, TextWriter stderr,
                          Stream? bodyOut = null, CliServices? services = null)
    {
        services ??= new CliServices();
        if (args.Length > 0 && args[0].Equals("mcp", StringComparison.OrdinalIgnoreCase))
        {
            (string[] Rest, bool Debug, string? LogFile) g;
            try { g = GlobalOptions.Extract(args.Skip(1).ToArray()); }
            catch (CliUsageException ex) { stderr.WriteLine(ex.Message); return ExitCodes.Usage; }

            using var log = CliLog.Create(g.Debug, g.LogFile, stderr);
            services.Log = log;
            var err = log.WrapStderr(stderr);
            try { return Commands.McpCommand.Run(new Args(g.Rest), input, stdout, err, services); }
            catch (CliUsageException ex) { err.WriteLine(ex.Message); return ExitCodes.Usage; }
            catch (CliDataException ex) { err.WriteLine(ex.Message); return ExitCodes.Data; }
            catch (Exception ex) { err.WriteLine("error: " + log.Describe(ex)); return ExitCodes.Failure; }
        }
        return Run(args, stdout, stderr, bodyOut, services);
    }
```

Replace the **inner** `Run` overload (lines 62-89) with:

```csharp
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr,
                          Stream? bodyOut = null, CliServices? services = null)
    {
        services ??= new CliServices();
        if (args.Length == 0) { stderr.WriteLine(Usage); return ExitCodes.Usage; }

        (string[] Rest, bool Debug, string? LogFile) g;
        try { g = GlobalOptions.Extract(args); }
        catch (CliUsageException ex) { stderr.WriteLine(ex.Message); return ExitCodes.Usage; }

        using var log = CliLog.Create(g.Debug, g.LogFile, stderr);
        services.Log = log;
        var err = log.WrapStderr(stderr);
        try
        {
            if (g.Rest.Length == 0) { err.WriteLine(Usage); return ExitCodes.Usage; }
            string command = g.Rest[0].ToLowerInvariant();
            var rest = g.Rest.Skip(1).ToArray();
            return command switch
            {
                "--version" or "-v" => Version(stdout),
                "help" or "--help" or "-h" => Help(rest, stdout),
                "certs" => Commands.CertsCommand.Run(new Args(rest), stdout, err, services),
                "send" => Commands.SendCommand.Run(new Args(rest), stdout, err, bodyOut ?? new MemoryStream(), services),
                "run" => Commands.RunCommand.Run(new Args(rest), stdout, err, services),
                "selftest" => Commands.SelfTestCommand.Run(new Args(rest), stdout, err),
                "import" => Commands.ImportCommand.Run(new Args(rest), stdout, err, services),
                "export" => Commands.ExportCommand.Run(new Args(rest), stdout, err, services),
                "serve" => Commands.ServeCommand.Run(new Args(rest), stdout, err, services),
                _ => throw new CliUsageException($"Unknown command '{g.Rest[0]}'.\n{Usage}")
            };
        }
        catch (CliUsageException ex) { err.WriteLine(ex.Message); return ExitCodes.Usage; }
        catch (CliDataException ex) { err.WriteLine(ex.Message); return ExitCodes.Data; }
        catch (Exception ex) { err.WriteLine("error: " + log.Describe(ex)); return ExitCodes.Failure; }
    }
```

- [ ] **Step 5: Run the new tests, then the full suite**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~CliLogTests"`
Expected: all PASS.

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj`
Expected: PASS (existing CLI tests exercise both Run overloads).

- [ ] **Step 6: Commit**

```bash
git add src/ApiTester.Cli/CliLog.cs src/ApiTester.Cli/CliApp.cs tests/ApiTester.Tests/Cli/CliLogTests.cs
git commit -m "Add --debug and --log-file diagnostics to the CLI"
```

---

### Task 4: Auto tokens in `certapi send`

**Files:**
- Modify: `src/ApiTester.Cli/Commands/SendCommand.cs`
- Test: `tests/ApiTester.Tests/Cli/AutoTokenCliTests.cs`

**Interfaces:**
- Consumes: `TokenService.AutoAttach/Capture/HostOf/Mask/MaskAuthorization`, `CliServices.Log`, `CliLog.Debug`.
- Produces: `send` behavior relied on by Task 9's help text: `--no-auto-token` flag; the three note strings from Global Constraints.

- [ ] **Step 1: Write the failing tests**

Create `tests/ApiTester.Tests/Cli/AutoTokenCliTests.cs` (same harness pattern as `SendCommandTests`):

```csharp
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
        var state = TempState();
        try
        {
            var login = await RunAsync(new[] { "send", "{URL}", "--cert", "CliClient", "--insecure" },
                state, responseBody: "{\"access_token\":\"tok-abc\",\"expires_in\":3600}");
            Assert.Equal(0, login.Code);
            Assert.Contains("note: captured bearer token for localhost", login.Err);
            Assert.Contains("access_token", login.Err);

            var saved = AppState.LoadFrom(state);
            Assert.Equal("tok-abc", Assert.Single(saved.SessionTokens).Token);

            var next = await RunAsync(new[] { "send", "{URL}", "--cert", "CliClient", "--insecure" }, state);
            Assert.Contains("note: using captured token for localhost", next.Err);
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
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~AutoTokenCliTests"`
Expected: FAIL — `--no-auto-token` unknown option, and no token notes are printed.

- [ ] **Step 3: Implement in `SendCommand.Run`**

All edits in `src/ApiTester.Cli/Commands/SendCommand.cs`.

3a. Bind the new flag with the others (after `var captureSpecs = args.Values("--capture");`, line 75):

```csharp
        bool noAutoToken = args.Flag("--no-auto-token");
```

3b. Always load the state so tokens are available (replace lines 110-116, the `// ---- variables ----` comment and `var state = ...` statement):

```csharp
        // ---- variables ----
        // The state is always loaded now: even without --env, the live state may hold a
        // captured session token for this URL's origin. A --workspace that doesn't exist yet
        // is fine when --capture is present — it is created fresh on save.
        var state = LoadWorkspaceOrEmpty(workspace, services);
```

3c. Auto-attach after the unresolved-variables block (after line 143, before `// ---- certificate ----`):

```csharp
        // ---- automatic session token ----
        if (!noAutoToken)
        {
            var used = TokenService.AutoAttach(state, url, headerPairs, out var expired);
            if (used is not null)
            {
                if (!quiet) stderr.WriteLine($"note: using captured token for {TokenService.HostOf(url)}");
                services.Log.Debug($"auto token attached for {used.Origin} ({used.Source})");
            }
            else if (expired is not null && !quiet)
            {
                stderr.WriteLine($"note: the captured token for {TokenService.HostOf(url)} has expired — sending without it");
            }
        }
```

3d. Debug diagnostics around the send (insert directly before `var response = services.Client.SendAsync(...)`, line 162):

```csharp
        services.Log.Debug($"{request.Method} {request.Url}");
        foreach (var h in headerPairs)
            services.Log.Debug("header: " + (h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                ? $"{h.Key}: {TokenService.MaskAuthorization(h.Value)}" : $"{h.Key}: {h.Value}"));
        services.Log.Debug(cert is null ? "certificate: none" : $"certificate: {cert.Subject} ({cert.Thumbprint})");
        services.Log.Debug($"timeout: {timeout} s · insecure: {insecure} · store: {store}");
```

And directly after the send returns (after line 163):

```csharp
        services.Log.Debug("result: " + (response.Error is null
            ? $"{response.StatusCode} {response.ReasonPhrase}".Trim()
            : $"[{response.Error.Kind}] {response.Error.Message}")
            + $" · {response.Elapsed.TotalMilliseconds:F0} ms · {response.Body.LongLength} bytes");
        if (response.Connection is { } conn)
            services.Log.Debug($"connection: tls {conn.TlsProtocol ?? "—"} · proxy {(conn.ViaProxy ? "yes" : "no")} · client cert sent {(conn.ClientCertificateSent ? "yes" : "no")}");
```

3e. Unify the post-response state writes. Replace the capture call (lines 167-168):

```csharp
        if (captureRules.Count > 0 && response.Error is null)
            ApplyCaptures(captureRules, response, workspace, services, stderr);
```

with:

```csharp
        bool stateDirty = false;
        if (captureRules.Count > 0 && response.Error is null)
        {
            var outcome = CaptureApplier.Apply(state, captureRules, response.Body, response.ContentType, response.Headers);
            var ok = outcome.Where(o => o.Ok).Select(o => o.Variable).ToList();
            if (ok.Count > 0) stderr.WriteLine("captured " + string.Join(", ", ok));
            foreach (var b in outcome.Where(o => !o.Ok)) stderr.WriteLine($"capture '{b.Variable}' failed: {b.Error}");
            stateDirty |= outcome.Count > 0;
        }
        if (!noAutoToken && response.Error is null &&
            TokenService.Capture(state, url, response.Body, response.ContentType, response.Headers) is { } captured)
        {
            if (!quiet)
            {
                string expiry = captured.ExpiresUtc is { } e
                    ? $", expires in {(int)Math.Max(1, (e - DateTime.UtcNow).TotalMinutes)} min" : "";
                stderr.WriteLine($"note: captured bearer token for {TokenService.HostOf(url)} ({captured.Source}{expiry})");
            }
            services.Log.Debug($"token captured for {captured.Origin} ({captured.Source}): {TokenService.Mask(captured.Token)}");
            stateDirty = true;
        }
        if (stateDirty) SaveState(state, workspace, services, stderr);
```

3f. Replace the whole `ApplyCaptures` method (lines 201-219) with:

```csharp
    private static void SaveState(AppState state, string? workspace, CliServices services, TextWriter stderr)
    {
        if (workspace is null && services.IsGuiRunning())
        {
            stderr.WriteLine("note: the GUI is running — captured values were not saved (it would overwrite them on close).");
            return;
        }
        try { state.SaveTo(workspace ?? services.LiveStatePath); }
        catch (Exception ex) { stderr.WriteLine($"warning: could not save captured values: {ex.Message}"); }
    }
```

- [ ] **Step 4: Run the new tests, then the full suite**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~AutoTokenCliTests"`
Expected: all PASS.

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj`
Expected: PASS — `CaptureCliTests` and `SendCommandTests` must still pass unchanged (same note wording, same exit codes). Note: existing tests that don't set `LiveStatePath` read the default live state now; if any fail because a real state file exists on the machine, set `LiveStatePath` to a temp path in that test's `CliServices` — do not change command behavior.

- [ ] **Step 5: Commit**

```bash
git add src/ApiTester.Cli/Commands/SendCommand.cs tests/ApiTester.Tests/Cli/AutoTokenCliTests.cs
git commit -m "Auto-capture and reuse bearer tokens in certapi send"
```

---

### Task 5: Auto tokens in `certapi run`

**Files:**
- Modify: `src/ApiTester.Cli/Commands/RunCommand.cs`
- Test: append to `tests/ApiTester.Tests/Cli/AutoTokenCliTests.cs`

**Interfaces:**
- Consumes: Task 2's `TokenService` members; `AuthType == "Auto"` semantics (legacy `None` is migrated by `AppState.LoadFrom`, which `CliWorkspace.Load` uses).
- Produces: `run` reuses a token captured by request N for request N+1 in the same invocation; `--no-auto-token` flag.

- [ ] **Step 1: Write the failing test**

Append to `AutoTokenCliTests` (uses the same `RunAsync` harness — add a workspace with two saved requests):

```csharp
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
            Assert.Contains("api/login: captured bearer token for localhost", se.ToString());
            Assert.Contains("api/list: using captured token for localhost", se.ToString());
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~AutoTokenCliTests"`
Expected: the two new tests FAIL (`--no-auto-token` unknown; no token notes).

- [ ] **Step 3: Implement in `RunCommand`**

All edits in `src/ApiTester.Cli/Commands/RunCommand.cs`.

3a. Bind the flag (after `bool json = args.Flag("--json");`, line 39):

```csharp
        bool noAutoToken = args.Flag("--no-auto-token");
```

3b. Change `Execute` to attach tokens and report the resolved URL. Replace the signature (line 131-132):

```csharp
    private static (ApiResponse Response, string Url) Execute(
        string path, RequestModel m, AppState state, bool noAutoToken,
        Dictionary<string, string> vars, bool strictVars, TextWriter stderr, CliServices services)
```

Inside `Execute`, the two early-return error paths must return tuples — change

```csharp
                return new ApiResponse { Error = new ApiError(ApiErrorKind.Unknown, $"unresolved variables: {tokens}") };
```
to
```csharp
                return (new ApiResponse { Error = new ApiError(ApiErrorKind.Unknown, $"unresolved variables: {tokens}") }, url);
```
(move the `string url = R(m.EffectiveUrl());` line **above** the unresolved-variables block so `url` is in scope), and change

```csharp
                return new ApiResponse { Error = new ApiError(ApiErrorKind.Unknown, $"certificate {m.CertThumbprint} not found in the store") };
```
to
```csharp
                return (new ApiResponse { Error = new ApiError(ApiErrorKind.Unknown, $"certificate {m.CertThumbprint} not found in the store") }, url);
```

After the auth `switch` and the (moved) `url` assignment, insert:

```csharp
        if (!noAutoToken && m.AuthType == "Auto" &&
            TokenService.AutoAttach(state, url, headers, out _) is { } used)
        {
            stderr.WriteLine($"{path}: using captured token for {TokenService.HostOf(url)}");
            services.Log.Debug($"{path}: auto token attached for {used.Origin} ({used.Source})");
        }
```

And the final send becomes:

```csharp
        var response = services.Client.SendAsync(request, cert, m.IgnoreServerCert,
            cancellationToken: services.Cancel).GetAwaiter().GetResult();
        services.Log.Debug($"{path}: " + (response.Error is null
            ? $"{response.StatusCode} · {response.Elapsed.TotalMilliseconds:F0} ms"
            : $"[{response.Error.Kind}] {response.Error.Message}"));
        return (response, url);
```

3c. Update the loop (lines 60-80). Replace:

```csharp
        bool capturedAny = false;
```
with
```csharp
        bool capturedAny = false;
        bool tokensCaptured = false;
```

and replace the loop body's first two lines:

```csharp
            var response = Execute(node.Request!, vars, strictVars, stderr, services);
            results.Add((path, node.Request!, response));
```
with
```csharp
            var (response, url) = Execute(path, node.Request!, state, noAutoToken, vars, strictVars, stderr, services);
            results.Add((path, node.Request!, response));
            if (!noAutoToken && response.Error is null &&
                TokenService.Capture(state, url, response.Body, response.ContentType, response.Headers) is { } captured)
            {
                stderr.WriteLine($"{path}: captured bearer token for {TokenService.HostOf(url)} ({captured.Source})");
                tokensCaptured = true;
            }
```

3d. Update the save condition (lines 83-92): change

```csharp
        if ((record || capturedAny) && !guiBlocksLiveWrite)
```
to
```csharp
        if ((record || capturedAny || tokensCaptured) && !guiBlocksLiveWrite)
```
and
```csharp
        else if (capturedAny && guiBlocksLiveWrite)
```
to
```csharp
        else if ((capturedAny || tokensCaptured) && guiBlocksLiveWrite)
```

- [ ] **Step 4: Run the new tests, then the full suite**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~AutoTokenCliTests"`
Expected: all PASS.

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj`
Expected: PASS (`RunCommandTests` unchanged — legacy workspaces migrate `None` → `Auto`, and requests without a stored token behave exactly as before).

- [ ] **Step 5: Commit**

```bash
git add src/ApiTester.Cli/Commands/RunCommand.cs tests/ApiTester.Tests/Cli/AutoTokenCliTests.cs
git commit -m "Reuse captured tokens across certapi run suites"
```

---

### Task 6: Session tokens in the MCP server

**Files:**
- Modify: `src/ApiTester.Cli/Commands/McpCommand.cs`
- Modify: `src/ApiTester.Cli/Commands/SendCommand.cs` (`BuildEnvelope` gains optional notes)
- Test: append to `tests/ApiTester.Tests/Cli/McpCommandTests.cs`

**Interfaces:**
- Consumes: Task 2's `TokenService`; `SendCommand.BuildEnvelope`.
- Produces: `SendCommand.BuildEnvelope(ApiResponse r, bool includeBody, IReadOnlyList<string>? notes = null)` — the envelope gains a `"notes"` array when notes are present. MCP tokens are **in-memory per server session** (never written to disk); `--no-auto-token` disables them.

- [ ] **Step 1: Write the failing test**

Append to `tests/ApiTester.Tests/Cli/McpCommandTests.cs` (follow the file's existing harness for building tools and calling handlers — it already constructs `McpCommand.BuildTools(...)` with a loopback server; mirror the existing send_request test's setup):

```csharp
    [Fact]
    public async Task Send_request_captures_then_reuses_a_session_token()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("McpClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(
            serverCert, clientCert.Thumbprint!, "{\"access_token\":\"mcp-tok\"}");

        var services = new CliServices { IsGuiRunning = () => false };
        var tools = McpCommand.BuildTools(clientCert, new HostAllowlist(new List<string>()),
            insecure: true, timeout: 30, includeLocalMachine: false, workspace: null,
            noAutoToken: false, services);
        var send = tools.Single(t => t.Name == "send_request");

        ToolResult Call(string json) =>
            send.Handler(System.Text.Json.JsonDocument.Parse(json).RootElement);

        var first = Call($"{{\"url\":\"{server.BaseUrl}\"}}");
        Assert.Contains("captured bearer token", first.Json);

        var second = Call($"{{\"url\":\"{server.BaseUrl}\"}}");
        Assert.Contains("using captured token", second.Json);

        // An explicit Authorization header wins over the session token.
        var explicitAuth = Call($"{{\"url\":\"{server.BaseUrl}\",\"headers\":{{\"Authorization\":\"Bearer mine\"}}}}");
        Assert.DoesNotContain("using captured token", explicitAuth.Json);
    }

    [Fact]
    public async Task No_auto_token_disables_the_session_store()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("McpClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(
            serverCert, clientCert.Thumbprint!, "{\"access_token\":\"mcp-tok\"}");

        var services = new CliServices { IsGuiRunning = () => false };
        var tools = McpCommand.BuildTools(clientCert, new HostAllowlist(new List<string>()),
            insecure: true, timeout: 30, includeLocalMachine: false, workspace: null,
            noAutoToken: true, services);
        var send = tools.Single(t => t.Name == "send_request");
        var first = send.Handler(System.Text.Json.JsonDocument.Parse($"{{\"url\":\"{server.BaseUrl}\"}}").RootElement);
        Assert.DoesNotContain("captured bearer token", first.Json);
    }
```

Note: if the existing `McpCommandTests` builds tools through a helper, extend that helper with the `noAutoToken` parameter instead of duplicating setup — keep the file's local conventions.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~McpCommandTests"`
Expected: build error — `BuildTools` has no `noAutoToken` parameter.

- [ ] **Step 3: Extend `BuildEnvelope` with notes**

In `src/ApiTester.Cli/Commands/SendCommand.cs`, change the `BuildEnvelope` signature (line 221):

```csharp
    internal static string BuildEnvelope(ApiResponse r, bool includeBody, IReadOnlyList<string>? notes = null)
```

and insert before the final body-append block (before `if (includeBody && r.Error is null)` near the end):

```csharp
        if (notes is { Count: > 0 }) obj["notes"] = notes;
```

- [ ] **Step 4: Implement the session token store in `McpCommand`**

In `src/ApiTester.Cli/Commands/McpCommand.cs`:

4a. Bind the flag in `Run` (after `string? workspace = args.Value("--workspace");`, line 40):

```csharp
        bool noAutoToken = args.Flag("--no-auto-token");
```

and pass it through (line 58):

```csharp
        var server = new McpServer(BuildTools(cert, allow, insecure, timeout, localMachine, workspace, noAutoToken, services), Version());
```

4b. Change the `BuildTools` signature (line 71):

```csharp
    internal static IReadOnlyList<ToolDef> BuildTools(
        X509Certificate2? cert, HostAllowlist allow, bool insecure, int timeout,
        bool includeLocalMachine, string? workspace, bool noAutoToken, CliServices services)
```

4c. Replace the `Envelope`/`SendUrl` helpers (lines 75-94) with a session store and token-aware sender:

```csharp
        // Session-scoped token store: lives for this MCP process only, never written to disk.
        var tokenState = new AppState();

        ToolResult SendUrl(string method, string url, IEnumerable<KeyValuePair<string, string>> headers,
            string? body, string? contentType, bool allowAutoToken = true)
        {
            if (!allow.IsAllowed(url))
                return new ToolResult(JsonSerializer.Serialize(new { error = $"host for '{url}' is not allowed" }), true);

            var headerList = headers.ToList();
            var notes = new List<string>();
            if (!noAutoToken && allowAutoToken)
            {
                var used = TokenService.AutoAttach(tokenState, url, headerList, out var expired);
                if (used is not null) notes.Add($"using captured token for {TokenService.HostOf(url)}");
                else if (expired is not null) notes.Add($"captured token for {TokenService.HostOf(url)} has expired");
            }

            var request = new ApiRequest
            {
                Method = new HttpMethod(method.ToUpperInvariant()),
                Url = url,
                Headers = headerList,
                Body = body,
                ContentType = body is not null ? (contentType ?? "application/json") : null,
                Timeout = TimeSpan.FromSeconds(timeout)
            };
            var response = services.Client.SendAsync(request, cert, insecure, followRedirects: false, cancellationToken: services.Cancel)
                .GetAwaiter().GetResult();

            if (!noAutoToken && response.Error is null &&
                TokenService.Capture(tokenState, url, response.Body, response.ContentType, response.Headers) is { } captured)
                notes.Add($"captured bearer token for {TokenService.HostOf(url)} ({captured.Source})");

            return new ToolResult(SendCommand.BuildEnvelope(response, includeBody: true, notes), IsError: response.Error is not null);
        }
```

4d. In `run_saved` (line 192), pass the auth-mode gate so an explicit `None` never sends auth:

```csharp
                return SendUrl(m.Method, url, headers, body,
                    m.ContentType == "(none)" ? null : m.ContentType,
                    allowAutoToken: m.AuthType == "Auto");
```

- [ ] **Step 5: Run the new tests, then the full suite**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~McpCommandTests"`
Expected: all PASS (existing MCP tests updated only where they call `BuildTools` directly — add `noAutoToken: false`).

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/ApiTester.Cli/Commands/McpCommand.cs src/ApiTester.Cli/Commands/SendCommand.cs tests/ApiTester.Tests/Cli/McpCommandTests.cs
git commit -m "Give the MCP tools session-scoped auto tokens"
```

---

### Task 7: Auto auth mode and token chip in the GUI

**Files:**
- Modify: `src/ApiTester.App/MainWindow.xaml` (auth combo ~line 344; status bar ~line 608)
- Modify: `src/ApiTester.App/MainWindow.xaml.cs` (auth mapping, send path, chip)

No new unit tests — the logic lives in Core (Tasks 1-2); this task is wiring. Verification is a build plus the smoke checklist in Step 6.

**Interfaces:**
- Consumes: `TokenService.Capture/TokenFor/AutoAttach/OriginOf/MaskAuthorization`, `AppState.SessionTokens/AutoTokens`.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Update the auth combo XAML**

In `src/ApiTester.App/MainWindow.xaml`, replace the `AuthTypeCombo` items (lines 344-349):

```xml
                            <ComboBox x:Name="AuthTypeCombo" Width="180" SelectedIndex="0"
                                      SelectionChanged="AuthTypeCombo_SelectionChanged">
                                <ComboBoxItem>Auto (captured token)</ComboBoxItem>
                                <ComboBoxItem>None (never send auth)</ComboBoxItem>
                                <ComboBoxItem>Bearer token</ComboBoxItem>
                                <ComboBoxItem>Basic</ComboBoxItem>
                            </ComboBox>
```

Directly after the closing `</StackPanel>` of that TYPE row, add the hint:

```xml
                        <TextBlock x:Name="AutoAuthHint" FontSize="12" Foreground="{StaticResource Text.Muted}"
                                   TextWrapping="Wrap" Margin="60,0,0,8"
                                   Text="Sends the bearer token captured from this website's last auth response, when one exists. Other websites never receive it."/>
```

- [ ] **Step 2: Add the token chip to the status bar XAML**

In the status bar `DockPanel` (line 610-614), between `SelfTestButton` and `StatusText`:

```xml
                    <Border x:Name="TokenChip" DockPanel.Dock="Right" Visibility="Collapsed" Margin="0,0,10,0"
                            VerticalAlignment="Center" Background="{StaticResource Bg.Input}"
                            BorderBrush="{StaticResource Border}" BorderThickness="1" CornerRadius="10"
                            Padding="10,3" Cursor="Hand" MouseLeftButtonUp="TokenChip_Click"
                            ToolTip="A bearer token was captured for this website — click for details">
                        <TextBlock x:Name="TokenChipText" FontSize="12" Foreground="{StaticResource Text.Soft}"/>
                    </Border>
```

- [ ] **Step 3: Update the auth mapping in code-behind**

In `src/ApiTester.App/MainWindow.xaml.cs`:

3a. `AuthTypeCombo_SelectionChanged` (lines 1313-1318) becomes:

```csharp
    private void AuthTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BearerPanel is null) return; // during init
        AutoAuthHint.Visibility = AuthTypeCombo.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        BearerPanel.Visibility = AuthTypeCombo.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        BasicPanel.Visibility = AuthTypeCombo.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
    }
```

3b. `LoadIntoControls` (lines 249-252): replace the auth lines with:

```csharp
            AuthTypeCombo.SelectedIndex = m.AuthType switch { "Bearer" => 2, "Basic" => 3, "None" => 1, _ => 0 };
            BearerTokenBox.Text = m.AuthType == "Bearer" ? m.AuthSecret ?? "" : "";
            BasicUserBox.Text = m.AuthUser ?? "";
            BasicPassBox.Text = m.AuthType == "Basic" ? m.AuthSecret ?? "" : "";
```

3c. `CaptureControlsInto` (lines 277-279): replace the auth lines with:

```csharp
        m.AuthType = AuthTypeCombo.SelectedIndex switch { 2 => "Bearer", 3 => "Basic", 1 => "None", _ => "Auto" };
        m.AuthUser = BasicUserBox.Text;
        m.AuthSecret = AuthTypeCombo.SelectedIndex == 2 ? BearerTokenBox.Text : BasicPassBox.Text;
```

3d. `BuildHeaders` `switch` (lines 1336-1345) needs no change — `"Auto"` and `"None"` both add nothing there; Auto attaches later.

- [ ] **Step 4: Attach, capture, and mask in the send path**

4a. In `BuildRequest` (line 1349), after `var (url, headers, body, unresolved) = ResolveActive();` add:

```csharp
        if (m.AuthType == "Auto")
            TokenService.AutoAttach(_state, url, headers, out _);
```

4b. In `SendRequestAsync`, after the collection `RecordResult` block (line 1449) and before the `Captures` block, add:

```csharp
            if (response.Error is null)
                TokenService.Capture(_state, request.Url, response.Body, response.ContentType, response.Headers);
            UpdateTokenChip();
```

4c. Mask Authorization in the network trace — in the `RecordNetwork` call (line 1440), replace the `RequestHeaders` line with:

```csharp
                    RequestHeaders = (request.Headers ?? new List<KeyValuePair<string, string>>())
                        .Select(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                            ? new KeyValuePair<string, string>(h.Key, TokenService.MaskAuthorization(h.Value))
                            : h).ToList(),
```

- [ ] **Step 5: Implement the chip**

Add to `MainWindow.xaml.cs` (near the certificates region, after `SelectCertByThumbprint`):

```csharp
    // ---------- session token chip ----------

    /// <summary>The URL the editor currently points at (base + path), without touching the model.</summary>
    private string CurrentEditorUrl() => UrlHelper.Combine(BaseUrlBox.Text, UrlBox.Text);

    private void UpdateTokenChip()
    {
        var url = CurrentEditorUrl();
        var origin = TokenService.OriginOf(url);
        var token = origin is null ? null : _state.SessionTokens.FirstOrDefault(t => t.Origin == origin);
        if (token is null) { TokenChip.Visibility = Visibility.Collapsed; return; }

        TokenChip.Visibility = Visibility.Visible;
        string suffix =
            !_state.AutoTokens ? " · auto off"
            : token.IsExpired ? " · expired"
            : token.ExpiresUtc is { } e ? $" · expires in {Math.Max(1, (int)(e - DateTime.UtcNow).TotalMinutes)}m"
            : "";
        TokenChipText.Text = $"Token: {new Uri(url).Host}{suffix}";
    }

    private void TokenChip_Click(object sender, MouseButtonEventArgs e)
    {
        var url = CurrentEditorUrl();
        var origin = TokenService.OriginOf(url);
        var token = origin is null ? null : _state.SessionTokens.FirstOrDefault(t => t.Origin == origin);
        if (token is null) { UpdateTokenChip(); return; }

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem
        {
            Header = $"{token.Source} · captured {token.CapturedUtc.ToLocalTime():HH:mm}" +
                     (token.ExpiresUtc is { } ex ? $" · expires {ex.ToLocalTime():HH:mm}" : ""),
            IsEnabled = false
        });
        menu.Items.Add(new Separator());

        var clearOne = new MenuItem { Header = $"Clear token for {new Uri(url).Host}" };
        clearOne.Click += (_, _) => { _state.SessionTokens.Remove(token); UpdateTokenChip(); };
        menu.Items.Add(clearOne);

        var clearAll = new MenuItem { Header = "Clear all captured tokens" };
        clearAll.Click += (_, _) => { _state.SessionTokens.Clear(); UpdateTokenChip(); };
        menu.Items.Add(clearAll);

        menu.Items.Add(new Separator());
        var toggle = new MenuItem { Header = "Automatically use captured tokens", IsCheckable = true, IsChecked = _state.AutoTokens };
        toggle.Click += (_, _) => { _state.AutoTokens = toggle.IsChecked; UpdateTokenChip(); };
        menu.Items.Add(toggle);

        menu.PlacementTarget = TokenChip;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }
```

Wire the refresh points:
- End of `TabStrip_SelectionChanged` (inside the `try`, after `BindNetwork(newTab);`): add `UpdateTokenChip();`
- The existing `UrlBox` TextChanged handler (~line 1794, the one setting `ActiveRequest.Path = UrlBox.Text;`): add `UpdateTokenChip();` at the end.
- `BaseUrlBox`: in `MainWindow.xaml` add `TextChanged="BaseUrlBox_TextChanged"` to the `BaseUrlBox` TextBox (line 216), and add the handler:

```csharp
    private void BaseUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TokenChip is null) return; // during init
        UpdateTokenChip();
    }
```

- End of the `MainWindow` constructor body: add `UpdateTokenChip();`

- [ ] **Step 6: Build and smoke-check**

Run: `dotnet build ApiTester.sln`
Expected: build succeeds with no warnings about missing XAML members.

Manual smoke list (run `src/ApiTester.App` once):
1. New tab defaults to **Auto (captured token)**; hint text visible.
2. Bearer/Basic panels appear on their entries; None shows neither.
3. After a send whose response has `access_token`, the chip appears; click shows details/clear/toggle.
4. A follow-on Auto send to the same host carries `Authorization` (visible masked in the Network tab).

- [ ] **Step 7: Commit**

```bash
git add src/ApiTester.App/MainWindow.xaml src/ApiTester.App/MainWindow.xaml.cs
git commit -m "Add the Auto auth mode and token chip to the GUI"
```

---

### Task 8: Collection website/cert defaults

**Files:**
- Create: `src/ApiTester.Core/CollectionDefaults.cs`
- Create: `src/ApiTester.App/CollectionDefaultsDialog.xaml`
- Create: `src/ApiTester.App/CollectionDefaultsDialog.xaml.cs`
- Modify: `src/ApiTester.Core/AppState.cs` (`CollectionNode`, ~line 130)
- Modify: `src/ApiTester.App/MainWindow.xaml` (TreeView, line 124)
- Modify: `src/ApiTester.App/MainWindow.xaml.cs` (open + auto-remember + menu handler)
- Test: `tests/ApiTester.Tests/CollectionDefaultsTests.cs`

**Interfaces:**
- Consumes: `CollectionNode` tree.
- Produces:
  - `CollectionNode.DefaultBaseUrl : string?`, `CollectionNode.DefaultCertThumbprint : string?`
  - `static (string? BaseUrl, string? CertThumbprint) CollectionDefaults.For(IEnumerable<CollectionNode> roots, CollectionNode target)`
  - `static CollectionNode? CollectionDefaults.RootOf(IEnumerable<CollectionNode> roots, CollectionNode target)`
  - `static (bool Ok, string? BaseUrl, string? CertThumbprint) CollectionDefaultsDialog.Show(Window owner, string folderName, string? currentBaseUrl, string? currentThumbprint, IReadOnlyList<(string Label, string? Thumbprint)> certOptions, IReadOnlyList<string> savedBaseUrls)`

- [ ] **Step 1: Write the failing tests**

Create `tests/ApiTester.Tests/CollectionDefaultsTests.cs`:

```csharp
using ApiTester.Core;

namespace ApiTester.Tests;

public class CollectionDefaultsTests
{
    private static (List<CollectionNode> Roots, CollectionNode Leaf, CollectionNode Mid, CollectionNode Root) Tree()
    {
        var leaf = new CollectionNode { Name = "req", IsFolder = false, Request = new RequestModel() };
        var mid = new CollectionNode { Name = "mid", IsFolder = true };
        var root = new CollectionNode { Name = "root", IsFolder = true };
        mid.Children.Add(leaf);
        root.Children.Add(mid);
        return (new List<CollectionNode> { root }, leaf, mid, root);
    }

    [Fact]
    public void Nearest_ancestor_default_wins()
    {
        var (roots, leaf, mid, root) = Tree();
        root.DefaultBaseUrl = "https://root.example";
        root.DefaultCertThumbprint = "ROOTCERT";
        mid.DefaultBaseUrl = "https://mid.example";

        var (baseUrl, cert) = CollectionDefaults.For(roots, leaf);
        Assert.Equal("https://mid.example", baseUrl);   // nearest folder wins per value
        Assert.Equal("ROOTCERT", cert);                 // falls through where the nearer folder is silent
    }

    [Fact]
    public void No_defaults_yields_nulls_and_unknown_target_is_safe()
    {
        var (roots, leaf, _, _) = Tree();
        Assert.Equal((null, null), CollectionDefaults.For(roots, leaf));

        var stranger = new CollectionNode { Name = "x", IsFolder = false };
        Assert.Equal((null, null), CollectionDefaults.For(roots, stranger));
        Assert.Null(CollectionDefaults.RootOf(roots, stranger));
    }

    [Fact]
    public void RootOf_finds_the_top_level_ancestor()
    {
        var (roots, leaf, _, root) = Tree();
        Assert.Same(root, CollectionDefaults.RootOf(roots, leaf));

        var topLevel = new CollectionNode { Name = "solo", IsFolder = false, Request = new RequestModel() };
        roots.Add(topLevel);
        Assert.Null(CollectionDefaults.RootOf(roots, topLevel));   // no ancestor folder
    }

    [Fact]
    public void Defaults_round_trip_through_serialization()
    {
        var state = new AppState();
        var folder = new CollectionNode { Name = "api", IsFolder = true, DefaultBaseUrl = "https://x", DefaultCertThumbprint = "ABC" };
        state.Collections.Add(folder);
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            state.SaveTo(path);
            var loaded = AppState.LoadFrom(path);
            Assert.Equal("https://x", loaded.Collections[0].DefaultBaseUrl);
            Assert.Equal("ABC", loaded.Collections[0].DefaultCertThumbprint);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~CollectionDefaultsTests"`
Expected: build error — the properties and `CollectionDefaults` class don't exist.

- [ ] **Step 3: Add the Core pieces**

3a. In `src/ApiTester.Core/AppState.cs`, add to `CollectionNode` after `public RequestModel? Request { get; set; }` (line 132):

```csharp
    /// <summary>Folder-level defaults: the website and client certificate a request opened from
    /// this folder inherits when it doesn't carry its own. The nearest ancestor with a value wins.</summary>
    public string? DefaultBaseUrl { get; set; }
    public string? DefaultCertThumbprint { get; set; }
```

3b. Create `src/ApiTester.Core/CollectionDefaults.cs`:

```csharp
namespace ApiTester.Core;

/// <summary>Resolves the website/certificate a collection request inherits from its ancestor
/// folders — the nearest ancestor with a value wins, per value.</summary>
public static class CollectionDefaults
{
    public static (string? BaseUrl, string? CertThumbprint) For(IEnumerable<CollectionNode> roots, CollectionNode target)
    {
        var chain = AncestorsOf(roots, target);
        string? baseUrl = null, cert = null;
        for (int i = chain.Count - 1; i >= 0; i--)   // nearest first
        {
            baseUrl ??= string.IsNullOrWhiteSpace(chain[i].DefaultBaseUrl) ? null : chain[i].DefaultBaseUrl!.Trim();
            cert ??= string.IsNullOrEmpty(chain[i].DefaultCertThumbprint) ? null : chain[i].DefaultCertThumbprint;
        }
        return (baseUrl, cert);
    }

    /// <summary>The target's top-level ancestor folder, or null when it sits at the root.</summary>
    public static CollectionNode? RootOf(IEnumerable<CollectionNode> roots, CollectionNode target)
    {
        var chain = AncestorsOf(roots, target);
        return chain.Count > 0 ? chain[0] : null;
    }

    /// <summary>The folders from the tree root down to (excluding) the target; empty when the
    /// target is top-level or absent.</summary>
    private static List<CollectionNode> AncestorsOf(IEnumerable<CollectionNode> roots, CollectionNode target)
    {
        var chain = new List<CollectionNode>();
        return Walk(roots) ? chain : new List<CollectionNode>();

        bool Walk(IEnumerable<CollectionNode> scope)
        {
            foreach (var n in scope)
            {
                if (ReferenceEquals(n, target)) return true;
                if (!n.IsFolder) continue;
                chain.Add(n);
                if (Walk(n.Children)) return true;
                chain.RemoveAt(chain.Count - 1);
            }
            return false;
        }
    }
}
```

- [ ] **Step 4: Run the Core tests**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~CollectionDefaultsTests"`
Expected: all PASS.

- [ ] **Step 5: Create the defaults dialog**

Create `src/ApiTester.App/CollectionDefaultsDialog.xaml` (chrome follows the app's dialog pattern — dark theme resources, no OS title bar; check `InputDialog.xaml` if a resource key differs):

```xml
<Window x:Class="ApiTester.App.CollectionDefaultsDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Set website &amp; certificate" Width="560" SizeToContent="Height"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize"
        Background="{StaticResource Bg.Window}">
    <StackPanel Margin="18">
        <TextBlock x:Name="HeaderText" FontSize="15" FontWeight="SemiBold"
                   Foreground="{StaticResource Text.Soft}" Margin="0,0,0,4"/>
        <TextBlock Text="Requests opened from this collection use these when they don't have their own."
                   FontSize="12" Foreground="{StaticResource Text.Muted}" TextWrapping="Wrap" Margin="0,0,0,14"/>

        <TextBlock Text="WEBSITE" FontSize="11" FontWeight="SemiBold"
                   Foreground="{StaticResource Text.Muted}" Margin="0,0,0,4"/>
        <DockPanel Margin="0,0,0,12">
            <ComboBox x:Name="SavedCombo" Width="34" DockPanel.Dock="Right" Margin="6,0,0,0"
                      SelectionChanged="SavedCombo_SelectionChanged"/>
            <TextBox x:Name="BaseUrlBox" VerticalContentAlignment="Center"/>
        </DockPanel>

        <TextBlock Text="CERTIFICATE" FontSize="11" FontWeight="SemiBold"
                   Foreground="{StaticResource Text.Muted}" Margin="0,0,0,4"/>
        <ComboBox x:Name="CertCombo" Margin="0,0,0,18"/>

        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
            <Button Content="Clear defaults" Width="110" Margin="0,0,8,0" Click="Clear_Click"/>
            <Button Content="Cancel" Width="90" Margin="0,0,8,0" IsCancel="True"/>
            <Button Content="Save" Width="90" IsDefault="True" Click="Save_Click"/>
        </StackPanel>
    </StackPanel>
</Window>
```

Create `src/ApiTester.App/CollectionDefaultsDialog.xaml.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ApiTester.App;

public partial class CollectionDefaultsDialog : Window
{
    private readonly IReadOnlyList<(string Label, string? Thumbprint)> _certOptions;
    private bool _ok;
    private bool _cleared;

    private CollectionDefaultsDialog(string folderName, string? currentBaseUrl, string? currentThumbprint,
        IReadOnlyList<(string Label, string? Thumbprint)> certOptions, IReadOnlyList<string> savedBaseUrls)
    {
        InitializeComponent();
        _certOptions = certOptions;
        HeaderText.Text = $"Defaults for “{folderName}”";
        BaseUrlBox.Text = currentBaseUrl ?? "";

        var saved = new List<string> { "…" };
        saved.AddRange(savedBaseUrls);
        SavedCombo.ItemsSource = saved;
        SavedCombo.SelectedIndex = 0;

        CertCombo.ItemsSource = certOptions.Select(o => o.Label).ToList();
        int idx = certOptions.ToList().FindIndex(o => o.Thumbprint == currentThumbprint);
        CertCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    public static (bool Ok, string? BaseUrl, string? CertThumbprint) Show(
        Window owner, string folderName, string? currentBaseUrl, string? currentThumbprint,
        IReadOnlyList<(string Label, string? Thumbprint)> certOptions, IReadOnlyList<string> savedBaseUrls)
    {
        var dlg = new CollectionDefaultsDialog(folderName, currentBaseUrl, currentThumbprint, certOptions, savedBaseUrls)
        { Owner = owner };
        dlg.ShowDialog();
        if (!dlg._ok) return (false, null, null);
        if (dlg._cleared) return (true, null, null);
        string? baseUrl = string.IsNullOrWhiteSpace(dlg.BaseUrlBox.Text) ? null : dlg.BaseUrlBox.Text.Trim();
        int i = dlg.CertCombo.SelectedIndex;
        string? thumb = i >= 0 && i < certOptions.Count ? certOptions[i].Thumbprint : null;
        return (true, baseUrl, thumb);
    }

    private void SavedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SavedCombo.SelectedIndex > 0 && SavedCombo.SelectedItem is string s)
        {
            BaseUrlBox.Text = s;
            SavedCombo.SelectedIndex = 0;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) { _ok = true; _cleared = true; Close(); }
    private void Save_Click(object sender, RoutedEventArgs e) { _ok = true; Close(); }
}
```

If `Bg.Window` is not an existing resource key, use the same window Background as `InputDialog.xaml`.

- [ ] **Step 6: Wire the tree menu, blank-fill, and auto-remember**

6a. In `src/ApiTester.App/MainWindow.xaml`, extend the TreeView (line 124-125):

```xml
                        <TreeView x:Name="CollectionsTree" ScrollViewer.VerticalScrollBarVisibility="Auto"
                                  MouseDoubleClick="CollectionsTree_MouseDoubleClick"
                                  PreviewMouseRightButtonDown="CollectionsTree_PreviewMouseRightButtonDown">
                            <TreeView.ContextMenu>
                                <ContextMenu>
                                    <MenuItem Header="Set website &amp; certificate…" Click="SetCollectionDefaults_Click"/>
                                </ContextMenu>
                            </TreeView.ContextMenu>
```

(keep whatever child content the TreeView already has after this).

6b. In `MainWindow.xaml.cs`, extend `CollectionsTree_MouseDoubleClick` (lines 383-394) — after `clone.SourceCollectionId = node.Id;` insert:

```csharp
            // Fill only the blanks: the saved request wins, then folder defaults, then the current tab.
            var (defBase, defCert) = CollectionDefaults.For(_collections, node);
            if (string.IsNullOrWhiteSpace(clone.BaseUrl))
                clone.BaseUrl = defBase ?? ActiveRequest?.BaseUrl;
            if (string.IsNullOrEmpty(clone.CertThumbprint))
                clone.CertThumbprint = defCert ?? SelectedThumbprint();
```

6c. Add the two handlers (near the other collections handlers):

```csharp
    private void CollectionsTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(CollectionsTree, e.OriginalSource as DependencyObject) is TreeViewItem item)
            item.IsSelected = true;
    }

    private void SetCollectionDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (CollectionsTree.SelectedItem is not CollectionNode { IsFolder: true } folder)
        {
            StatusText.Text = "Select a collection or folder to set defaults on.";
            return;
        }
        var options = _allOptions.Select(o => (o.Label, o.Thumbprint)).ToList();
        var (ok, baseUrl, thumb) = CollectionDefaultsDialog.Show(
            this, folder.Name, folder.DefaultBaseUrl, folder.DefaultCertThumbprint, options, _state.SavedBaseUrls);
        if (!ok) return;
        folder.DefaultBaseUrl = baseUrl;
        folder.DefaultCertThumbprint = thumb;
        StatusText.Text = baseUrl is null && thumb is null
            ? $"Cleared defaults for “{folder.Name}”."
            : $"Defaults for “{folder.Name}” saved.";
    }
```

6d. Auto-remember on first success — in `SendRequestAsync`, directly after the `RecordResult` block (line 1449) add:

```csharp
            // First successful send from a collection whose root has no defaults yet: remember
            // the website and certificate that worked, so sibling endpoints inherit them.
            if (response.Error is null && model.SourceCollectionId is { } rememberId &&
                FindNodeById(rememberId) is { IsFolder: false } rememberLeaf &&
                CollectionDefaults.RootOf(_collections, rememberLeaf) is { } rootFolder &&
                string.IsNullOrWhiteSpace(rootFolder.DefaultBaseUrl) &&
                string.IsNullOrEmpty(rootFolder.DefaultCertThumbprint) &&
                (!string.IsNullOrWhiteSpace(model.BaseUrl) || !string.IsNullOrEmpty(model.CertThumbprint)))
            {
                rootFolder.DefaultBaseUrl = string.IsNullOrWhiteSpace(model.BaseUrl) ? null : model.BaseUrl!.Trim();
                rootFolder.DefaultCertThumbprint = string.IsNullOrEmpty(model.CertThumbprint) ? null : model.CertThumbprint;
                StatusText.Text += $"   Remembered website & certificate for “{rootFolder.Name}”.";
            }
```

- [ ] **Step 7: Build, run the suite, smoke-check**

Run: `dotnet build ApiTester.sln` then `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj`
Expected: build succeeds, all tests PASS.

Manual smoke list:
1. Import an OpenAPI file → double-click an endpooint with the website/cert already set on the active tab → both fields arrive filled.
2. Right-click a folder → "Set website & certificate…" → values stick and win over tab inheritance.
3. First successful send from a defaults-less collection appends the "Remembered…" status note; the second doesn't.

- [ ] **Step 8: Commit**

```bash
git add src/ApiTester.Core/AppState.cs src/ApiTester.Core/CollectionDefaults.cs src/ApiTester.App/CollectionDefaultsDialog.xaml src/ApiTester.App/CollectionDefaultsDialog.xaml.cs src/ApiTester.App/MainWindow.xaml src/ApiTester.App/MainWindow.xaml.cs tests/ApiTester.Tests/CollectionDefaultsTests.cs
git commit -m "Inherit website and certificate defaults in collections"
```

---

### Task 9: Example-rich help for every command

**Files:**
- Modify: `src/ApiTester.Cli/CliApp.cs` (`Usage`)
- Modify: all 8 `Help` constants in `src/ApiTester.Cli/Commands/*.cs`
- Test: `tests/ApiTester.Tests/Cli/HelpTextTests.cs`

**Interfaces:** none new — text only. Every help gains an `Examples:` section and documents the global flags; `send`, `run`, and `mcp` document `--no-auto-token` and the token notes.

- [ ] **Step 1: Write the failing guard tests**

Create `tests/ApiTester.Tests/Cli/HelpTextTests.cs`:

```csharp
using ApiTester.Cli;
using ApiTester.Cli.Commands;

namespace ApiTester.Tests.Cli;

public class HelpTextTests
{
    public static TheoryData<string, string> Helps => new()
    {
        { "send", SendCommand.Help },
        { "run", RunCommand.Help },
        { "certs", CertsCommand.Help },
        { "selftest", SelfTestCommand.Help },
        { "import", ImportCommand.Help },
        { "export", ExportCommand.Help },
        { "serve", ServeCommand.Help },
        { "mcp", McpCommand.Help },
    };

    [Theory]
    [MemberData(nameof(Helps))]
    public void Every_command_help_has_examples_and_the_global_flags(string name, string help)
    {
        Assert.Contains("Examples:", help);
        Assert.Contains($"certapi {name}", help);
        Assert.Contains("--debug", help);
    }

    [Fact]
    public void The_overview_has_a_quick_start_and_global_options()
    {
        Assert.Contains("Examples:", CliApp.Usage);
        Assert.Contains("--debug", CliApp.Usage);
        Assert.Contains("--log-file", CliApp.Usage);
    }

    [Theory]
    [InlineData("send")]
    [InlineData("run")]
    [InlineData("mcp")]
    public void Token_aware_commands_document_no_auto_token(string name)
    {
        var help = name switch { "send" => SendCommand.Help, "run" => RunCommand.Help, _ => McpCommand.Help };
        Assert.Contains("--no-auto-token", help);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~HelpTextTests"`
Expected: FAIL — no `Examples:` sections yet.

- [ ] **Step 3: Rewrite the help text**

3a. `CliApp.Usage` becomes:

```csharp
    public const string Usage = """
        Usage: certapi <command> [options]

        Commands:
          send <url>        Send a one-off request (client cert from the Windows store)
          run <path>        Run saved requests from your collections (or --all)
          certs             List client certificates
          selftest          Prove the mTLS path end-to-end against a loopback server
          import            Import a cURL command or an OpenAPI file into collections
          export            Export collections as OpenAPI, or the whole workspace
          serve <upstream>  Run a local mTLS gateway that forwards to <upstream>
          mcp               Run an MCP server so AI agents can make mTLS calls
          help [command]    Show help (for one command, or this overview)

        Global options (work on every command, anywhere on the line):
          --debug           Rich diagnostics on stderr: resolved URLs, headers (Authorization
                            masked), certificate lookup, TLS details, timings, full stack traces
          --log-file <path> Append everything (diagnostics + all stderr output) to a log file

        Examples:
          certapi certs
          certapi send https://api.example.com/health --cert "CN=My Client"
          certapi send https://api.example.com/login -X POST -d "{\"user\":\"me\"}"
              # a token in the response (access_token / id_token / …) is captured
              # automatically and reused for later requests to the same host
          certapi run smoke-suite --env Staging
          certapi selftest
          certapi send https://api.example.com/x --debug --log-file certapi.log

        Run 'certapi help <command>' for options. 'certapi --version' prints the version.
        """;
```

3b. `SendCommand.Help` becomes:

```csharp
    public const string Help = """
        Usage: certapi send <url> [options]

        Request:
          -X, --method <m>        HTTP method (default GET)
          -H, --header "k: v"     Add a header (repeatable)
          -d, --data <body>       Request body ( --data-file <file> reads it from disk )
          --content-type <ct>     Body content type (default application/json)
          --bearer <token>        Authorization: Bearer …
          --basic <user:pass>     Authorization: Basic …
          --timeout <seconds>     Default 100

        TLS / certificates:
          --cert <thumb|subject>  Client certificate from the Windows store
          --store <location>      CurrentUser (default); LocalMachine searches both stores
          --insecure              Ignore server certificate errors

        Automatic tokens:
          A bearer token found in a response (access_token, id_token, token, accessToken, jwt,
          or an X-Auth-Token / X-Access-Token header) is captured and scoped to that host.
          Later sends to the same host attach it automatically — unless you pass explicit auth
          (--bearer / --basic / an Authorization header).
          --no-auto-token         Disable capture and reuse for this invocation

        Variables:
          --env <name>            Environment ({{var}} values) from your workspace
          --var k=v               Override/add a variable (repeatable)
          --workspace <file>      Load environments from a workspace file instead of the live state
          --strict-vars           Unresolved {{tokens}} become an error instead of a warning
          --capture var=path      Save a response value into an environment variable after the
                                  send (repeatable). path is a JSON body path (access_token,
                                  data.token) or header:Name for a response header.

        Output:
          -o, --output <file>     Write the body to a file instead of stdout
          --include               Print status line and headers before the body
          --pretty                Pretty-print the body (JSON/XML; hex for binary)
          --json                  Print a JSON result envelope instead of the raw body
          --fail                  Exit 1 when the HTTP status is 400 or higher
          -q, --quiet             No metadata line on stderr

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          # Simple GET with a client certificate picked by subject
          certapi send https://api.example.com/users --cert "CN=My Client"

          # POST JSON, pretty-print the response
          certapi send https://api.example.com/users -X POST -d "{\"name\":\"Ada\"}" --pretty

          # Log in once, then call the API — the token is captured and reused automatically
          certapi send https://api.example.com/login -X POST -d "{\"user\":\"me\",\"pass\":\"…\"}"
          certapi send https://api.example.com/orders          # sends Authorization: Bearer …

          # Headers, query strings, and a file body
          certapi send "https://api.example.com/search?q=abc" -H "Accept: application/json"
          certapi send https://api.example.com/upload -X PUT --data-file .\payload.json

          # Environments and capture rules
          certapi send https://{{host}}/login --env Staging --capture session=data.session_id

          # Save a binary body, keep stderr clean, fail the build on HTTP errors
          certapi send https://api.example.com/report.pdf -o report.pdf -q --fail

          # Troubleshoot a failing endpoint with full diagnostics in a file
          certapi send https://api.example.com/broken --debug --log-file broken.log

        The body goes to stdout; everything else goes to stderr. Exit 0 on a delivered
        response (any status unless --fail), 1 on transport errors, 2/3 on usage/data errors.
        """;
```

3c. `RunCommand.Help` becomes:

```csharp
    public const string Help = """
        Usage: certapi run <Collection[/Folder][/Request]> [options]
               certapi run --all [options]

        Runs saved requests. A folder or collection path runs everything beneath it as a
        suite; a request path runs that one request. Pass = a 2xx response.

        Options:
          --all                   Run every saved request in the workspace
          --workspace <file>      Load collections from a workspace file (default: live GUI state)
          --env <name>            Environment for {{variables}}; --var k=v overrides (repeatable)
          --record / --no-record  Write known-good results back (default: on for live state,
                                  off for workspace files; skipped while the GUI is running)
          --strict-vars           Unresolved {{tokens}} fail the request
          --no-auto-token         Don't capture or attach session tokens during this run
          --json                  JSON results instead of the table

        Requests whose Auth is "Auto" attach the captured token for their host; a token
        captured by one request (e.g. a login) is reused by the rest of the suite.

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          # Run one request, a folder, or everything
          certapi run "petstore/Get pet by id"
          certapi run petstore/smoke
          certapi run --all

          # A login-first suite: the login response's token carries through the suite
          certapi run "api/login then browse" --env Staging

          # CI: machine-readable results, no writes, fail the job on any failure
          certapi run --all --workspace .\suite.json --no-record --json

          # Investigate a flaky suite with full diagnostics
          certapi run api --debug --log-file suite-debug.log

        Exit codes: 0 all passed · 1 any failure · 2 usage · 3 data error.
        """;
```

3d-3h. Apply the same pattern to the remaining five commands — keep every existing option line, then append a `Global: --debug … --log-file …` line and an `Examples:` section. Exact text to append to each:

`CertsCommand.Help` — append:

```
        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi certs
          certapi certs --store LocalMachine
          certapi certs --json
```
(keep only example lines whose flags the command really has — check its option list above while editing.)

`SelfTestCommand.Help` — append:

```
        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi selftest
          certapi selftest --debug
```

`ImportCommand.Help` — append:

```
        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi import --curl "curl -X POST https://api.example.com/login -d '{}'"
          certapi import --openapi .\petstore.json
          certapi import --openapi .\petstore.json --workspace .\suite.json
```

`ExportCommand.Help` — append:

```
        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi export --openapi -o api.json
          certapi export --workspace -o workspace.json
```

`ServeCommand.Help` — append:

```
        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi serve https://internal-api.example.com --cert "CN=My Client"
          certapi serve https://internal-api.example.com --cert "CN=My Client" --port 8443 --insecure
```

`McpCommand.Help` — add to the options list:

```
          --no-auto-token         Don't capture/reuse bearer tokens across the session's calls
```

and append:

```
        Tokens returned by one tool call (e.g. a login via send_request) are captured in
        memory for this session and attached to later calls to the same host.

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi mcp --cert "CN=Agent Client" --allow api.example.com
          certapi mcp --cert 4A8823… --allow api.example.com --allow auth.example.com --insecure
          certapi mcp --cert "CN=Agent Client" --allow api.example.com --workspace .\suite.json
```

While editing each file, verify the example flags against that command's real options (e.g. `certs --json`, `serve --port`) and drop any example line whose flag doesn't exist.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~HelpTextTests"`
Expected: all PASS.

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj`
Expected: PASS (some CLI tests assert usage text substrings — update only if one asserts a line you reworded).

- [ ] **Step 5: Commit**

```bash
git add src/ApiTester.Cli/CliApp.cs src/ApiTester.Cli/Commands tests/ApiTester.Tests/Cli/HelpTextTests.cs
git commit -m "Write example-rich help for every certapi command"
```

---

### Task 10: Documentation, GUI help, changelog, version bump

**Files:**
- Modify: `src/ApiTester.App/HelpWindow.xaml.cs` (sections list, line 23-35; new section method)
- Modify: `README.md`, `docs/index.html`, `CHANGELOG.md`
- Modify: `src/ApiTester.Cli/ApiTester.Cli.csproj` and `src/ApiTester.App/ApiTester.App.csproj` (1.25.0 → 1.26.0)

- [ ] **Step 1: Add the "Automatic tokens" section to the GUI help**

In `src/ApiTester.App/HelpWindow.xaml.cs`, insert into `_sections` after `("Environments & variables", Environments),`:

```csharp
            ("Automatic tokens", AutoTokens),
```

Add the builder method (next to `Environments()`), following the house style (`Section`/`P`/`Sub`/`Bullets`/`NoteBox`):

```csharp
    private UIElement AutoTokens() => Section("Automatic tokens",
        P("Call a login endpoint and the app spots the bearer token in the response — access_token, " +
          "id_token, token, accessToken, or jwt in the JSON body (top level or under data/result), " +
          "or an X-Auth-Token / X-Access-Token header. No setup needed."),
        Sub("SCOPED TO THE WEBSITE"),
        P("A captured token belongs to the exact website it came from (scheme, host, and port). " +
          "Requests to any other website never receive it."),
        Sub("USING IT"),
        P("Requests whose Auth type is “Auto (captured token)” — the default — attach the token " +
          "automatically. A chip in the status bar shows the active website's token and its expiry; " +
          "click it to inspect, clear, or turn automatic tokens off. Pick “None (never send auth)” " +
          "on a request to opt out."),
        Sub("EVERYWHERE"),
        P("The same capture-and-reuse works headless: certapi send and certapi run print a note " +
          "when they capture or use a token (--no-auto-token disables it), and the MCP server " +
          "keeps a per-session token store so agent login flows just work."),
        NoteBox("Explicit auth always wins: a Bearer/Basic setting or a manual Authorization " +
                "header is never overridden, and expired tokens are never sent."));
```

- [ ] **Step 2: Mention collection defaults in the Collections help section**

In the `Collections()` builder (find `private UIElement Collections()`), append one paragraph before the section's closing:

```csharp
        P("Right-click a collection or folder and choose “Set website & certificate…” to give it " +
          "defaults: endpoints opened from it inherit that website and certificate when they don't " +
          "carry their own. The first successful send from a collection remembers the pair " +
          "automatically, so clicking through an imported API just works."),
```

(Adjust placement to match the method's actual `Section(...)` argument list — add it as another `P(...)` argument.)

- [ ] **Step 3: Update CHANGELOG and versions**

3a. Add at the top of `CHANGELOG.md` (below the intro):

```markdown
## [1.26.0] - 2026-07-17

### Added
- **Automatic bearer tokens** — a token returned by any response (`access_token`, `id_token`,
  `token`, `accessToken`, `jwt`, or an `X-Auth-Token`/`X-Access-Token` header) is captured with
  zero setup and scoped to the website it came from. Requests with the new **Auto** auth mode
  (the default) attach it automatically; explicit auth is never overridden, tokens never cross
  hosts, and expired tokens are never sent. Works in the app (with a status-bar token chip to
  inspect, clear, or disable), in `certapi send`/`run` (`--no-auto-token` to opt out), and in
  the MCP server (per-session store, so agent login flows chain naturally).
- **Collection defaults** — a collection or folder can hold a default website and client
  certificate ("Set website & certificate…" on right-click, or auto-remembered from the first
  successful send). Endpoints opened from a collection fill their blanks from the nearest
  folder default or the active tab — no more re-picking the website and cert for every endpoint.
- **`--debug` and `--log-file <path>`** on every certapi command: resolved URLs, sent headers
  (Authorization masked), certificate lookup, TLS details, timings, and full stack traces on
  stderr and/or appended to a log file.
- **Examples in every help screen** — `certapi help <command>` now shows realistic, copy-paste
  command examples, including login-then-call token flows and CI patterns.

### Changed
- Requests saved with auth **None** by earlier versions are treated as **Auto** (that value
  used to mean "nothing configured"); the new explicit **None (never send auth)** is preserved.
  State files are stamped with a schema version so the migration runs exactly once.
```

3b. In both `src/ApiTester.Cli/ApiTester.Cli.csproj` and `src/ApiTester.App/ApiTester.App.csproj`, change:

```xml
    <Version>1.25.0</Version>
```
to
```xml
    <Version>1.26.0</Version>
```

- [ ] **Step 4: Update README and the docs page**

4a. `README.md`: in the features list, extend the existing "Capture & reuse auth tokens" bullet (or add beside it):

```markdown
- **Automatic bearer tokens** — login once and follow-on requests to the same host carry the
  captured token automatically, in the GUI, `certapi send`/`run`, and the MCP server. Host-scoped,
  never overriding explicit auth; `--no-auto-token` / a status-bar toggle opt out.
- **Collection defaults** — collections remember their website + client certificate, so opening
  any endpoint is immediately sendable.
- **`--debug` / `--log-file`** — every certapi command can explain exactly what it sent, looked
  up, and negotiated, on screen or into a log file.
```

4b. `docs/index.html`: in the features grid, update the "Capture &amp; reuse auth tokens" card's text to mention automatic detection, and add one new card following the existing card markup pattern (copy an adjacent card's exact HTML structure):

- Title: `Automatic tokens`
- Body: `Log in once — the token in the response is detected, scoped to that host, and attached to follow-on requests automatically. GUI, CLI, and MCP server alike.`

And in the CLI section of the page, add `--debug` / `--log-file` to the option documentation with one example line each.

- [ ] **Step 5: Full verification**

Run: `dotnet build ApiTester.sln && dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj`
Expected: clean build, all tests PASS.

Run: `dotnet run --project src/ApiTester.Cli -- help send` (spot-check the examples render with sane alignment), and `dotnet run --project src/ApiTester.Cli -- --version` → `certapi 1.26.0`.

- [ ] **Step 6: Commit**

```bash
git add src/ApiTester.App/HelpWindow.xaml.cs README.md docs/index.html CHANGELOG.md src/ApiTester.Cli/ApiTester.Cli.csproj src/ApiTester.App/ApiTester.App.csproj
git commit -m "Document automatic tokens and bump to 1.26.0"
```

---

## Plan Self-Review (completed)

- **Spec coverage:** Token engine (Tasks 1-2), GUI/CLI/MCP surfaces (Tasks 4-7), collection stickiness (Task 8), CLI examples + debug/log (Tasks 3, 9), docs/error handling/testing woven through. The spec's "GUI-running guard" is exercised in Task 4's test; expiry messaging in Tasks 2/4; masking in Tasks 1/4/7.
- **Type consistency:** `TokenService.Capture/TokenFor/ExpiredTokenFor/AutoAttach/HostOf/Mask/MaskAuthorization`, `AppState.SessionTokens/AutoTokens/SchemaVersion/Migrate`, `CliServices.Log`, `CliLog.Create/Debug/Note/Describe/WrapStderr`, `GlobalOptions.Extract`, `CollectionDefaults.For/RootOf`, `BuildEnvelope(r, includeBody, notes)` — names match across all tasks.
- **Known judgment calls for the implementer:** exact insertion lines may drift a few lines as earlier tasks land; anchor on the quoted code, not the line numbers.
