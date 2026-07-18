using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiTester.Cli.Mcp;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class McpCommand
{
    public const string Help = """
        Usage: certapi mcp [options]

        Runs a Model Context Protocol server on stdio so an AI agent can make mutual-TLS calls
        with a pinned Windows-store client certificate. Tools: send_request, list_certificates,
        list_saved, run_saved, self_test. Configure your MCP host to launch this command.

          --cert <thumb|subject>  Certificate all tools use (pinned; the agent can't change it)
          --store <location>      CurrentUser (default); LocalMachine searches both stores
          --allow <host>          Allowed upstream host (repeatable); a URL must match or be a
                                  subdomain of one. Omit to allow any host (prints a warning).
          --insecure              Ignore upstream server-certificate errors (internal CAs)
          --timeout <seconds>     Per-request upstream timeout (default 100)
          --workspace <file>      Load saved requests / environments from a workspace file

        Speaks JSON-RPC 2.0 over stdin/stdout; diagnostics go to stderr. Stop with Ctrl+C or by
        closing stdin. Exit 0 clean shutdown, 2 usage, 3 data error.
        """;

    public static int Run(Args args, TextReader input, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string? certQuery = args.Value("--cert");
        string store = args.Value("--store") ?? "CurrentUser";
        var allowHosts = args.Values("--allow");
        bool insecure = args.Flag("--insecure");
        string? timeoutRaw = args.Value("--timeout");
        string? workspace = args.Value("--workspace");
        bool noAutoToken = args.Flag("--no-auto-token");
        if (args.Positionals().Count > 0) throw new CliUsageException(Help);

        int timeout = 100;
        if (timeoutRaw is not null && (!int.TryParse(timeoutRaw, out timeout) || timeout <= 0))
            throw new CliUsageException($"--timeout expects a positive number of seconds, got '{timeoutRaw}'.");

        bool localMachine = store.Equals("LocalMachine", StringComparison.OrdinalIgnoreCase);
        if (!localMachine && !store.Equals("CurrentUser", StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException("--store must be CurrentUser or LocalMachine.");

        var cert = certQuery is null ? null
            : CertPicker.Resolve(services.ListCertificates(localMachine), certQuery, stderr).Certificate;

        var allow = new HostAllowlist(allowHosts);
        stderr.WriteLine($"certapi mcp ready · cert: {cert?.Subject ?? "none"} · " +
            (allowHosts.Count == 0 ? "allow: ANY HOST (no --allow given)" : "allow: " + string.Join(", ", allowHosts)));

        var server = new McpServer(BuildTools(cert, allow, insecure, timeout, localMachine, workspace, noAutoToken, services), Version());
        server.Run(input, stdout, stderr, services.Cancel);
        return ExitCodes.Ok;
    }

    private static string Version()
    {
        var v = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }

    internal static IReadOnlyList<ToolDef> BuildTools(
        X509Certificate2? cert, HostAllowlist allow, bool insecure, int timeout,
        bool includeLocalMachine, string? workspace, bool noAutoToken, CliServices services)
    {
        // Session-scoped token store: lives for this MCP process only, never written to disk.
        var tokenState = new AppState();

        ToolResult SendUrl(string method, string url, IEnumerable<KeyValuePair<string, string>> headers,
            string? body, string? contentType, bool allowAutoToken = true)
        {
            if (!allow.IsAllowed(url))
                return new ToolResult(JsonSerializer.Serialize(new { error = $"host for '{url}' is not allowed" }), true);

            var headerList = headers.ToList();
            var notes = new List<string>();
            if (!noAutoToken && allowAutoToken)
            {
                var used = TokenService.AutoAttach(tokenState, url, headerList, out var expired);
                if (used is not null) notes.Add($"using captured token for {TokenService.HostOf(url)}");
                else if (expired is not null) notes.Add($"captured token for {TokenService.HostOf(url)} has expired");
            }

            var request = new ApiRequest
            {
                Method = new HttpMethod(method.ToUpperInvariant()),
                Url = url,
                Headers = headerList,
                Body = body,
                ContentType = body is not null ? (contentType ?? "application/json") : null,
                Timeout = TimeSpan.FromSeconds(timeout)
            };
            var response = services.Client.SendAsync(request, cert, insecure, followRedirects: false, cancellationToken: services.Cancel)
                .GetAwaiter().GetResult();

            if (!noAutoToken && response.Error is null &&
                TokenService.Capture(tokenState, url, response.Body, response.ContentType, response.Headers) is { } captured)
                notes.Add($"captured bearer token for {TokenService.HostOf(url)} ({captured.Source})");

            return new ToolResult(SendCommand.BuildEnvelope(response, includeBody: true, notes), IsError: response.Error is not null);
        }

        // ---- send_request ----
        var sendRequest = new ToolDef("send_request",
            "Send an HTTP request to an allowed host with the pinned client certificate. Returns status, headers, and body.",
            JsonNode.Parse("""
                {"type":"object","required":["url"],"properties":{
                  "method":{"type":"string","description":"HTTP method (default GET)"},
                  "url":{"type":"string","description":"Absolute http(s):// URL on an allowed host"},
                  "headers":{"type":"object","additionalProperties":{"type":"string"}},
                  "body":{"type":"string"},
                  "contentType":{"type":"string"}}}
                """)!,
            a =>
            {
                string? url = Str(a, "url");
                if (string.IsNullOrWhiteSpace(url)) return Err("url is required");
                var headers = ObjPairs(a, "headers");
                return SendUrl(Str(a, "method") ?? "GET", url!, headers, Str(a, "body"), Str(a, "contentType"));
            });

        // ---- list_certificates ----
        var listCerts = new ToolDef("list_certificates",
            "List client certificates in the Windows store (subject, thumbprint, expiry). Read-only.",
            JsonNode.Parse("""{"type":"object","properties":{"filter":{"type":"string"}}}""")!,
            a =>
            {
                string? filter = Str(a, "filter");
                var certs = services.ListCertificates(includeLocalMachine)
                    .Where(c => filter is null ||
                                c.Subject.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                c.Issuer.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                c.Thumbprint.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .Select(c => new
                    {
                        subject = c.Subject, issuer = c.Issuer, thumbprint = c.Thumbprint,
                        notAfter = c.NotAfter, expired = c.IsExpired(), clientAuthEku = c.HasClientAuthEku
                    });
                return new ToolResult(JsonSerializer.Serialize(new { certificates = certs }), false);
            });

        // ---- list_saved ----
        var listSaved = new ToolDef("list_saved",
            "List saved requests from your collections as Collection/Folder/Request paths. Read-only.",
            JsonNode.Parse("""{"type":"object","properties":{}}""")!,
            _ =>
            {
                var state = CliWorkspace.Load(workspace, services.LiveStatePath);
                List<(string Path, ApiTester.Core.CollectionNode Node)> leaves;
                try { leaves = CliWorkspace.ResolveTargets(state, null, all: true); }
                catch (CliDataException) { leaves = new(); }   // empty collections
                var items = leaves.Select(l => new { path = l.Path, method = l.Node.Request!.Method, url = l.Node.Request!.EffectiveUrl() });
                return new ToolResult(JsonSerializer.Serialize(new { items }), false);
            });

        // ---- run_saved ----
        var runSaved = new ToolDef("run_saved",
            "Run a saved request by its Collection/Folder/Request path with {{variables}} resolved, using the pinned certificate.",
            JsonNode.Parse("""
                {"type":"object","required":["path"],"properties":{
                  "path":{"type":"string"},
                  "env":{"type":"string"},
                  "vars":{"type":"object","additionalProperties":{"type":"string"}}}}
                """)!,
            a =>
            {
                string? path = Str(a, "path");
                if (string.IsNullOrWhiteSpace(path)) return Err("path is required");
                var state = CliWorkspace.Load(workspace, services.LiveStatePath);
                List<(string Path, ApiTester.Core.CollectionNode Node)> leaves;
                try { leaves = CliWorkspace.ResolveTargets(state, path, all: false); }
                catch (CliDataException ex) { return Err(ex.Message); }
                if (leaves.Count != 1) return Err($"'{path}' resolves to {leaves.Count} requests; name a single request");

                var m = leaves[0].Node.Request!;
                var vars = CliWorkspace.BuildVars(state, Str(a, "env"), ObjKeys(a, "vars"));
                var unresolved = new List<string>();
                string R(string s)
                {
                    var (r, u) = VariableResolver.Resolve(s ?? "", vars);
                    foreach (var x in u) if (!unresolved.Contains(x)) unresolved.Add(x);
                    return r;
                }
                var headers = new List<KeyValuePair<string, string>>();
                foreach (var h in m.Headers)
                    if (h.Enabled && !string.IsNullOrWhiteSpace(h.Name)) headers.Add(new(R(h.Name.Trim()), R(h.Value ?? "")));
                switch (m.AuthType)
                {
                    case "Bearer" when !string.IsNullOrWhiteSpace(m.AuthSecret):
                        headers.Add(new("Authorization", "Bearer " + R(m.AuthSecret!.Trim()))); break;
                    case "Basic":
                        headers.Add(new("Authorization", "Basic " +
                            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{R(m.AuthUser ?? "")}:{R(m.AuthSecret ?? "")}")))); break;
                }
                string url = R(m.EffectiveUrl());
                string? body = string.IsNullOrEmpty(m.Body) ? null : R(m.Body!);
                if (unresolved.Count > 0)
                    return Err("unresolved variables: " + string.Join(", ", unresolved.Select(u => "{{" + u + "}}")));
                return SendUrl(m.Method, url, headers, body,
                    m.ContentType == "(none)" ? null : m.ContentType,
                    allowAutoToken: m.AuthType == "Auto");
            });

        // ---- self_test ----
        var selfTest = new ToolDef("self_test",
            "Prove the mutual-TLS path end to end against a built-in loopback server. Read-only.",
            JsonNode.Parse("""{"type":"object","properties":{}}""")!,
            _ =>
            {
                var result = new SelfTestRunner().RunAsync().GetAwaiter().GetResult();
                return new ToolResult(JsonSerializer.Serialize(new { passed = result.Passed, detail = result.Detail }), false);
            });

        return new[] { sendRequest, listCerts, listSaved, runSaved, selfTest };
    }

    private static ToolResult Err(string message) =>
        new(JsonSerializer.Serialize(new { error = message }), true);

    private static string? Str(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static List<KeyValuePair<string, string>> ObjPairs(JsonElement a, string name)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Object)
            foreach (var p in o.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) list.Add(new(p.Name, p.Value.GetString()!));
        return list;
    }

    private static List<string> ObjKeys(JsonElement a, string name)
    {
        var list = new List<string>();
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Object)
            foreach (var p in o.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) list.Add($"{p.Name}={p.Value.GetString()}");
        return list;
    }
}
