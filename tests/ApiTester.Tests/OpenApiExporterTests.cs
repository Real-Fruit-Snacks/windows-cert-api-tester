using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Tests;

public class OpenApiExporterTests
{
    private static JsonDocument Export(ParsedCollection pc) =>
        JsonDocument.Parse(OpenApiExporter.ToJson(pc));

    [Fact]
    public void Exports_server_tags_paths_and_operation_facts()
    {
        var pc = new ParsedCollection
        {
            Name = "My API",
            BaseUrl = "https://api.example/v1",
            Folders =
            {
                new ParsedCollection
                {
                    Name = "users",
                    Requests =
                    {
                        new ParsedRequest
                        {
                            Method = "POST",
                            BaseUrl = "https://api.example/v1",
                            Url = "/users?dry_run=true",
                            Name = "Create user",
                            Description = "Last checked 2026-07-16 — 201 (known good)",
                            Headers = { new("X-Trace", "1"), new("Content-Type", "application/json") },
                            Body = "{\"name\":\"a\"}",
                            ContentType = "application/json"
                        }
                    }
                }
            }
        };

        using var doc = Export(pc);
        var root = doc.RootElement;

        Assert.Equal("3.0.3", root.GetProperty("openapi").GetString());
        Assert.Equal("My API", root.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("https://api.example/v1", root.GetProperty("servers")[0].GetProperty("url").GetString());
        Assert.Equal("users", root.GetProperty("tags")[0].GetProperty("name").GetString());

        var op = root.GetProperty("paths").GetProperty("/users").GetProperty("post");
        Assert.Equal("Create user", op.GetProperty("summary").GetString());
        Assert.Contains("known good", op.GetProperty("description").GetString());
        Assert.Equal("users", op.GetProperty("tags")[0].GetString());

        var parameters = op.GetProperty("parameters").EnumerateArray().ToList();
        Assert.Contains(parameters, p => p.GetProperty("name").GetString() == "dry_run" &&
                                         p.GetProperty("in").GetString() == "query" &&
                                         p.GetProperty("example").GetString() == "true");
        Assert.Contains(parameters, p => p.GetProperty("name").GetString() == "X-Trace" &&
                                         p.GetProperty("in").GetString() == "header");
        // Content-Type is represented by the request body, not a header parameter.
        Assert.DoesNotContain(parameters, p => p.GetProperty("name").GetString() == "Content-Type");

        var body = op.GetProperty("requestBody").GetProperty("content").GetProperty("application/json");
        Assert.Equal("a", body.GetProperty("example").GetProperty("name").GetString());
    }

    [Fact]
    public void Exports_auth_as_schemes_without_secrets()
    {
        var pc = new ParsedCollection
        {
            Name = "Secured",
            Requests =
            {
                new ParsedRequest { Method = "GET", Url = "/a", BearerToken = "SECRET-TOKEN" },
                new ParsedRequest { Method = "GET", Url = "/b", BasicUser = "alice", BasicPassword = "hunter2" }
            }
        };

        string json = OpenApiExporter.ToJson(pc);
        Assert.DoesNotContain("SECRET-TOKEN", json);
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("alice", json);

        using var doc = JsonDocument.Parse(json);
        var schemes = doc.RootElement.GetProperty("components").GetProperty("securitySchemes");
        Assert.Equal("bearer", schemes.GetProperty("bearerAuth").GetProperty("scheme").GetString());
        Assert.Equal("basic", schemes.GetProperty("basicAuth").GetProperty("scheme").GetString());

        var a = doc.RootElement.GetProperty("paths").GetProperty("/a").GetProperty("get");
        Assert.True(a.GetProperty("security")[0].TryGetProperty("bearerAuth", out _));
    }

    [Fact]
    public void Absolute_url_under_the_server_is_reduced_to_a_path()
    {
        var pc = new ParsedCollection
        {
            Name = "API",
            BaseUrl = "https://h/v1",
            Requests = { new ParsedRequest { Method = "GET", Url = "https://h/v1/things?x=1" } }
        };

        using var doc = Export(pc);
        Assert.True(doc.RootElement.GetProperty("paths").TryGetProperty("/things", out _));
    }

    [Fact]
    public void Absolute_url_outside_the_server_gets_a_path_level_server_override()
    {
        var pc = new ParsedCollection
        {
            Name = "API",
            BaseUrl = "https://main",
            Requests = { new ParsedRequest { Method = "GET", Url = "https://other:8443/health" } }
        };

        using var doc = Export(pc);
        var pathItem = doc.RootElement.GetProperty("paths").GetProperty("/health");
        Assert.Equal("https://other:8443", pathItem.GetProperty("servers")[0].GetProperty("url").GetString());
    }

    [Fact]
    public void Relative_paths_gain_a_leading_slash_and_duplicates_keep_the_first_operation()
    {
        var pc = new ParsedCollection
        {
            Name = "API",
            Requests =
            {
                new ParsedRequest { Method = "GET", Url = "things", Name = "first" },
                new ParsedRequest { Method = "GET", Url = "/things", Name = "second" }
            }
        };

        using var doc = Export(pc);
        var op = doc.RootElement.GetProperty("paths").GetProperty("/things").GetProperty("get");
        Assert.Equal("first", op.GetProperty("summary").GetString());
    }

    [Fact]
    public void Non_json_bodies_are_exported_as_string_examples()
    {
        var pc = new ParsedCollection
        {
            Name = "API",
            Requests =
            {
                new ParsedRequest { Method = "POST", Url = "/raw", Body = "plain <text>", ContentType = "text/plain" }
            }
        };

        using var doc = Export(pc);
        var content = doc.RootElement.GetProperty("paths").GetProperty("/raw")
            .GetProperty("post").GetProperty("requestBody").GetProperty("content");
        Assert.Equal("plain <text>", content.GetProperty("text/plain").GetProperty("example").GetString());
    }

    [Fact]
    public void Exported_document_round_trips_through_the_importer()
    {
        var pc = new ParsedCollection
        {
            Name = "Round trip",
            BaseUrl = "https://api",
            Folders =
            {
                new ParsedCollection
                {
                    Name = "users",
                    Requests = { new ParsedRequest { Method = "GET", Url = "/users", Name = "List users",
                                                     Description = "verified" } }
                }
            },
            Requests = { new ParsedRequest { Method = "GET", Url = "/health", Name = "Health" } }
        };

        var back = OpenApiImporter.Parse(OpenApiExporter.ToJson(pc));

        Assert.Equal("Round trip", back.Name);
        Assert.Equal("https://api", back.BaseUrl);

        var all = Flatten(back).ToList();
        Assert.Contains(all, r => r.Method == "GET" && r.Url == "/health" && r.Name == "Health");
        Assert.Contains(all, r => r.Method == "GET" && r.Url == "/users" && r.Name == "List users" &&
                                  r.Description == "verified");
        var usersFolder = back.Folders.Single(f => f.Name == "users");
        Assert.Single(usersFolder.Requests);
    }

    private static IEnumerable<ParsedRequest> Flatten(ParsedCollection c) =>
        c.Requests.Concat(c.Folders.SelectMany(Flatten));
}
