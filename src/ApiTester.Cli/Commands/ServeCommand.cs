using System.Net;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class ServeCommand
{
    public const string Help = """
        Usage: certapi serve <upstream> --port <n> [options]

        Runs a local reverse proxy on http://127.0.0.1:<port> that forwards every request to
        <upstream> with your Windows-store client certificate attached. Point an app's base URL
        at the local port and it reaches a certificate-protected site with no mTLS code of its own.

          <upstream>              A full https:// URL, or the name of a saved website
          --port <n>              Local port to listen on (127.0.0.1 only)
          --cert <thumb|subject>  Client certificate from the Windows store
          --store <location>      CurrentUser (default); LocalMachine searches both stores
          --cert-file <path>      Client certificate from a file (.pfx/.p12 or .pem/.crt) instead
          --cert-password <pw>    Password for a .pfx/.p12 certificate file
          --key-file <path>       Private-key file for a PEM cert whose key is separate
          --insecure              Ignore upstream server-certificate errors (internal CAs)
          --token <value>         Require callers to send this token (Authorization: Bearer
                                  <value> or X-Certapi-Token: <value>); off by default
          --timeout <seconds>     Per-request upstream timeout (default 100)
          --workspace <file>      Resolve a saved-website <upstream> from a workspace file
          -q, --quiet             No startup banner or per-request log

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi serve https://internal-api.example.com --cert "CN=My Client" --port 8443
          certapi serve https://internal-api.example.com --cert "CN=My Client" --port 8443 --insecure

        Loopback only; stop with Ctrl+C. Exit 0 clean shutdown, 2 usage, 3 data error.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string? portRaw = args.Value("--port");
        string store = args.Value("--store") ?? "CurrentUser";
        bool insecure = args.Flag("--insecure");
        string? token = args.Value("--token");
        string? timeoutRaw = args.Value("--timeout");
        string? workspace = args.Value("--workspace");
        bool quiet = args.Flag("-q", "--quiet");
        // Resolve the certificate before Positionals() rejects its options (store or a file).
        var cert = CliCert.Resolve(args, store, services, stderr);

        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);
        if (portRaw is null) throw new CliUsageException("serve needs --port <n>.");
        if (!int.TryParse(portRaw, out int port) || port is < 1 or > 65535)
            throw new CliUsageException($"--port expects 1-65535, got '{portRaw}'.");

        int timeoutSeconds = 100;
        if (timeoutRaw is not null && (!int.TryParse(timeoutRaw, out timeoutSeconds) || timeoutSeconds <= 0))
            throw new CliUsageException($"--timeout expects a positive number of seconds, got '{timeoutRaw}'.");

        // Resolve the upstream: an absolute http(s) URL, or a saved-website name.
        Uri upstream = ResolveUpstream(positionals[0], workspace, services);

        using var gateway = services.GatewayFactory(upstream, cert, insecure, TimeSpan.FromSeconds(timeoutSeconds));

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try { listener.Start(); }
        catch (HttpListenerException ex) { throw new CliDataException($"Could not listen on port {port}: {ex.Message}"); }

        var logLock = new object();
        void Log(string line) { if (!quiet) lock (logLock) stderr.WriteLine(line); }

        Log($"listening on http://127.0.0.1:{port}  ->  {upstream}   (cert: {cert?.Subject ?? "none"})");

        var ct = services.Cancel;
        using var stopReg = ct.Register(() => { try { listener.Stop(); } catch { } });

        var inFlight = new System.Collections.Concurrent.ConcurrentDictionary<Task, byte>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = listener.GetContextAsync().GetAwaiter().GetResult(); }
                catch (Exception) when (ct.IsCancellationRequested) { break; }   // Stop() unblocked us

                var task = HandleAsync(context, gateway, token, Log, ct);
                inFlight[task] = 0;
                _ = task.ContinueWith(t => inFlight.TryRemove(t, out _), TaskScheduler.Default);
            }
        }
        finally
        {
            // Let requests already accepted finish before we tear down the gateway/listener.
            try { Task.WhenAny(Task.WhenAll(inFlight.Keys), Task.Delay(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult(); }
            catch { }
            try { listener.Close(); } catch { }
        }

        Log("stopped.");
        return ExitCodes.Ok;
    }

    private static Uri ResolveUpstream(string value, string? workspace, CliServices services)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var abs) &&
            (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
            return new Uri(abs.GetLeftPart(UriPartial.Authority));   // scheme + host + port only

        var state = CliWorkspace.Load(workspace, services.LiveStatePath);
        var saved = state.SavedBaseUrls.FirstOrDefault(b =>
            b.Equals(value, StringComparison.OrdinalIgnoreCase) ||
            (Uri.TryCreate(b, UriKind.Absolute, out var u) && u.Host.Equals(value, StringComparison.OrdinalIgnoreCase)));
        if (saved is null || !Uri.TryCreate(saved, UriKind.Absolute, out var savedUri))
            throw new CliDataException($"'{value}' is not an absolute URL or a saved website. Use https://… or a saved website name.");
        return new Uri(savedUri.GetLeftPart(UriPartial.Authority));
    }

    private static async Task HandleAsync(
        HttpListenerContext context, MtlsGateway gateway, string? token,
        Action<string> log, CancellationToken ct)
    {
        var req = context.Request;
        var res = context.Response;
        string method = req.HttpMethod;
        string pathAndQuery = req.RawUrl ?? "/";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (token is not null && !TokenOk(req, token))
            {
                res.StatusCode = 401;
                res.Close();
                log($"{method,-6} {pathAndQuery}  -> 401 (token)");
                return;
            }

            var headers = new List<KeyValuePair<string, string>>();
            foreach (string? key in req.Headers.AllKeys)
                if (key is not null)
                    foreach (var v in req.Headers.GetValues(key) ?? Array.Empty<string>())
                        headers.Add(new(key, v));

            var gwReq = new GatewayRequest(method, pathAndQuery, headers,
                req.HasEntityBody ? req.InputStream : null, req.ContentType);

            var gwResp = await gateway.ForwardAsync(gwReq, ct);
            using (gwResp.Lifetime)
            {
                res.StatusCode = gwResp.StatusCode;
                if (gwResp.ReasonPhrase is { } reason) res.StatusDescription = reason;
                long? contentLength = null;
                foreach (var h in gwResp.Headers)
                {
                    if (h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    { if (long.TryParse(h.Value, out var cl)) contentLength = cl; continue; }
                    try { res.Headers.Add(h.Key, h.Value); } catch (ArgumentException) { /* restricted header HttpListener manages itself — skip */ }
                }
                if (contentLength is { } len) res.ContentLength64 = len;
                else res.SendChunked = true;

                await gwResp.Body.CopyToAsync(res.OutputStream, ct);
            }
            res.Close();
            log($"{method,-6} {pathAndQuery}  -> {gwResp.StatusCode}  {sw.ElapsedMilliseconds} ms");
        }
        catch (GatewayTargetException ex)
        {
            try
            {
                res.StatusCode = 400;
                var bytes = System.Text.Encoding.UTF8.GetBytes("400 Bad Request: " + ex.Message);
                res.ContentType = "text/plain";
                res.ContentLength64 = bytes.Length;
                await res.OutputStream.WriteAsync(bytes, ct);
                res.Close();
            }
            catch { /* client already gone */ }
            log($"{method,-6} {pathAndQuery}  -> 400 (off-host target)");
            return;
        }
        catch (Exception ex)
        {
            try
            {
                res.StatusCode = 502;
                var bytes = System.Text.Encoding.UTF8.GetBytes("502 Bad Gateway: " + ex.Message);
                res.ContentType = "text/plain";
                res.ContentLength64 = bytes.Length;
                await res.OutputStream.WriteAsync(bytes, ct);
                res.Close();
            }
            catch { /* client already gone */ }
            log($"{method,-6} {pathAndQuery}  -> 502 ({ex.Message})");
        }
    }

    private static bool TokenOk(HttpListenerRequest req, string token)
    {
        string? auth = req.Headers["Authorization"];
        if (auth is not null && auth.Equals("Bearer " + token, StringComparison.Ordinal)) return true;
        string? custom = req.Headers["X-Certapi-Token"];
        return custom is not null && custom.Equals(token, StringComparison.Ordinal);
    }
}
