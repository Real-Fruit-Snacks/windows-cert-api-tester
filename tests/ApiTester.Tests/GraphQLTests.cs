using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Tests;

public class GraphQLTests
{
    [Fact]
    public void Builds_a_query_only_body()
    {
        var body = GraphQL.BuildBody("{ me { id } }");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("{ me { id } }", doc.RootElement.GetProperty("query").GetString());
        Assert.False(doc.RootElement.TryGetProperty("variables", out _));
    }

    [Fact]
    public void Embeds_variables_object()
    {
        var body = GraphQL.BuildBody("query($id:ID!){ user(id:$id){ name } }", "{\"id\":42}");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(42, doc.RootElement.GetProperty("variables").GetProperty("id").GetInt32());
    }

    [Fact]
    public void Escapes_the_query_string()
    {
        var body = GraphQL.BuildBody("{ x(s:\"a\\\"b\") }");   // query contains quotes/backslashes
        using var doc = JsonDocument.Parse(body);              // must still be valid JSON
        Assert.Contains("x(s:", doc.RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public void Rejects_non_object_variables()
    {
        Assert.ThrowsAny<JsonException>(() => GraphQL.BuildBody("{ x }", "[1,2,3]"));
        Assert.ThrowsAny<JsonException>(() => GraphQL.BuildBody("{ x }", "not json"));
    }
}
