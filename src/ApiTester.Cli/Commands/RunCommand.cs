using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class RunCommand
{
    public const string Help = """
        Usage: certapi run <Collection[/Folder][/Request]> [options]
               certapi run --all [options]

        Runs saved requests. A folder or collection path runs everything beneath it as a
        suite; a request path runs that one request. A request passes when its assertions all
        pass (Status / Time / Header / Body / Body-text checks set on it in the app); a request
        with no assertions passes on any 2xx response. Failed assertions are listed on stderr.

        Options:
          --all                   Run every saved request in the workspace
          --workspace <file>      Load collections from a workspace file (default: live GUI state)
          --env <name>            Environment for {{variables}}; --var k=v overrides (repeatable)
          --data <file>           Data-driven run: repeat the request(s) once per row of a CSV or
                                  JSON file, the row's columns overriding {{variables}}
          --record / --no-record  Write known-good results back (default: on for live state,
                                  off for workspace files; skipped while the GUI is running)
          --strict-vars           Unresolved {{tokens}} fail the request
          --no-auto-token         Don't capture or attach session tokens during this run
          --cookies               Keep a cookie jar for the run, so a login's Set-Cookie is sent
                                  on later requests (cookie-based sessions)
          --json                  JSON results instead of the table

        Requests whose Auth is "Auto" attach the captured token for their host; a token
        captured by one request (e.g. a login) is reused by the rest of the suite.

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          # Run one request, a folder, or everything
          certapi run "petstore/Get pet by id"
          certapi run petstore/smoke
          certapi run --all

          # A login-first suite: the login response's token carries through the suite
          certapi run "api/login then browse" --env Staging

          # Data-driven: run one request once per row of users.csv (columns become {{variables}})
          certapi run "api/Get user" --data .\users.csv

          # CI: machine-readable results, no writes at all, fail the job on any failure
          certapi run --all --workspace .\suite.json --no-record --no-auto-token --json

          # Investigate a flaky suite with full diagnostics
          certapi run api --debug --log-file suite-debug.log

        Exit codes: 0 all passed · 1 any failure · 2 usage · 3 data error.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        bool all = args.Flag("--all");
        string? workspace = args.Value("--workspace");
        string? envName = args.Value("--env");
        var varOverrides = args.Values("--var");
        bool recordFlag = args.Flag("--record");
        bool noRecord = args.Flag("--no-record");
        bool strictVars = args.Flag("--strict-vars");
        bool json = args.Flag("--json");
        bool noAutoToken = args.Flag("--no-auto-token");
        string? dataFile = args.Value("--data");
        bool useCookies = args.Flag("--cookies");
        var positionals = args.Positionals();
        if (positionals.Count > 1 || (positionals.Count == 0 && !all)) throw new CliUsageException(Help);

        var state = CliWorkspace.Load(workspace, services.LiveStatePath);
        var targets = CliWorkspace.ResolveTargets(state, positionals.FirstOrDefault(), all);

        // Data-driven runs: one iteration per dataset row, its columns overriding the variables.
        IReadOnlyList<IReadOnlyDictionary<string, string>?> rows;
        if (dataFile is null) rows = new IReadOnlyDictionary<string, string>?[] { null };
        else
        {
            try { rows = DataSet.Load(dataFile).Cast<IReadOnlyDictionary<string, string>?>().ToList(); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            { throw new CliDataException($"Could not read data file '{dataFile}': {ex.Message}"); }
            if (rows.Count == 0) throw new CliDataException($"The data file '{dataFile}' has no rows.");
        }

        Dictionary<string, string> BuildIterVars(IReadOnlyDictionary<string, string>? row)
        {
            var v = CliWorkspace.BuildVars(state, envName, varOverrides);
            if (row is not null) foreach (var kv in row) v[kv.Key] = kv.Value;
            return v;
        }

        // If --env names an existing environment, make it the capture target so a token captured
        // by one request in this run is reusable by later requests via {{var}}.
        if (envName is not null &&
            state.Environments.FirstOrDefault(e => e.Name.Equals(envName, StringComparison.OrdinalIgnoreCase)) is { } namedEnv)
            state.ActiveEnvironmentId = namedEnv.Id;

        bool record = !noRecord && (workspace is null || recordFlag);
        if (record && workspace is null && services.IsGuiRunning())
        {
            record = false;
            stderr.WriteLine("note: the GUI is running — results were not recorded (it would overwrite them on close).");
        }

        // One cookie jar for the whole run, so a login's Set-Cookie carries to later requests.
        var jar = useCookies ? new System.Net.CookieContainer() : null;
        bool capturedAny = false;
        bool tokensCaptured = false;
        var results = new List<(string Path, RequestModel Model, ApiResponse Response)>();
        var clock = Stopwatch.StartNew();
        int rowIndex = 0;
        foreach (var row in rows)
        {
            rowIndex++;
            var vars = BuildIterVars(row);
            string label = dataFile is null ? "" : $"[row {rowIndex}] ";
            foreach (var (path, node) in targets)
            {
                string id = label + path;
                var (response, url) = Execute(id, node.Request!, state, noAutoToken, vars, strictVars, jar, stderr, services);
                results.Add((id, node.Request!, response));
                if (!noAutoToken && response.Error is null &&
                    TokenService.Capture(state, url, response.Body, response.ContentType, response.Headers) is { } captured)
                {
                    stderr.WriteLine($"{id}: captured bearer token for {TokenService.HostOf(url)} ({captured.Source})");
                    tokensCaptured = true;
                }
                if (record) node.RecordResult(response.Error is null ? response.StatusCode : null, DateTime.UtcNow);
                if (node.Request!.Assertions.Any(a => a.Enabled))
                    foreach (var ar in AssertionEvaluator.Evaluate(node.Request!.Assertions, response).Where(a => !a.Passed))
                        stderr.WriteLine($"{id}: assertion failed — {ar.Description} (got {ar.Actual ?? "∅"})");
                if (response.Error is null && node.Request!.Captures.Count > 0)
                {
                    var outcome = CaptureApplier.Apply(state, node.Request!.Captures, response.Body, response.ContentType, response.Headers);
                    if (outcome.Count > 0)
                    {
                        capturedAny = true;
                        var okVars = outcome.Where(o => o.Ok).Select(o => o.Variable).ToList();
                        if (okVars.Count > 0) stderr.WriteLine($"{id}: captured " + string.Join(", ", okVars));
                        foreach (var b in outcome.Where(o => !o.Ok)) stderr.WriteLine($"{id}: capture '{b.Variable}' failed: {b.Error}");
                        vars = BuildIterVars(row);
                    }
                }
            }
        }
        clock.Stop();

        bool guiBlocksLiveWrite = workspace is null && services.IsGuiRunning();
        if ((record || capturedAny || tokensCaptured) && !guiBlocksLiveWrite)
        {
            try { state.SaveTo(workspace ?? services.LiveStatePath); }
            catch (Exception ex) { stderr.WriteLine($"warning: could not save results: {ex.Message}"); }
        }
        else if ((capturedAny || tokensCaptured) && guiBlocksLiveWrite)
        {
            stderr.WriteLine("note: the GUI is running — captured values were not saved (it would overwrite them on close).");
        }

        int passed = results.Count(r => Passed(r.Model, r.Response));
        int failed = results.Count - passed;

        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                results = results.Select(r => new
                {
                    path = r.Path,
                    method = r.Model.Method,
                    url = r.Model.EffectiveUrl(),
                    status = r.Response.StatusCode,
                    elapsedMs = Math.Round(r.Response.Elapsed.TotalMilliseconds),
                    sizeBytes = r.Response.Body.LongLength,
                    passed = Passed(r.Model, r.Response),
                    assertions = r.Model.Assertions.Any(a => a.Enabled)
                        ? AssertionEvaluator.Evaluate(r.Model.Assertions, r.Response)
                            .Select(a => new { a.Description, a.Passed, actual = a.Actual })
                        : null,
                    error = r.Response.Error?.Message
                }),
                summary = new { total = results.Count, passed, failed, elapsedMs = clock.ElapsedMilliseconds }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            foreach (var (path, model, r) in results)
            {
                string verdict = Passed(model, r) ? "PASS" : "FAIL";
                string status = r.Error is not null ? "ERR" : r.StatusCode?.ToString() ?? "—";
                int assertCount = model.Assertions.Count(a => a.Enabled);
                string detail = r.Error is not null ? $"  ({r.Error.Message})"
                    : assertCount > 0 ? $"  ({assertCount} assertion{(assertCount == 1 ? "" : "s")})" : "";
                stdout.WriteLine(
                    $"{verdict}  {status,4}  {r.Elapsed.TotalMilliseconds,6:F0} ms  {OutputText.Size(r.Body.LongLength),9}  {path}{detail}");
            }
            stdout.WriteLine($"----\n{results.Count} request{(results.Count == 1 ? "" : "s")} · {passed} passed · {failed} failed · {clock.Elapsed.TotalSeconds:F1} s");
        }

        return failed == 0 ? ExitCodes.Ok : ExitCodes.Failure;
    }

    /// <summary>A request passes when its enabled assertions all pass; with no assertions it falls
    /// back to the historical "a 2xx response is a pass" behaviour.</summary>
    private static bool Passed(RequestModel m, ApiResponse r) =>
        m.Assertions.Any(a => a.Enabled) ? AssertionEvaluator.AllPass(m.Assertions, r) : r.IsSuccess;

    private static (ApiResponse Response, string Url) Execute(
        string path, RequestModel m, AppState state, bool noAutoToken,
        Dictionary<string, string> vars, bool strictVars, System.Net.CookieContainer? cookies,
        TextWriter stderr, CliServices services)
    {
        var unresolved = new List<string>();
        string R(string s)
        {
            var (resolved, missing) = VariableResolver.Resolve(s ?? "", vars);
            foreach (var x in missing) if (!unresolved.Contains(x)) unresolved.Add(x);
            return resolved;
        }

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var h in m.Headers)
            if (h.Enabled && !string.IsNullOrWhiteSpace(h.Name))
                headers.Add(new(R(h.Name.Trim()), R(h.Value ?? "")));
        switch (m.AuthType)
        {
            case "Bearer" when !string.IsNullOrWhiteSpace(m.AuthSecret):
                headers.Add(new("Authorization", "Bearer " + R(m.AuthSecret!.Trim())));
                break;
            case "Basic":
                headers.Add(new("Authorization", "Basic " +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{R(m.AuthUser ?? "")}:{R(m.AuthSecret ?? "")}"))));
                break;
        }
        var winAuth = m.AuthType == "Windows"
            ? WindowsAuthOptions.FromCredentials(R(m.AuthUser ?? ""), R(m.AuthSecret ?? ""))
            : null;
        string url = R(m.EffectiveUrl());
        string? body = string.IsNullOrEmpty(m.Body) ? null : R(m.Body!);

        if (!noAutoToken && m.AuthType == "Auto" &&
            TokenService.AutoAttach(state, url, headers, out _) is { } used)
        {
            stderr.WriteLine($"{path}: using captured token for {TokenService.HostOf(url)}");
            services.Log.Debug($"{path}: auto token attached for {used.Origin} ({used.Source})");
        }

        if (unresolved.Count > 0)
        {
            var tokens = string.Join(", ", unresolved.Select(u => "{{" + u + "}}"));
            if (strictVars)
                return (new ApiResponse { Error = new ApiError(ApiErrorKind.Unknown, $"unresolved variables: {tokens}") }, url);
            stderr.WriteLine($"warning: unresolved variables: {tokens}");
        }

        System.Security.Cryptography.X509Certificates.X509Certificate2? cert = null;
        if (!string.IsNullOrEmpty(m.CertThumbprint))
        {
            cert = services.FindCertificate(m.CertThumbprint!);
            if (cert is null)
                return (new ApiResponse { Error = new ApiError(ApiErrorKind.Unknown, $"certificate {m.CertThumbprint} not found in the store") }, url);
        }

        var request = new ApiRequest
        {
            Method = new HttpMethod(m.Method),
            Url = url,
            Headers = headers,
            Body = m.IsMultipart ? null : body,
            Parts = m.IsMultipart
                ? m.EnabledParts().Select(p => p with { Name = R(p.Name), Value = p.Value is null ? null : R(p.Value) }).ToList()
                : null,
            ContentType = !m.IsMultipart && body is not null && m.ContentType != "(none)" ? m.ContentType : null,
            WindowsAuth = winAuth,
            Timeout = TimeSpan.FromSeconds(m.TimeoutSeconds)
        };
        // Attach any browser-captured session cookies for this origin, on top of the optional
        // shared --cookies jar (honors --no-auto-token and the workspace's AutoCookies switch).
        var effectiveJar = cookies ?? new System.Net.CookieContainer();
        if (!noAutoToken) CookieService.SeedContainer(state, url, effectiveJar);
        var response = services.Client.SendAsync(request, cert, m.IgnoreServerCert,
            cookies: effectiveJar, cancellationToken: services.Cancel).GetAwaiter().GetResult();
        services.Log.Debug($"{path}: " + (response.Error is null
            ? $"{response.StatusCode} · {response.Elapsed.TotalMilliseconds:F0} ms"
            : $"[{response.Error.Kind}] {response.Error.Message}"));
        return (response, url);
    }
}
