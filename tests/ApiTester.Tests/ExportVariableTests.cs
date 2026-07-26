using System.IO;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Guards the export direction of the same query composition the v1.54.0 wire fix
/// addressed (see QueryVariableCliTests), where the correct answer is the opposite: on the wire a
/// surviving {{variable}} token is a value the server will receive, but in an export it is a
/// template a human or another tool fills in later, so it must survive percent-escaping verbatim.</summary>
public class ExportVariableTests
{
    private static CollectionNode SavedRequest(string value) => new()
    {
        Name = "saved",
        IsFolder = false,
        Request = new RequestModel
        {
            Method = "GET",
            BaseUrl = "https://api",
            Path = "/items",
            QueryParams = { new ParamRow { Key = "a", Value = value } }
        }
    };

    [Fact]
    public void A_saved_requests_variable_survives_export_as_a_literal_token()
    {
        var node = SavedRequest("{{tok}}");

        var url = node.ToParsed().Requests[0].Url;

        Assert.Contains("a={{tok}}", url);
        Assert.DoesNotContain("%7B%7B", url);
    }

    [Fact]
    public void An_ordinary_value_is_escaped_exactly_as_before()
    {
        var node = SavedRequest("a b&c=d é");

        var url = node.ToParsed().Requests[0].Url;
        var (_, query) = QueryString.Split(url);

        Assert.Equal("a=a%20b%26c%3Dd%20%C3%A9", query);
    }

    [Fact]
    public void A_variable_mixed_with_reserved_characters_keeps_the_token_and_escapes_the_rest()
    {
        var node = SavedRequest("{{tok}}&x=1");

        var url = node.ToParsed().Requests[0].Url;
        var (_, query) = QueryString.Split(url);

        Assert.Equal("a={{tok}}%26x%3D1", query);

        var pair = Assert.Single(QueryString.Parse(query));
        Assert.Equal("a", pair.Key);
        Assert.Equal("{{tok}}&x=1", pair.Value);
    }

    [Fact]
    public void An_exported_curl_command_carries_the_token_a_reader_can_fill_in()
    {
        var node = SavedRequest("{{tok}}");
        var model = node.Request!;

        var snippet = CodeGenerator.Curl(new CodeRequest(
            "GET", model.ExportUrl(), Array.Empty<KeyValuePair<string, string>>(), null));

        Assert.Contains("a={{tok}}", snippet);
        Assert.DoesNotContain("%7B%7B", snippet);
    }

    [Fact]
    public void A_resolvable_variable_is_substituted_before_the_export_escapes_it()
    {
        var node = SavedRequest("{{tok}}");
        var model = node.Request!;

        var url = model.ExportUrl(s => s.Replace("{{tok}}", "a b"));

        Assert.Contains("a=a%20b", url);
        Assert.DoesNotContain("{{", url);
        Assert.DoesNotContain("}}", url);
    }

    [Fact]
    public void Exporting_openapi_from_a_workspace_keeps_the_token_in_the_parameter_example()
    {
        var node = SavedRequest("{{tok}}");
        var ws = Path.Combine(Path.GetTempPath(), $"certapi-exportvar-{Guid.NewGuid():N}.json");
        var live = Path.Combine(Path.GetTempPath(), $"certapi-exportvar-live-{Guid.NewGuid():N}.json");
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-exportvar-out-{Guid.NewGuid():N}.json");
        try
        {
            var state = new AppState();
            state.Collections.Add(node);
            state.SaveTo(ws);

            int code = CliApp.Run(
                new[] { "export", "openapi", "-o", outFile, "--workspace", ws },
                new StringWriter(), new StringWriter(),
                services: new CliServices { LiveStatePath = live, IsGuiRunning = () => false });
            Assert.Equal(0, code);

            string json = File.ReadAllText(outFile);
            using var doc = JsonDocument.Parse(json);
            var parameters = doc.RootElement.GetProperty("paths").GetProperty("/items").GetProperty("get").GetProperty("parameters");
            var example = parameters[0].GetProperty("example").GetString();

            Assert.Equal("{{tok}}", example);
            Assert.DoesNotContain("%7B%7B", json);
        }
        finally { File.Delete(ws); File.Delete(live); File.Delete(outFile); }
    }
}
