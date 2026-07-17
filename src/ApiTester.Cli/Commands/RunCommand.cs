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
        suite; a request path runs that one request. Pass = a 2xx response.

        Options:
          --all                   Run every saved request in the workspace
          --workspace <file>      Load collections from a workspace file (default: live GUI state)
          --env <name>            Environment for {{variables}}; --var k=v overrides (repeatable)
          --record / --no-record  Write known-good results back (default: on for live state,
                                  off for workspace files; skipped while the GUI is running)
          --strict-vars           Unresolved {{tokens}} fail the request
          --json                  JSON results instead of the table

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
        var positionals = args.Positionals();
        if (positionals.Count > 1 || (positionals.Count == 0 && !all)) throw new CliUsageException(Help);

        var state = CliWorkspace.Load(workspace, services.LiveStatePath);
        var targets = CliWorkspace.ResolveTargets(state, positionals.FirstOrDefault(), all);
        var vars = CliWorkspace.BuildVars(state, envName, varOverrides);

        bool record = !noRecord && (workspace is null || recordFlag);
        if (record && workspace is null && services.IsGuiRunning())
        {
            record = false;
            stderr.WriteLine("note: the GUI is running — results were not recorded (it would overwrite them on close).");
        }

        var results = new List<(string Path, RequestModel Model, ApiResponse Response)>();
        var clock = Stopwatch.StartNew();
        foreach (var (path, node) in targets)
        {
            var response = Execute(node.Request!, vars, strictVars, stderr, services);
            results.Add((path, node.Request!, response));
            if (record) node.RecordResult(response.Error is null ? response.StatusCode : null, DateTime.UtcNow);
        }
        clock.Stop();

        if (record)
        {
            try { state.SaveTo(workspace ?? services.LiveStatePath); }
            catch (Exception ex) { stderr.WriteLine($"warning: could not record results: {ex.Message}"); }
        }

        int passed = results.Count(r => r.Response.IsSuccess);
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
                    passed = r.Response.IsSuccess,
                    error = r.Response.Error?.Message
                }),
                summary = new { total = results.Count, passed, failed, elapsedMs = clock.ElapsedMilliseconds }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            foreach (var (path, _, r) in results)
            {
                string verdict = r.IsSuccess ? "PASS" : "FAIL";
                string status = r.Error is not null ? "ERR" : r.StatusCode?.ToString() ?? "—";
                string detail = r.Error is not null ? $"  ({r.Error.Message})" : "";
                stdout.WriteLine(
                    $"{verdict}  {status,4}  {r.Elapsed.TotalMilliseconds,6:F0} ms  {OutputText.Size(r.Body.LongLength),9}  {path}{detail}");
            }
            stdout.WriteLine($"----\n{results.Count} request{(results.Count == 1 ? "" : "s")} · {passed} passed · {failed} failed · {clock.Elapsed.TotalSeconds:F1} s");
        }

        return failed == 0 ? ExitCodes.Ok : ExitCodes.Failure;
    }

    private static ApiResponse Execute(
        RequestModel m, Dictionary<string, string> vars, bool strictVars, TextWriter stderr, CliServices services)
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
        string url = R(m.EffectiveUrl());
        string? body = string.IsNullOrEmpty(m.Body) ? null : R(m.Body!);

        if (unresolved.Count > 0)
        {
            var tokens = string.Join(", ", unresolved.Select(u => "{{" + u + "}}"));
            if (strictVars)
                return new ApiResponse { Error = new ApiError(ApiErrorKind.Unknown, $"unresolved variables: {tokens}") };
            stderr.WriteLine($"warning: unresolved variables: {tokens}");
        }

        System.Security.Cryptography.X509Certificates.X509Certificate2? cert = null;
        if (!string.IsNullOrEmpty(m.CertThumbprint))
        {
            cert = services.FindCertificate(m.CertThumbprint!);
            if (cert is null)
                return new ApiResponse { Error = new ApiError(ApiErrorKind.Unknown, $"certificate {m.CertThumbprint} not found in the store") };
        }

        var request = new ApiRequest
        {
            Method = new HttpMethod(m.Method),
            Url = url,
            Headers = headers,
            Body = body,
            ContentType = body is not null && m.ContentType != "(none)" ? m.ContentType : null,
            Timeout = TimeSpan.FromSeconds(m.TimeoutSeconds)
        };
        return services.Client.SendAsync(request, cert, m.IgnoreServerCert,
            cancellationToken: services.Cancel).GetAwaiter().GetResult();
    }
}
