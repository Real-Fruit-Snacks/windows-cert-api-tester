using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiTester.Cli.Mcp;

namespace ApiTester.Tests.Cli;

public class McpServerTests
{
    private static McpServer Server()
    {
        var echo = new ToolDef(
            "echo", "Echoes its text argument.",
            JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string"}}}""")!,
            args => new ToolResult($"{{\"echoed\":\"{args.GetProperty("text").GetString()}\"}}", IsError: false));
        var boom = new ToolDef(
            "boom", "Always fails.",
            JsonNode.Parse("""{"type":"object"}""")!,
            _ => new ToolResult("{\"error\":\"nope\"}", IsError: true));
        return new McpServer(new[] { echo, boom }, "9.9.9");
    }

    private static JsonElement Result(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("result").Clone();
    }

    [Fact]
    public void Initialize_reports_capabilities_and_server_info()
    {
        var line = Server().HandleLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""");
        var r = Result(line!);
        Assert.Equal("2024-11-05", r.GetProperty("protocolVersion").GetString());
        Assert.True(r.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.Equal("certapi", r.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal("9.9.9", r.GetProperty("serverInfo").GetProperty("version").GetString());
    }

    [Fact]
    public void Tools_list_returns_the_injected_tools()
    {
        var line = Server().HandleLine("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var tools = Result(line!).GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("echo", tools);
        Assert.Contains("boom", tools);
    }

    [Fact]
    public void Tools_call_runs_the_handler_and_wraps_the_result()
    {
        var line = Server().HandleLine("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi"}}}""");
        var r = Result(line!);
        Assert.False(r.GetProperty("isError").GetBoolean());
        var text = r.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("\"echoed\":\"hi\"", text);
    }

    [Fact]
    public void Tools_call_marks_handler_errors_and_unknown_tools()
    {
        var boom = Result(Server().HandleLine("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"boom","arguments":{}}}""")!);
        Assert.True(boom.GetProperty("isError").GetBoolean());

        var unknown = Result(Server().HandleLine("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"ghost","arguments":{}}}""")!);
        Assert.True(unknown.GetProperty("isError").GetBoolean());
        Assert.Contains("ghost", unknown.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Notifications_get_no_response()
    {
        Assert.Null(Server().HandleLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
    }

    [Fact]
    public void Unknown_method_and_parse_errors_return_json_rpc_errors()
    {
        using var d1 = JsonDocument.Parse(Server().HandleLine("""{"jsonrpc":"2.0","id":6,"method":"no_such"}""")!);
        Assert.Equal(-32601, d1.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        using var d2 = JsonDocument.Parse(Server().HandleLine("{ this is not json ")!);
        Assert.Equal(-32700, d2.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void Run_processes_a_stream_until_eof()
    {
        var input = new StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{\"text\":\"x\"}}}\n");
        var output = new StringWriter();
        Server().Run(input, output, TextWriter.Null, default);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);   // ping + tools/call; the notification produced no line
    }
}
