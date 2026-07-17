using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Tests;

public class JsonPathTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Reads_top_level_nested_and_indexed_paths()
    {
        var root = Root("""{"access_token":"abc","data":{"token":"xyz"},"items":[{"id":"one"},{"id":"two"}]}""");
        Assert.Equal("abc", JsonPath.Evaluate(root, "access_token")!.Value.GetString());
        Assert.Equal("xyz", JsonPath.Evaluate(root, "data.token")!.Value.GetString());
        Assert.Equal("two", JsonPath.Evaluate(root, "items[1].id")!.Value.GetString());
    }

    [Fact]
    public void Leading_index_and_root_array_work()
    {
        var root = Root("""[{"k":"v0"},{"k":"v1"}]""");
        Assert.Equal("v1", JsonPath.Evaluate(root, "[1].k")!.Value.GetString());
    }

    [Fact]
    public void Missing_or_out_of_range_or_wrong_kind_returns_null()
    {
        var root = Root("""{"a":{"b":"c"},"arr":[1]}""");
        Assert.Null(JsonPath.Evaluate(root, "a.missing"));
        Assert.Null(JsonPath.Evaluate(root, "arr[5]"));
        Assert.Null(JsonPath.Evaluate(root, "a[0]"));      // object indexed like an array
        Assert.Null(JsonPath.Evaluate(root, "a.b.c"));     // descend into a string
    }

    [Fact]
    public void Malformed_bracket_segments_return_null()
    {
        var root = Root("""{"count":5,"arr":[1,2]}""");
        Assert.Null(JsonPath.Evaluate(root, "count["));      // unclosed, nothing after
        Assert.Null(JsonPath.Evaluate(root, "arr[0"));       // unclosed
        Assert.Null(JsonPath.Evaluate(root, "arr[x]"));      // non-numeric
    }
}
