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
