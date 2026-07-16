using ApiTester.App;
using ApiTester.Core;

namespace ApiTester.Tests;

public class RequestModelMappingTests
{
    [Fact]
    public void FromParsed_curl_style_full_url_splits_query_and_maps_bearer()
    {
        var p = new ParsedRequest
        {
            Method = "POST",
            BaseUrl = null,
            Url = "https://h/api?a=1&b=2",
            Headers = { new("X-Key", "abc") },
            Body = "{\"x\":1}",
            ContentType = "application/json",
            BearerToken = "tok",
            InsecureSkipVerify = true
        };

        var m = RequestModel.FromParsed(p);

        Assert.Equal("POST", m.Method);
        Assert.Equal("", m.BaseUrl);
        Assert.Equal("https://h/api", m.Path);
        Assert.Equal(2, m.QueryParams.Count);
        Assert.Equal("a", m.QueryParams[0].Key);
        Assert.Equal("1", m.QueryParams[0].Value);
        Assert.Equal("b", m.QueryParams[1].Key);
        Assert.Equal("{\"x\":1}", m.Body);
        Assert.Equal("application/json", m.ContentType);
        var header = Assert.Single(m.Headers);
        Assert.Equal("X-Key", header.Name);
        Assert.Equal("Bearer", m.AuthType);
        Assert.Equal("tok", m.AuthSecret);
        Assert.True(m.IgnoreServerCert);
        Assert.Equal("https://h/api?a=1&b=2", m.EffectiveUrl());
    }

    [Fact]
    public void FromParsed_openapi_style_keeps_base_and_path_separate()
    {
        var p = new ParsedRequest { Method = "GET", BaseUrl = "https://api.example/v1", Url = "/widgets/{id}", Name = "Get widget" };

        var m = RequestModel.FromParsed(p);

        Assert.Equal("https://api.example/v1", m.BaseUrl);
        Assert.Equal("/widgets/{id}", m.Path);
        Assert.Empty(m.QueryParams);
        Assert.Equal("None", m.AuthType);
        Assert.Equal("https://api.example/v1/widgets/{id}", m.EffectiveUrl());
    }

    [Fact]
    public void FromParsed_maps_basic_credentials()
    {
        var p = new ParsedRequest { Method = "GET", Url = "https://h", BasicUser = "alice", BasicPassword = "secret" };

        var m = RequestModel.FromParsed(p);

        Assert.Equal("Basic", m.AuthType);
        Assert.Equal("alice", m.AuthUser);
        Assert.Equal("secret", m.AuthSecret);
    }

    [Fact]
    public void History_entry_round_trip_preserves_all_fields()
    {
        var original = new RequestModel
        {
            Method = "PUT",
            BaseUrl = "https://h",
            Path = "/api/thing",
            Body = "payload",
            ContentType = "application/xml",
            AuthType = "Bearer",
            AuthUser = "u",
            AuthSecret = "tk",
            CertThumbprint = "ABCD1234",
            IgnoreServerCert = true,
            TimeoutSeconds = 42
        };
        original.Headers.Add(new HeaderRow { Enabled = false, Name = "Accept", Value = "*/*" });
        original.QueryParams.Add(new ParamRow { Enabled = true, Key = "q", Value = "v" });

        var restored = RequestModel.FromHistoryEntry(original.ToHistoryEntry(200, null));

        Assert.Equal("PUT", restored.Method);
        Assert.Equal("https://h", restored.BaseUrl);
        Assert.Equal("/api/thing", restored.Path);
        Assert.Equal("payload", restored.Body);
        Assert.Equal("application/xml", restored.ContentType);
        Assert.Equal("Bearer", restored.AuthType);
        Assert.Equal("u", restored.AuthUser);
        Assert.Equal("tk", restored.AuthSecret);
        Assert.Equal("ABCD1234", restored.CertThumbprint);
        Assert.True(restored.IgnoreServerCert);
        Assert.Equal(42, restored.TimeoutSeconds);

        var header = Assert.Single(restored.Headers);
        Assert.False(header.Enabled);
        Assert.Equal("Accept", header.Name);
        var param = Assert.Single(restored.QueryParams);
        Assert.Equal("q", param.Key);
        Assert.Equal("https://h/api/thing?q=v", restored.EffectiveUrl());
    }

    [Fact]
    public void LoadFrom_replaces_fields_in_place_keeping_the_same_collections()
    {
        var m = new RequestModel { Method = "GET", Path = "/old" };
        m.Headers.Add(new HeaderRow { Name = "Old", Value = "1" });
        var headersInstance = m.Headers;
        var paramsInstance = m.QueryParams;

        var entry = new HistoryEntry
        {
            Method = "DELETE",
            BaseUrl = "https://x",
            Url = "/new",
            Headers = { new HeaderRow { Name = "New", Value = "2" } },
            Params = { new ParamRow { Key = "p", Value = "q" } },
            AuthType = "Basic",
            AuthUser = "bob",
            AuthSecret = "pw"
        };

        m.LoadFrom(entry);

        Assert.Equal("DELETE", m.Method);
        Assert.Equal("/new", m.Path);
        Assert.Same(headersInstance, m.Headers);   // same instance → data bindings survive
        Assert.Same(paramsInstance, m.QueryParams);
        var header = Assert.Single(m.Headers);
        Assert.Equal("New", header.Name);
        var param = Assert.Single(m.QueryParams);
        Assert.Equal("p", param.Key);
        Assert.Equal("Basic", m.AuthType);
        Assert.Equal("bob", m.AuthUser);
    }
}

public class CollectionNodeTests
{
    [Fact]
    public void FromParsed_builds_a_folder_tree_with_request_leaves()
    {
        var pc = new ParsedCollection
        {
            Name = "My API",
            BaseUrl = "https://api",
            Folders =
            {
                new ParsedCollection
                {
                    Name = "users",
                    BaseUrl = "https://api",
                    Requests = { new ParsedRequest { Method = "GET", BaseUrl = "https://api", Url = "/users", Name = "List users" } }
                }
            },
            Requests = { new ParsedRequest { Method = "GET", BaseUrl = "https://api", Url = "/health", Name = "Health" } }
        };

        var node = CollectionNode.FromParsed(pc);

        Assert.True(node.IsFolder);
        Assert.Equal("My API", node.Name);
        Assert.Equal(2, node.Children.Count);

        var folder = node.Children.Single(c => c.IsFolder);
        Assert.Equal("users", folder.Name);
        var listUsers = Assert.Single(folder.Children);
        Assert.False(listUsers.IsFolder);
        Assert.Equal("List users", listUsers.Name);
        Assert.NotNull(listUsers.Request);
        Assert.Equal("GET", listUsers.Request!.Method);
        Assert.Equal("https://api/users", listUsers.Request.EffectiveUrl());

        var health = node.Children.Single(c => !c.IsFolder);
        Assert.Equal("Health", health.Name);
        Assert.Equal("https://api/health", health.Request!.EffectiveUrl());
    }

    [Fact]
    public void FromParsed_falls_back_to_method_and_url_when_a_request_has_no_name()
    {
        var pc = new ParsedCollection
        {
            Name = "API",
            Requests = { new ParsedRequest { Method = "POST", Url = "/things", Name = null } }
        };

        var node = CollectionNode.FromParsed(pc);

        var leaf = Assert.Single(node.Children);
        Assert.Equal("POST /things", leaf.Name);
    }

    [Fact]
    public void RecordResult_marks_2xx_as_known_good()
    {
        var node = new CollectionNode { Name = "Health", IsFolder = false, Request = new RequestModel() };
        Assert.False(node.HasResult);
        Assert.Equal("Never sent", node.StatusSummary);

        node.RecordResult(200, new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc));

        Assert.True(node.HasResult);
        Assert.True(node.IsKnownGood);
        Assert.Contains("200", node.StatusSummary);
        Assert.Contains("known good", node.StatusSummary);
    }

    [Fact]
    public void RecordResult_marks_failures_and_non_2xx_as_not_known_good()
    {
        var node = new CollectionNode { IsFolder = false, Request = new RequestModel() };

        node.RecordResult(404, DateTime.UtcNow);
        Assert.True(node.HasResult);
        Assert.False(node.IsKnownGood);
        Assert.Contains("404", node.StatusSummary);

        node.RecordResult(null, DateTime.UtcNow);   // failed without a response
        Assert.True(node.HasResult);
        Assert.False(node.IsKnownGood);
        Assert.Contains("failed", node.StatusSummary);
    }

    [Fact]
    public void Folders_have_no_status_summary()
    {
        Assert.Null(new CollectionNode { IsFolder = true, Name = "F" }.StatusSummary);
    }

    [Fact]
    public void Last_result_survives_a_json_round_trip()
    {
        var node = new CollectionNode { Name = "Health", IsFolder = false, Request = new RequestModel() };
        var when = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
        node.RecordResult(503, when);

        var json = System.Text.Json.JsonSerializer.Serialize(node);
        var back = System.Text.Json.JsonSerializer.Deserialize<CollectionNode>(json)!;

        Assert.Equal(503, back.LastStatusCode);
        Assert.Equal(when, back.LastCheckedUtc);
        Assert.True(back.HasResult);
        Assert.False(back.IsKnownGood);
    }

    [Fact]
    public void Source_collection_link_survives_a_json_round_trip()
    {
        var m = new RequestModel { Method = "GET", Path = "/health", SourceCollectionId = "abc123" };

        var json = System.Text.Json.JsonSerializer.Serialize(m);
        var back = System.Text.Json.JsonSerializer.Deserialize<RequestModel>(json)!;

        Assert.Equal("abc123", back.SourceCollectionId);
    }
}
