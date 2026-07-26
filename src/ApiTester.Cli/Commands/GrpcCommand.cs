using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiTester.Core;
using ApiTester.Grpc;

namespace ApiTester.Cli.Commands;

/// <summary>The CLI surface over <see cref="GrpcCaller"/>: <c>certapi grpc list</c> discovers the
/// services and methods a server advertises via reflection, and <c>certapi grpc call</c> invokes
/// one of them (unary or server-streaming), using the same Windows-store certificate handling as
/// every other command. Either subcommand can take its services and methods from a compiled
/// descriptor set (--protoset) instead of a live server, for a server that does not implement
/// reflection at all. Every gRPC/Protobuf type stays inside ApiTester.Grpc — this class only
/// ever sees the plain records ApiTester.Grpc exposes.</summary>
public static class GrpcCommand
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public const string Help = """
        Usage: certapi grpc list <address> [options]
               certapi grpc list --protoset <file> [options]
               certapi grpc call <address> <Service/Method> [options]

        Calls a gRPC service (HTTP/2) that requires a client certificate, using the same Windows-
        store certificate handling as the rest of certapi. list shows the services and methods a
        server advertises via server reflection (or a compiled descriptor set — see --protoset
        below); call invokes one of them — unary, or server-streaming (messages print as they
        arrive).

        Descriptors come from server reflection (grpc.reflection.v1alpha.ServerReflection) by
        default. A server that does not implement reflection — often deliberately, in production —
        can still be listed and called by supplying a compiled descriptor set with --protoset.
        Client-streaming and bidirectional methods are out of scope for this version. certapi serve
        does not proxy gRPC — HttpListener is HTTP/1.1-only — so certapi grpc reaches the service
        directly with your certificate rather than going through the gateway.

        Request:
          -d, --data <json>       The request message as JSON (default {}, an empty message)
          --data-file <path>      Read the request JSON from a file instead of -d
          -H, --header "k: v"     Request metadata (repeatable)
          --max-messages <n>      Stop a server-streaming call after n messages
          --timeout <seconds>     Default 100

        Descriptors (for a server that does not implement reflection):
          --protoset <file>       Read the service definitions from a compiled descriptor set — a
                                    FileDescriptorSet — instead of asking the server. Produce one
                                    with:
                                      protoc --descriptor_set_out=<file> --include_imports <proto>
                                    (--include_imports is not optional: without it the set is
                                    missing the types it imports.) A descriptor set wins over
                                    server reflection when both are available. grpc list --protoset
                                    needs no address and opens no connection at all; an address
                                    given there is ignored.

        TLS / certificates:
        """ + "\n" + CliCert.HelpLines + """
          --insecure               Ignore server certificate errors

        Transport (a gRPC channel is HTTP/2 by definition, so HTTP-version pinning, redirects,
        decompression, and retries do not apply and have no flags here):
          --proxy <url>            Route through this proxy (e.g. http://proxy.corp:8080)
          --no-proxy               Ignore the system/PAC proxy
          --proxy-user <u:pass>    Proxy credentials

        Automatic tokens:
          A bearer token captured by an earlier certapi send to the same host is attached
          automatically as metadata; certapi grpc uses a captured token but never captures a new
          one.
          --no-auto-token          Do not attach a captured bearer token for this call
          --workspace <file>       Load pins and tokens from a workspace file instead of the live
                                    state

        Output:
          --json                   Print a JSON envelope instead of the plain rendering
          -q, --quiet              No metadata line on stderr

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Well-known Protobuf types (google.protobuf.Timestamp, Duration, Struct, Any, the wrapper
        types) render and are supplied in their canonical JSON forms — a Timestamp renders as an
        RFC 3339 string ("2023-11-14T22:13:20Z"), not {"seconds":"5","nanos":0}. An unresolved
        Any degrades visibly ({"@type":...,"value":"<base64>"}) rather than failing the call.

        Examples:
          (Examples use PowerShell quoting; in cmd.exe write JSON bodies as "{\"user\":\"me\"}".)

          certapi grpc list https://api.example.com:5001 --cert "CN=My Client"
          certapi grpc call https://api.example.com:5001 my.pkg.Greeter/SayHello -d '{"name":"Ada"}'
          certapi grpc call https://api.example.com:5001 Greeter/SayHello --json
          certapi grpc call https://api.example.com:5001 my.pkg.Feed/Watch --max-messages 5
          certapi grpc list --protoset api.protoset
          certapi grpc call https://api.example.com:5001 my.pkg.Greeter/SayHello --protoset api.protoset

        list prints services to stdout, one per line, indented with their methods (stream marks a
        streaming request or response); call prints the response — or, for a server-streaming
        method, one compact JSON object per line as each message arrives — to stdout. Everything
        else goes to stderr. Exit 0 on success (including a stream stopped early by
        --max-messages), 1 when the gRPC status is not OK, 2 on a bad command line (including an
        unsupported method kind), 3 on a data problem (reflection unavailable, an unknown
        service/method/field, or a descriptor set that is missing, unreadable, not a compiled
        FileDescriptorSet, or missing part of its dependency closure).
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        // ---- bind options (every one, even those a given subcommand ignores — Positionals()
        // rejects anything option-shaped left over) ----
        string? data = args.Value("-d", "--data");
        string? dataFile = args.Value("--data-file");
        var headers = args.Values("-H", "--header");
        string? maxMessagesRaw = args.Value("--max-messages");
        string store = args.Value("--store") ?? "CurrentUser";
        bool insecure = args.Flag("--insecure");
        string? proxyUrl = args.Value("--proxy");
        bool noProxy = args.Flag("--no-proxy");
        string? proxyUser = args.Value("--proxy-user");
        string? timeoutRaw = args.Value("--timeout");
        string? workspace = args.Value("--workspace");
        bool noAutoToken = args.Flag("--no-auto-token");
        bool json = args.Flag("--json");
        bool quiet = args.Flag("-q", "--quiet");
        string? protoset = args.Value("--protoset");
        var cert = CliCert.Resolve(args, store, services, stderr);

        if (data is not null && dataFile is not null)
            throw new CliUsageException("-d/--data and --data-file are mutually exclusive.");

        if (proxyUrl is not null && noProxy)
            throw new CliUsageException("--proxy and --no-proxy are mutually exclusive.");
        string? proxyAuthUser = null, proxyAuthPassword = null;
        if (proxyUser is not null)
        {
            if (proxyUrl is null)
                throw new CliUsageException("--proxy-user needs --proxy (there is no proxy to authenticate to).");
            int userColon = proxyUser.IndexOf(':');
            if (userColon < 0)
                throw new CliUsageException($"--proxy-user expects user:password, got '{proxyUser}'.");
            proxyAuthUser = proxyUser[..userColon];
            proxyAuthPassword = proxyUser[(userColon + 1)..];
        }

        int? maxMessages = null;
        if (maxMessagesRaw is not null)
        {
            if (!int.TryParse(maxMessagesRaw, out var m) || m < 1)
                throw new CliUsageException($"--max-messages expects a positive number, got '{maxMessagesRaw}'.");
            maxMessages = m;
        }

        int timeout = 100;
        if (timeoutRaw is not null && (!int.TryParse(timeoutRaw, out timeout) || timeout <= 0))
            throw new CliUsageException($"--timeout expects a positive number of seconds, got '{timeoutRaw}'.");

        var positionals = args.Positionals();
        if (positionals.Count == 0) throw new CliUsageException(Help);
        string sub = positionals[0].ToLowerInvariant();

        string addressRaw = "";
        string? serviceMethodRaw = null;
        switch (sub)
        {
            case "list" when positionals.Count == 2:
                addressRaw = positionals[1];
                break;
            // Ruling 3: listing what a descriptor set declares needs no server at all, so the
            // address is optional in this one combination — and when one is supplied anyway it is
            // simply ignored, because there is nothing to ask it.
            case "list" when positionals.Count == 1 && protoset is not null:
                break;
            case "call" when positionals.Count == 3:
                addressRaw = positionals[1];
                serviceMethodRaw = positionals[2];
                break;
            default:
                throw new CliUsageException(Help);
        }

        // A descriptor-set listing must not open a socket at all (ruling 3), so this returns before
        // the address is even parsed and before any channel exists — an address supplied alongside
        // --protoset (see the case above) is accepted and ignored rather than dialed. This is also
        // why an unreachable or malformed address is not an error in this one combination: it is
        // never looked at.
        if (sub == "list" && protoset is not null)
        {
            services.Log.Debug($"grpc list from descriptor set {protoset}");
            var loadStopwatch = Stopwatch.StartNew();
            var set = LoadDescriptorSet(protoset);
            loadStopwatch.Stop();
            return RunList(set.Services, loadStopwatch.Elapsed, stdout, stderr, json, quiet);
        }

        // D4: an address whose scheme is not http/https is a usage error — checked here so the
        // user gets this message rather than an ArgumentException out of GrpcCaller's constructor.
        if (!Uri.TryCreate(addressRaw, UriKind.Absolute, out var address) ||
            (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            string scheme = Uri.TryCreate(addressRaw, UriKind.Absolute, out var parsed)
                ? parsed.Scheme
                : "(not an absolute URI)";
            throw new CliUsageException(
                $"gRPC needs an absolute http or https address; got scheme '{scheme}' from '{addressRaw}' " +
                "(only http and https are accepted).");
        }

        var metadata = new List<KeyValuePair<string, string>>();
        foreach (var raw in headers)
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0) throw new CliUsageException($"Header must be \"Name: value\", got '{raw}'.");
            metadata.Add(new(raw[..colon].Trim(), raw[(colon + 1)..].Trim()));
        }

        string body = data ?? (dataFile is not null
            ? File.Exists(dataFile) ? File.ReadAllText(dataFile) : throw new CliDataException($"Body file not found: {dataFile}")
            : "{}");

        // ---- workspace, per-site trust, and the automatic session token (mirrors SendCommand) ----
        var state = LoadWorkspaceOrEmpty(workspace, services, stderr);
        string host = TokenService.HostOf(addressRaw);
        Func<X509Certificate2?, bool> trustCert = c =>
            c is not null && TrustService.IsTrusted(state, host, c.Thumbprint!);

        if (!noAutoToken)
        {
            var used = TokenService.AutoAttach(state, addressRaw, metadata, out var expired);
            if (used is not null)
            {
                if (!quiet) stderr.WriteLine($"note: using captured token for {host}");
                services.Log.Debug($"auto token attached for {used.Origin} ({used.Source})");
            }
            else if (expired is not null && !quiet)
            {
                stderr.WriteLine($"note: the captured token for {host} has expired — sending without it");
            }
        }

        var transport = new TransportOptions
        {
            IgnoreServerCertificateErrors = insecure,
            Proxy = proxyUrl is not null ? ProxyMode.Explicit : noProxy ? ProxyMode.None : ProxyMode.System,
            ProxyUrl = proxyUrl,
            ProxyUser = proxyAuthUser,
            ProxyPassword = proxyAuthPassword
        };

        services.Log.Debug($"grpc {sub} {address}");
        if (protoset is not null) services.Log.Debug($"descriptors: from {protoset} (reflection not consulted)");
        services.Log.Debug(cert is null ? "certificate: none" : $"certificate: {cert.Subject} ({cert.Thumbprint})");
        services.Log.Debug($"timeout: {timeout} s · insecure: {insecure} · store: {store}");
        foreach (var h in metadata)
            services.Log.Debug("metadata: " + (h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                ? $"{h.Key}: {TokenService.MaskAuthorization(h.Value)}" : $"{h.Key}: {h.Value}"));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(services.Cancel);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        return RunAsync().GetAwaiter().GetResult();

        async Task<int> RunAsync()
        {
            // Loaded before the channel is built so a bad descriptor set is exit 3 with a message
            // about the file, rather than a connection failure against a server that was never the
            // problem. From here on, list and call are identical whichever source the descriptors
            // came from: caller.DiscoverAsync already returns the descriptor set's services without
            // a reflection round trip when one was supplied (ruling 2), so RunList/RunCallAsync below
            // never need to know whether --protoset was given at all — that is why this slice is small.
            var descriptorSet = protoset is null ? null : LoadDescriptorSet(protoset);
            await using var caller = new GrpcCaller(address, cert, transport, trustCert, descriptorSet);

            IReadOnlyList<GrpcServiceInfo> discovered;
            var discoverStopwatch = Stopwatch.StartNew();
            try
            {
                discovered = await caller.DiscoverAsync(cts.Token);
            }
            catch (GrpcReflectionUnavailableException ex)
            {
                // The library's message already names both the problem and the --protoset fix —
                // pass it through as-is rather than rewording it.
                throw new CliDataException(ex.Message);
            }
            catch (GrpcStatusException ex)
            {
                if (!quiet) stderr.WriteLine($"{ex.StatusName} ({ex.StatusCode}): {ex.StatusDetail}");
                return ExitCodes.Failure;
            }
            discoverStopwatch.Stop();
            services.Log.Debug(
                $"discovery: {discovered.Count} service(s) in {discoverStopwatch.Elapsed.TotalMilliseconds:F0} ms");

            if (sub == "list")
                return RunList(discovered, discoverStopwatch.Elapsed, stdout, stderr, json, quiet);

            // Ruling 2 wording: with --protoset, no server advertised anything — the descriptor set
            // declared it — so "not found" messages must say so rather than implying a server was
            // asked and answered.
            string sourceLabel = protoset is null ? "the server advertises" : "the descriptor set declares";

            try
            {
                return await RunCallAsync(caller, discovered, serviceMethodRaw!, sourceLabel, body, metadata,
                    maxMessages, stdout, stderr, json, quiet, services, cts.Token);
            }
            catch (GrpcMethodNotFoundException ex) { throw new CliDataException(ex.Message); }
            catch (GrpcJsonException ex) { throw new CliDataException(ex.Message); }
            catch (GrpcUnsupportedMethodException ex) { throw new CliUsageException(ex.Message); }
            catch (ArgumentException ex) { throw new CliUsageException(ex.Message); }
            // Belt-and-braces: the CLI normally resolves the service against the discovered list
            // first (ResolveServiceName, above RunCallAsync's dispatch), so in practice a descriptor
            // set that lacks the requested service is already caught there. This catch exists for a
            // descriptor-set lookup that still fails inside GrpcCaller itself (e.g. a method resolved
            // against the wrong service internally) — exit 3 either way, never a stack trace.
            catch (GrpcDescriptorSetException ex) { throw new CliDataException(ex.Message); }
        }
    }

    /// <summary>The single translation point from ApiTester.Grpc's descriptor-set errors to the
    /// CLI's exit 3: a missing file, an unreadable one, one that is not a compiled
    /// FileDescriptorSet (including the classic mistake of passing the .proto source), and one
    /// missing part of its dependency closure all arrive here already carrying a message written
    /// for the user, so this adds nothing and hides nothing.</summary>
    private static GrpcDescriptorSet LoadDescriptorSet(string path)
    {
        try { return GrpcDescriptorSet.Load(path); }
        catch (GrpcDescriptorSetException ex) { throw new CliDataException(ex.Message); }
    }

    /// <summary>Prints the discovered services/methods (plain or --json) to stdout, and the
    /// summary line to stderr unless -q.</summary>
    private static int RunList(
        IReadOnlyList<GrpcServiceInfo> discovered, TimeSpan elapsed,
        TextWriter stdout, TextWriter stderr, bool json, bool quiet)
    {
        if (json)
        {
            var payload = discovered.Select(s => new
            {
                service = s.Name,
                methods = s.Methods.Select(m => new
                {
                    name = m.Name,
                    clientStreaming = m.ClientStreaming,
                    serverStreaming = m.ServerStreaming,
                    inputType = m.InputType,
                    outputType = m.OutputType
                }).ToList()
            }).ToList();
            stdout.WriteLine(JsonSerializer.Serialize(payload, Indented));
        }
        else
        {
            foreach (var s in discovered)
            {
                stdout.WriteLine(s.Name);
                foreach (var m in s.Methods)
                {
                    string input = m.ClientStreaming ? $"stream {m.InputType}" : m.InputType;
                    string output = m.ServerStreaming ? $"stream {m.OutputType}" : m.OutputType;
                    stdout.WriteLine($"  {m.Name}({input}) returns ({output})");
                }
            }
        }

        if (!quiet)
        {
            int methodCount = discovered.Sum(s => s.Methods.Count);
            stderr.WriteLine(
                $"{discovered.Count} service{(discovered.Count == 1 ? "" : "s")} · " +
                $"{methodCount} method{(methodCount == 1 ? "" : "s")} · {elapsed.TotalMilliseconds:F0} ms");
        }
        return ExitCodes.Ok;
    }

    /// <summary>Splits Service/Method on the last '/', resolves the service name generously
    /// against what reflection discovered, finds the method, and dispatches to the unary or
    /// server-streaming path. A client-streaming (or bidirectional) method is out of scope for
    /// this version — a usage error, since the user asked for something the tool does not do.</summary>
    private static async Task<int> RunCallAsync(
        GrpcCaller caller, IReadOnlyList<GrpcServiceInfo> discovered, string serviceMethodRaw, string sourceLabel,
        string body, IReadOnlyList<KeyValuePair<string, string>> metadata, int? maxMessages,
        TextWriter stdout, TextWriter stderr, bool json, bool quiet, CliServices services, CancellationToken ct)
    {
        int slash = serviceMethodRaw.LastIndexOf('/');
        if (slash <= 0 || slash == serviceMethodRaw.Length - 1)
            throw new CliUsageException(
                $"'{serviceMethodRaw}' doesn't look like Service/Method — expected e.g. my.pkg.Greeter/SayHello.\n{Help}");

        string given = serviceMethodRaw[..slash];
        string methodName = serviceMethodRaw[(slash + 1)..];
        string service = ResolveServiceName(discovered, given, sourceLabel);
        var serviceInfo = discovered.First(s => s.Name == service);
        var methodInfo = serviceInfo.Methods.FirstOrDefault(m => m.Name == methodName);
        if (methodInfo is null)
        {
            string known = serviceInfo.Methods.Count == 0
                ? "(none)"
                : string.Join(", ", serviceInfo.Methods.Select(m => m.Name));
            throw new CliDataException(
                $"Method '{methodName}' was not found on service '{service}'. Methods that do exist: {known}.");
        }
        if (methodInfo.ClientStreaming)
            throw new CliUsageException(
                $"'{service}/{methodName}' is client-streaming; client-streaming and bidirectional " +
                "methods are out of scope for this version.");

        services.Log.Debug($"resolved to {service}/{methodName}");

        return methodInfo.ServerStreaming
            ? await RunStreamingAsync(caller, service, methodName, body, metadata, maxMessages, stdout, stderr, json, quiet, services, ct)
            : await RunUnaryAsync(caller, service, methodName, body, metadata, stdout, stderr, json, quiet, services, ct);
    }

    /// <summary>Resolves a possibly-short service name against what was discovered — reflection, or
    /// a descriptor set when --protoset was given: an exact match wins; otherwise exactly one
    /// discovered name ending with "." + given wins (so "Echo/Unary" finds "certapi.test.Echo");
    /// several matches or none is a data error naming the candidates or the services actually
    /// available, worded via <paramref name="sourceLabel"/> so the message stays honest about where
    /// that list came from ("the server advertises" vs. "the descriptor set declares").</summary>
    private static string ResolveServiceName(IReadOnlyList<GrpcServiceInfo> discovered, string given, string sourceLabel)
    {
        var exact = discovered.FirstOrDefault(s => s.Name == given);
        if (exact is not null) return exact.Name;

        var suffixMatches = discovered.Where(s => s.Name.EndsWith("." + given, StringComparison.Ordinal)).ToList();
        if (suffixMatches.Count == 1) return suffixMatches[0].Name;
        if (suffixMatches.Count > 1)
            throw new CliDataException(
                $"'{given}' matches more than one service: {string.Join(", ", suffixMatches.Select(s => s.Name))}.");

        string knownServices = discovered.Count == 0 ? "(none)" : string.Join(", ", discovered.Select(s => s.Name));
        throw new CliDataException($"Service '{given}' was not found. Services {sourceLabel}: {knownServices}.");
    }

    private static async Task<int> RunUnaryAsync(
        GrpcCaller caller, string service, string method, string body,
        IReadOnlyList<KeyValuePair<string, string>> metadata,
        TextWriter stdout, TextWriter stderr, bool json, bool quiet, CliServices services, CancellationToken ct)
    {
        var result = await caller.InvokeAsync(service, method, body, metadata, ct);
        services.Log.Debug($"elapsed: {result.Elapsed.TotalMilliseconds:F0} ms");

        if (result.StatusCode != 0)
        {
            if (json)
                stdout.WriteLine(BuildCallEnvelope(
                    result.StatusCode, result.StatusName, result.StatusDetail, result.Elapsed.TotalMilliseconds,
                    result.Trailers, message: null, messages: null));
            if (!quiet) stderr.WriteLine($"{result.StatusName} ({result.StatusCode}): {result.StatusDetail}");
            return ExitCodes.Failure;
        }

        if (json)
            stdout.WriteLine(BuildCallEnvelope(
                0, "OK", "", result.Elapsed.TotalMilliseconds, result.Trailers,
                message: JsonNode.Parse(result.ResponseJson), messages: null));
        else
            stdout.WriteLine(result.ResponseJson);

        if (!quiet) stderr.WriteLine($"OK · {result.Elapsed.TotalMilliseconds:F0} ms");
        return ExitCodes.Ok;
    }

    /// <summary>Non-JSON: one compact JSON object per line, flushed as each arrives, so a slow
    /// stream is visible as it happens. JSON: the lines are collected instead and rendered as one
    /// "messages" array in the envelope at the end, per the --json contract. Either way, stopping
    /// at --max-messages is success (exit 0); a non-OK status ending the stream is exit 1, with
    /// whatever already reached stdout left in place.</summary>
    private static async Task<int> RunStreamingAsync(
        GrpcCaller caller, string service, string method, string body,
        IReadOnlyList<KeyValuePair<string, string>> metadata, int? maxMessages,
        TextWriter stdout, TextWriter stderr, bool json, bool quiet, CliServices services, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        List<JsonNode?>? collected = json ? new List<JsonNode?>() : null;
        int count = 0;
        bool truncated = false;

        try
        {
            await foreach (var message in caller.InvokeStreamingAsync(service, method, body, metadata, ct))
            {
                count++;
                if (json) collected!.Add(JsonNode.Parse(message));
                else { stdout.WriteLine(message); stdout.Flush(); }

                if (maxMessages is int max && count >= max) { truncated = true; break; }
            }
        }
        catch (GrpcStatusException ex)
        {
            stopwatch.Stop();
            services.Log.Debug($"elapsed: {stopwatch.Elapsed.TotalMilliseconds:F0} ms · {count} message(s)");
            if (json)
                stdout.WriteLine(BuildCallEnvelope(
                    ex.StatusCode, ex.StatusName, ex.StatusDetail, stopwatch.Elapsed.TotalMilliseconds,
                    ex.Trailers, message: null, messages: collected));
            if (!quiet) stderr.WriteLine($"{ex.StatusName} ({ex.StatusCode}): {ex.StatusDetail}");
            return ExitCodes.Failure;
        }

        stopwatch.Stop();
        services.Log.Debug($"elapsed: {stopwatch.Elapsed.TotalMilliseconds:F0} ms · {count} message(s)");

        if (json)
            stdout.WriteLine(BuildCallEnvelope(
                0, "OK", "", stopwatch.Elapsed.TotalMilliseconds, Array.Empty<KeyValuePair<string, string>>(),
                message: null, messages: collected));

        if (!quiet)
        {
            string plural = count == 1 ? "" : "s";
            stderr.WriteLine(truncated
                ? $"stopped after {count} message{plural} (--max-messages) · {stopwatch.Elapsed.TotalMilliseconds:F0} ms"
                : $"OK · {count} message{plural} · {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        }
        return ExitCodes.Ok;
    }

    /// <summary>The shared --json envelope shape for a call, unary or streaming, success or
    /// failure: {status, statusName, detail, elapsedMs, trailers} always, plus "message" (a single
    /// nested object) or "messages" (an array) only when there is one to show.</summary>
    private static string BuildCallEnvelope(
        int status, string statusName, string detail, double elapsedMs,
        IReadOnlyList<KeyValuePair<string, string>> trailers, JsonNode? message, List<JsonNode?>? messages)
    {
        var obj = new Dictionary<string, object?>
        {
            ["status"] = status,
            ["statusName"] = statusName,
            ["detail"] = detail,
            ["elapsedMs"] = Math.Round(elapsedMs),
            ["trailers"] = TrailersToObject(trailers)
        };
        if (message is not null) obj["message"] = message;
        if (messages is not null) obj["messages"] = messages;
        return JsonSerializer.Serialize(obj, Indented);
    }

    private static Dictionary<string, string> TrailersToObject(IReadOnlyList<KeyValuePair<string, string>> trailers)
    {
        var obj = new Dictionary<string, string>();
        foreach (var t in trailers) obj[t.Key] = t.Value;
        return obj;
    }

    /// <summary>Deliberately duplicates SendCommand's private helper of the same shape rather than
    /// sharing it: SendCommand's copy is private, and widening its visibility would be an unrelated
    /// change to a file this slice does not own. A missing explicit --workspace file is a data
    /// error (there is nothing for grpc to create — unlike send's --capture, grpc never writes to
    /// the state); a corrupt *live* state (no --workspace given) warns on stderr and continues with
    /// an empty state, the same tolerance the GUI itself uses.</summary>
    private static AppState LoadWorkspaceOrEmpty(string? workspace, CliServices services, TextWriter stderr)
    {
        if (workspace is null)
        {
            try { return CliWorkspace.Load(null, services.LiveStatePath); }
            catch (CliDataException ex)
            {
                stderr.WriteLine($"warning: could not read the live state ({ex.Message}) — continuing without saved tokens/environments");
                return new AppState();
            }
        }
        return CliWorkspace.Load(workspace, services.LiveStatePath);
    }
}
