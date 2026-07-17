using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApiTester.Cli.Mcp;

/// <summary>The JSON text a tool returns and whether it represents an error.</summary>
public sealed record ToolResult(string Json, bool IsError);

/// <summary>One MCP tool: its name, description, JSON-Schema for arguments, and handler.
/// The handler receives the call's <c>arguments</c> object (JsonElement; Undefined when absent).</summary>
public sealed record ToolDef(string Name, string Description, JsonNode InputSchema, Func<JsonElement, ToolResult> Handler);

/// <summary>A minimal Model Context Protocol server over stdio: JSON-RPC 2.0, one compact JSON
/// object per line. Handles initialize / tools/list / tools/call / ping / notifications.</summary>
public sealed class McpServer
{
    private readonly Dictionary<string, ToolDef> _tools;
    private readonly IReadOnlyList<ToolDef> _order;
    private readonly string _version;

    public McpServer(IReadOnlyList<ToolDef> tools, string version)
    {
        _order = tools;
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _version = version;
    }

    public void Run(TextReader input, TextWriter output, TextWriter log, CancellationToken ct)
    {
        string? line;
        while (!ct.IsCancellationRequested && (line = input.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = HandleLine(line);
            if (response is not null) { output.WriteLine(response); output.Flush(); }
        }
    }

    /// <summary>Process one request line; returns the response line, or null for a notification.</summary>
    public string? HandleLine(string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return Error(null, -32700, "Parse error"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("method", out var methodEl) ||
                methodEl.ValueKind != JsonValueKind.String)
                return Error(IdOf(root), -32600, "Invalid request");

            string method = methodEl.GetString()!;
            JsonElement? id = IdOf(root);
            bool isNotification = id is null;

            // Notifications never get a response.
            if (method.StartsWith("notifications/", StringComparison.Ordinal)) return null;

            switch (method)
            {
                case "initialize":
                    return Result(id, InitializeResult(root));
                case "tools/list":
                    return Result(id, ToolsListResult());
                case "ping":
                    return Result(id, new JsonObject());
                case "tools/call":
                    return Result(id, ToolsCallResult(root));
                default:
                    return isNotification ? null : Error(id, -32601, "Method not found");
            }
        }
    }

    private static JsonElement? IdOf(JsonElement root) =>
        root.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null ? idEl.Clone() : null;

    private JsonObject InitializeResult(JsonElement root)
    {
        string protocol = "2024-11-05";
        if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty("protocolVersion", out var pv) && pv.ValueKind == JsonValueKind.String)
            protocol = pv.GetString()!;
        return new JsonObject
        {
            ["protocolVersion"] = protocol,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "certapi", ["version"] = _version }
        };
    }

    private JsonObject ToolsListResult()
    {
        var arr = new JsonArray();
        foreach (var t in _order)
            arr.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = t.InputSchema.DeepClone()
            });
        return new JsonObject { ["tools"] = arr };
    }

    private JsonObject ToolsCallResult(JsonElement root)
    {
        string name = "";
        JsonElement args = default;
        if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            if (p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString()!;
            if (p.TryGetProperty("arguments", out var a)) args = a;
        }

        ToolResult tr;
        if (!_tools.TryGetValue(name, out var tool))
            tr = new ToolResult($"{{\"error\":\"unknown tool '{name}'\"}}", IsError: true);
        else
        {
            try { tr = tool.Handler(args); }
            catch (Exception ex) { tr = new ToolResult(JsonSerializer.Serialize(new { error = ex.Message }), IsError: true); }
        }

        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = tr.Json }),
            ["isError"] = tr.IsError
        };
    }

    private static string Result(JsonElement? id, JsonNode result)
    {
        var env = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = IdNode(id), ["result"] = result };
        return env.ToJsonString();
    }

    private static string Error(JsonElement? id, int code, string message)
    {
        var env = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = IdNode(id),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        };
        return env.ToJsonString();
    }

    private static JsonNode? IdNode(JsonElement? id) =>
        id is null ? null : JsonNode.Parse(id.Value.GetRawText());
}
