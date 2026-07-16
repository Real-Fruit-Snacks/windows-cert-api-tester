using System.Linq;
using ApiTester.Core;

namespace ApiTester.Tests;

public class OpenApiImporterTests
{
    private const string OpenApi3 = """
    {
      "openapi": "3.0.0",
      "info": { "title": "Widget API" },
      "servers": [ { "url": "https://api.widget.example/v1" } ],
      "paths": {
        "/widgets": {
          "get":  { "tags": ["widgets"], "summary": "List widgets" },
          "post": { "tags": ["widgets"], "operationId": "createWidget" }
        },
        "/health": {
          "get": { "summary": "Health check" }
        }
      }
    }
    """;

    private const string Swagger2 = """
    {
      "swagger": "2.0",
      "info": { "title": "Legacy API" },
      "host": "legacy.example",
      "basePath": "/api",
      "schemes": ["https"],
      "paths": { "/ping": { "get": { "summary": "Ping" } } }
    }
    """;

    [Fact]
    public void OpenApi3_title_and_server_base_url()
    {
        var c = OpenApiImporter.Parse(OpenApi3);
        Assert.Equal("Widget API", c.Name);
        Assert.Equal("https://api.widget.example/v1", c.BaseUrl);
    }

    [Fact]
    public void OpenApi3_groups_operations_by_tag()
    {
        var c = OpenApiImporter.Parse(OpenApi3);
        var widgets = c.Folders.Single(f => f.Name == "widgets");
        Assert.Equal(2, widgets.Requests.Count);
        Assert.Contains(widgets.Requests, r => r.Method == "GET" && r.Name == "List widgets");
        Assert.Contains(widgets.Requests, r => r.Method == "POST" && r.Name == "createWidget");
    }

    [Fact]
    public void Untagged_operation_lands_in_default_folder()
    {
        var c = OpenApiImporter.Parse(OpenApi3);
        var def = c.Folders.Single(f => f.Name == "default");
        var health = Assert.Single(def.Requests);
        Assert.Equal("/health", health.Url);
        Assert.Equal("https://api.widget.example/v1", health.BaseUrl);
    }

    [Fact]
    public void Swagger2_base_url_from_host_basepath_schemes()
    {
        var c = OpenApiImporter.Parse(Swagger2);
        Assert.Equal("Legacy API", c.Name);
        Assert.Equal("https://legacy.example/api", c.BaseUrl);
        Assert.Equal("/ping", c.Folders.Single().Requests.Single().Url);
    }
}
