using System.IO;
using System.Text.Json;
using ApiTester.App;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Protects the workspace file format: an exported AppState must survive a JSON
/// round-trip with tabs, collections (including last results), environments, and history intact.</summary>
public class WorkspaceStateTests
{
    [Fact]
    public void Workspace_round_trips_through_json()
    {
        var state = new AppState
        {
            SavedBaseUrls = { "https://api.example" },
            Tabs =
            {
                new RequestModel
                {
                    Method = "POST",
                    BaseUrl = "https://api.example",
                    Path = "/users",
                    Body = "{\"a\":1}",
                    AuthType = "Bearer",
                    AuthSecret = "tok",
                    SourceCollectionId = "n1",
                    Headers = { new HeaderRow { Name = "X-T", Value = "1" } },
                    QueryParams = { new ParamRow { Key = "q", Value = "2" } }
                }
            },
            ActiveTabIndex = 0,
            Collections =
            {
                new CollectionNode
                {
                    Id = "f1", IsFolder = true, Name = "users",
                    Children =
                    {
                        new CollectionNode
                        {
                            Id = "n1", IsFolder = false, Name = "Create user",
                            Request = new RequestModel { Method = "POST", Path = "/users" }
                        }
                    }
                }
            },
            Environments =
            {
                new ApiEnvironment { Id = "e1", Name = "Dev", Variables = { new Variable { Key = "host", Value = "dev.local" } } }
            },
            ActiveEnvironmentId = "e1",
            History = { new HistoryEntry { Method = "GET", Url = "https://h/x", StatusCode = 200 } }
        };
        state.Collections[0].Children[0].RecordResult(201, new DateTime(2026, 7, 16, 9, 0, 0, DateTimeKind.Utc));

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        var back = JsonSerializer.Deserialize<AppState>(json)!;

        var tab = Assert.Single(back.Tabs);
        Assert.Equal("POST", tab.Method);
        Assert.Equal("/users", tab.Path);
        Assert.Equal("tok", tab.AuthSecret);
        Assert.Equal("n1", tab.SourceCollectionId);
        Assert.Equal("X-T", Assert.Single(tab.Headers).Name);
        Assert.Equal("q", Assert.Single(tab.QueryParams).Key);

        var folder = Assert.Single(back.Collections);
        var leaf = Assert.Single(folder.Children);
        Assert.Equal(201, leaf.LastStatusCode);
        Assert.True(leaf.IsKnownGood);

        var env = Assert.Single(back.Environments);
        Assert.Equal("Dev", env.Name);
        Assert.Equal("dev.local", Assert.Single(env.Variables).Value);
        Assert.Equal("e1", back.ActiveEnvironmentId);

        var h = Assert.Single(back.History);
        Assert.Equal(200, h.StatusCode);
        Assert.Equal("https://api.example", Assert.Single(back.SavedBaseUrls));
    }

    [Fact]
    public void SaveTo_and_LoadFrom_preserve_every_persisted_field()
    {
        // Guards against silent data loss: if a newer field stops being serialized (a stray
        // [JsonIgnore], a rename, a getter-only property), the user loses their tokens / tests /
        // multipart config on restart — and this test must fail rather than let that ship.
        var path = Path.Combine(Path.GetTempPath(), $"certapi-fields-{Guid.NewGuid():N}.json");
        try
        {
            var state = new AppState
            {
                Theme = "Light",
                AutoTokens = false,
                AutoCookies = false,
                IgnoreServerCertErrors = true,
                TimeoutSeconds = 45,
                SessionTokens =
                {
                    new SessionToken
                    {
                        Origin = "https://api.example.com:443",
                        Token = "tok-123",
                        Source = "oauth",
                        CapturedUtc = new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc),
                        ExpiresUtc = new DateTime(2026, 7, 18, 13, 0, 0, DateTimeKind.Utc)
                    }
                },
                SessionCookies =
                {
                    new SessionCookie
                    {
                        Origin = "https://intranet.corp:443",
                        Name = "SESSIONID",
                        Value = "abc123",
                        Path = "/",
                        Domain = "intranet.corp",
                        Secure = true,
                        HttpOnly = true,
                        ExpiresUtc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    }
                },
                Tabs =
                {
                    new RequestModel
                    {
                        Method = "POST",
                        Path = "/upload",
                        IsMultipart = true,
                        FormParts = { new FormPart { Enabled = true, Name = "file", IsFile = true, Value = @"C:\x.bin" } },
                        Captures = { new CaptureRule { Enabled = true, Variable = "id", Source = CaptureSource.Body, Path = "data.id" } },
                        Assertions = { new AssertionRule { Enabled = true, Target = AssertTarget.Status, Op = AssertOp.Equals, Value = "201" } }
                    }
                }
            };
            state.SaveTo(path);

            var back = AppState.LoadFrom(path);

            Assert.Equal("Light", back.Theme);
            Assert.False(back.AutoTokens);
            Assert.True(back.IgnoreServerCertErrors);
            Assert.Equal(45, back.TimeoutSeconds);

            var token = Assert.Single(back.SessionTokens);
            Assert.Equal("https://api.example.com:443", token.Origin);
            Assert.Equal("tok-123", token.Token);
            Assert.Equal("oauth", token.Source);
            Assert.Equal(new DateTime(2026, 7, 18, 13, 0, 0, DateTimeKind.Utc), token.ExpiresUtc!.Value.ToUniversalTime());

            Assert.False(back.AutoCookies);
            var cookie = Assert.Single(back.SessionCookies);
            Assert.Equal("https://intranet.corp:443", cookie.Origin);
            Assert.Equal("SESSIONID", cookie.Name);
            Assert.Equal("abc123", cookie.Value);
            Assert.True(cookie.Secure);
            Assert.True(cookie.HttpOnly);

            var tab = Assert.Single(back.Tabs);
            Assert.True(tab.IsMultipart);
            var part = Assert.Single(tab.FormParts);
            Assert.Equal("file", part.Name);
            Assert.True(part.IsFile);
            Assert.Equal(@"C:\x.bin", part.Value);

            var capture = Assert.Single(tab.Captures);
            Assert.Equal("id", capture.Variable);
            Assert.Equal(CaptureSource.Body, capture.Source);
            Assert.Equal("data.id", capture.Path);

            var assertion = Assert.Single(tab.Assertions);
            Assert.Equal(AssertTarget.Status, assertion.Target);
            Assert.Equal(AssertOp.Equals, assertion.Op);
            Assert.Equal("201", assertion.Value);
            Assert.True(assertion.Enabled);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveTo_and_LoadFrom_round_trip_an_explicit_path()
    {
        var path = Path.Combine(Path.GetTempPath(), $"certapi-test-{Guid.NewGuid():N}.json");
        try
        {
            var state = new AppState();
            state.Collections.Add(new CollectionNode { Name = "X", IsFolder = true });
            state.SaveTo(path);

            var back = AppState.LoadFrom(path);
            Assert.Equal("X", Assert.Single(back.Collections).Name);
            Assert.False(File.Exists(path + ".tmp"));   // temp file was moved, not left behind
        }
        finally { File.Delete(path); }
    }
}
