using System.Net.Http;
using System.Text;
using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class SendCommand
{
    public const string Help = """
        Usage: certapi send <url> [options]

        Request:
          -X, --method <m>        HTTP method (default GET)
          -H, --header "k: v"     Add a header (repeatable)
          -d, --data <body>       Request body ( --data-file <file> reads it from disk )
          --content-type <ct>     Body content type (default application/json)
          --bearer <token>        Authorization: Bearer …
          --basic <user:pass>     Authorization: Basic …
          --timeout <seconds>     Default 100

        TLS / certificates:
          --cert <thumb|subject>  Client certificate from the Windows store
          --store <location>      CurrentUser (default); LocalMachine searches both stores
          --cert-file <path>      Client certificate from a file (.pfx/.p12 or .pem/.crt) instead
          --cert-password <pw>    Password for a .pfx/.p12 certificate file
          --key-file <path>       Private-key file for a PEM cert whose key is in a separate file
          --insecure              Ignore server certificate errors

        Automatic tokens:
          A bearer token found in a response (access_token, id_token, token, accessToken, jwt,
          or an X-Auth-Token / X-Access-Token header) is captured and scoped to that host.
          Later sends to the same host attach it automatically — unless you pass explicit auth
          (--bearer / --basic / an Authorization header).
          --no-auto-token         Disable capture and reuse for this invocation

        Variables:
          --env <name>            Environment ({{var}} values) from your workspace
          --var k=v               Override/add a variable (repeatable)
          --workspace <file>      Load environments from a workspace file instead of the live state
          --strict-vars           Unresolved {{tokens}} become an error instead of a warning
          --capture var=path      Save a response value into an environment variable after the
                                  send (repeatable). path is a JSON body path (access_token,
                                  data.token) or header:Name for a response header.

        Output:
          -o, --output <file>     Write the body to a file instead of stdout
          --include               Print status line and headers before the body
          --pretty                Pretty-print the body (JSON/XML; hex for binary)
          --json                  Print a JSON result envelope instead of the raw body
          --fail                  Exit 1 when the HTTP status is 400 or higher
          -q, --quiet             No metadata line on stderr

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          (Examples use PowerShell quoting; in cmd.exe write JSON bodies as "{\"user\":\"me\"}".)

          # Simple GET with a client certificate picked by subject
          certapi send https://api.example.com/users --cert "CN=My Client"

          # POST JSON, pretty-print the response
          certapi send https://api.example.com/users -X POST -d '{"name":"Ada"}' --pretty

          # Log in once, then call the API — the token is captured and reused automatically
          certapi send https://api.example.com/login -X POST -d '{"user":"me","pass":"..."}'
          # the follow-on call sends Authorization: Bearer … automatically
          certapi send https://api.example.com/orders

          # Headers, query strings, and a file body
          certapi send "https://api.example.com/search?q=abc" -H "Accept: application/json"
          certapi send https://api.example.com/upload -X PUT --data-file .\payload.json

          # Environments and capture rules
          certapi send "https://{{host}}/login" --env Staging --capture session=data.session_id

          # Save a binary body, keep stderr clean, fail the build on HTTP errors
          certapi send https://api.example.com/report.pdf -o report.pdf -q --fail

          # Troubleshoot a failing endpoint with full diagnostics in a file
          certapi send https://api.example.com/broken --debug --log-file broken.log

        The body goes to stdout; everything else goes to stderr. Exit 0 on a delivered
        response (any status unless --fail), 1 on transport errors, 2/3 on usage/data errors.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, Stream bodyOut, CliServices services)
    {
        // ---- bind options ----
        string method = args.Value("-X", "--method") ?? "GET";
        var headers = args.Values("-H", "--header");
        string? data = args.Value("-d", "--data");
        string? dataFile = args.Value("--data-file");
        string? contentType = args.Value("--content-type");
        string? bearer = args.Value("--bearer");
        string? basic = args.Value("--basic");
        string store = args.Value("--store") ?? "CurrentUser";
        bool insecure = args.Flag("--insecure");
        string? timeoutRaw = args.Value("--timeout");
        int timeout = 100;
        if (timeoutRaw is not null && (!int.TryParse(timeoutRaw, out timeout) || timeout <= 0))
            throw new CliUsageException($"--timeout expects a positive number of seconds, got '{timeoutRaw}'.");
        string? envName = args.Value("--env");
        var varOverrides = args.Values("--var");
        string? workspace = args.Value("--workspace");
        bool strictVars = args.Flag("--strict-vars");
        string? outFile = args.Value("-o", "--output");
        bool include = args.Flag("--include");
        bool pretty = args.Flag("--pretty");
        bool json = args.Flag("--json");
        bool fail = args.Flag("--fail");
        bool quiet = args.Flag("-q", "--quiet");
        var captureSpecs = args.Values("--capture");
        bool noAutoToken = args.Flag("--no-auto-token");
        // Resolve the certificate here (Windows store or a file) so its options are consumed
        // before Positionals() rejects anything option-shaped that's left over.
        var cert = CliCert.Resolve(args, store, services, stderr);

        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);
        string url = positionals[0];

        var captureRules = new List<CaptureRule>();
        foreach (var raw in captureSpecs)
        {
            int eq = raw.IndexOf('=');
            if (eq <= 0) throw new CliUsageException($"--capture expects var=path, got '{raw}'.");
            string variable = raw[..eq].Trim();
            if (variable.Length == 0) throw new CliUsageException($"--capture needs a variable name, got '{raw}'.");
            string path = raw[(eq + 1)..];
            bool header = path.StartsWith("header:", StringComparison.OrdinalIgnoreCase);
            captureRules.Add(new CaptureRule
            {
                Variable = variable,
                Source = header ? CaptureSource.Header : CaptureSource.Body,
                Path = header ? path["header:".Length..] : path
            });
        }
        if (data is not null && dataFile is not null)
            throw new CliUsageException("-d/--data and --data-file are mutually exclusive.");
        if (bearer is not null && basic is not null)
            throw new CliUsageException("--bearer and --basic are mutually exclusive.");
        string? body = data ?? (dataFile is not null
            ? File.Exists(dataFile) ? File.ReadAllText(dataFile) : throw new CliDataException($"Body file not found: {dataFile}")
            : null);

        // An explicit --workspace file that doesn't exist is an error — unless --capture is
        // creating it (a capture write starts from an empty workspace).
        if (workspace is not null && captureRules.Count == 0 && !File.Exists(workspace))
            throw new CliDataException($"Workspace file not found: {workspace}");

        // ---- variables ----
        // The state is always loaded now: even without --env, the live state may hold a
        // captured session token for this URL's origin. A --workspace that doesn't exist yet
        // is fine when --capture is present — it is created fresh on save.
        var state = LoadWorkspaceOrEmpty(workspace, services, stderr);
        var vars = CliWorkspace.BuildVars(state, envName, varOverrides);
        var unresolved = new List<string>();
        string R(string s)
        {
            var (resolved, missing) = VariableResolver.Resolve(s ?? "", vars);
            foreach (var m in missing) if (!unresolved.Contains(m)) unresolved.Add(m);
            return resolved;
        }

        url = R(url);
        var headerPairs = new List<KeyValuePair<string, string>>();
        foreach (var raw in headers)
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0) throw new CliUsageException($"Header must be \"Name: value\", got '{raw}'.");
            headerPairs.Add(new(R(raw[..colon].Trim()), R(raw[(colon + 1)..].Trim())));
        }
        if (bearer is not null) headerPairs.Add(new("Authorization", "Bearer " + R(bearer)));
        if (basic is not null) headerPairs.Add(new("Authorization",
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(R(basic)))));
        if (body is not null) body = R(body);

        if (unresolved.Count > 0)
        {
            var tokens = string.Join(", ", unresolved.Select(u => "{{" + u + "}}"));
            if (strictVars) throw new CliDataException($"Unresolved variables: {tokens}");
            stderr.WriteLine($"warning: unresolved variables: {tokens}");
        }

        // ---- automatic session token ----
        if (!noAutoToken)
        {
            var used = TokenService.AutoAttach(state, url, headerPairs, out var expired);
            if (used is not null)
            {
                if (!quiet) stderr.WriteLine($"note: using captured token for {TokenService.HostOf(url)}");
                services.Log.Debug($"auto token attached for {used.Origin} ({used.Source})");
            }
            else if (expired is not null && !quiet)
            {
                stderr.WriteLine($"note: the captured token for {TokenService.HostOf(url)} has expired — sending without it");
            }
        }


        // ---- send ----
        var request = new ApiRequest
        {
            Method = new HttpMethod(method.ToUpperInvariant()),
            Url = url,
            Headers = headerPairs,
            Body = body,
            ContentType = body is not null ? (contentType ?? "application/json") : null,
            Timeout = TimeSpan.FromSeconds(timeout)
        };
        services.Log.Debug($"{request.Method} {request.Url}");
        foreach (var h in headerPairs)
            services.Log.Debug("header: " + (h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                ? $"{h.Key}: {TokenService.MaskAuthorization(h.Value)}" : $"{h.Key}: {h.Value}"));
        services.Log.Debug(cert is null ? "certificate: none" : $"certificate: {cert.Subject} ({cert.Thumbprint})");
        services.Log.Debug($"timeout: {timeout} s · insecure: {insecure} · store: {store}");
        var response = services.Client.SendAsync(request, cert, insecure,
            cancellationToken: services.Cancel).GetAwaiter().GetResult();
        services.Log.Debug("result: " + (response.Error is null
            ? $"{response.StatusCode} {response.ReasonPhrase}".Trim()
            : $"[{response.Error.Kind}] {response.Error.Message}")
            + $" · {response.Elapsed.TotalMilliseconds:F0} ms · {response.Body.LongLength} bytes");
        if (response.Connection is { } conn)
            services.Log.Debug($"connection: tls {conn.TlsProtocol ?? "—"} · proxy {(conn.ViaProxy ? "yes" : "no")} · client cert sent {(conn.ClientCertificateSent ? "yes" : "no")}");

        if (!quiet) stderr.WriteLine(OutputText.MetaLine(response));

        bool stateDirty = false;
        if (captureRules.Count > 0 && response.Error is null)
        {
            var outcome = CaptureApplier.Apply(state, captureRules, response.Body, response.ContentType, response.Headers);
            var ok = outcome.Where(o => o.Ok).Select(o => o.Variable).ToList();
            if (ok.Count > 0) stderr.WriteLine("captured " + string.Join(", ", ok));
            foreach (var b in outcome.Where(o => !o.Ok)) stderr.WriteLine($"capture '{b.Variable}' failed: {b.Error}");
            stateDirty |= outcome.Count > 0;
        }
        if (!noAutoToken && response.Error is null &&
            TokenService.Capture(state, url, response.Body, response.ContentType, response.Headers) is { } captured)
        {
            if (!quiet)
            {
                string expiry = captured.ExpiresUtc is { } e
                    ? $", expires in {(int)Math.Max(1, (e - DateTime.UtcNow).TotalMinutes)} min" : "";
                stderr.WriteLine($"note: captured bearer token for {TokenService.HostOf(url)} ({captured.Source}{expiry})");
            }
            services.Log.Debug($"token captured for {captured.Origin} ({captured.Source}): {TokenService.Mask(captured.Token)}");
            stateDirty = true;
        }
        if (stateDirty) SaveState(state, workspace, services, stderr);

        // ---- output ----
        if (json)
        {
            if (outFile is not null && response.Error is null) File.WriteAllBytes(outFile, response.Body);
            stdout.WriteLine(BuildEnvelope(response, includeBody: outFile is null));
        }
        else if (response.Error is null)
        {
            if (include)
            {
                stdout.WriteLine($"{response.StatusCode} {response.ReasonPhrase}".Trim());
                foreach (var h in response.Headers) stdout.WriteLine($"{h.Key}: {h.Value}");
                stdout.WriteLine();
            }
            if (outFile is not null) File.WriteAllBytes(outFile, response.Body);
            else if (pretty) stdout.WriteLine(new ResponseFormatter().Format(response).Text);
            else { stdout.Flush(); bodyOut.Write(response.Body); bodyOut.Flush(); }
        }

        if (response.Error is not null) return ExitCodes.Failure;
        if (fail && response.StatusCode is >= 400) return ExitCodes.Failure;
        return ExitCodes.Ok;
    }

    /// <summary>Like <see cref="CliWorkspace.Load"/>, but a missing explicit workspace file yields an
    /// empty workspace instead of an error — --capture is allowed to create the file on save. A
    /// corrupt *live* state (no --workspace given) is tolerated the same way the GUI tolerates it:
    /// warn on stderr and start fresh, rather than failing every plain send. An explicit --workspace
    /// keeps its current behavior — a corrupt file the caller named is a data error.</summary>
    private static AppState LoadWorkspaceOrEmpty(string? workspace, CliServices services, TextWriter stderr)
    {
        if (workspace is not null && !File.Exists(workspace)) return new AppState();
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

    private static void SaveState(AppState state, string? workspace, CliServices services, TextWriter stderr)
    {
        if (workspace is null && services.IsGuiRunning())
        {
            stderr.WriteLine("note: the GUI is running — captured values were not saved (it would overwrite them on close).");
            return;
        }
        try { state.SaveTo(workspace ?? services.LiveStatePath); }
        catch (Exception ex) { stderr.WriteLine($"warning: could not save captured values: {ex.Message}"); }
    }

    internal static string BuildEnvelope(ApiResponse r, bool includeBody, IReadOnlyList<string>? notes = null)
    {
        bool binary = false;
        string? text = null;
        if (includeBody && r.Error is null)
        {
            // UTF8.GetString never throws — invalid bytes decode to U+FFFD, which marks the body binary.
            text = Encoding.UTF8.GetString(r.Body);
            binary = text.Contains('�');
        }
        var obj = new Dictionary<string, object?>
        {
            ["status"] = r.StatusCode,
            ["reasonPhrase"] = r.ReasonPhrase,
            ["elapsedMs"] = Math.Round(r.Elapsed.TotalMilliseconds),
            ["contentType"] = r.ContentType,
            ["sizeBytes"] = r.Body.LongLength,
            ["headers"] = r.Headers
                .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Count() == 1 ? (object)g.First().Value : g.Select(h => h.Value).ToArray()),
            ["tlsProtocol"] = r.Connection?.TlsProtocol,
            ["clientCertPresented"] = r.Connection?.ClientCertificateSent ?? false,
            ["error"] = r.Error is null ? null : new { kind = r.Error.Kind.ToString(), message = r.Error.Message }
        };
        if (notes is { Count: > 0 }) obj["notes"] = notes;
        if (includeBody && r.Error is null)
        {
            if (binary) obj["bodyBase64"] = Convert.ToBase64String(r.Body);
            else obj["body"] = text;
        }
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
    }
}
