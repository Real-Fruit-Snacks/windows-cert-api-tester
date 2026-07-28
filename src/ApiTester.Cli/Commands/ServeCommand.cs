using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
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
          --upstream <p>=<url>    Mount an upstream at path prefix <p>, repeatable: with
                                  /api=https://api.internal a GET /api/orders reaches
                                  https://api.internal/orders. Longest prefix wins, and a path
                                  under no prefix is a 404 that contacts nothing. The positional
                                  <upstream> above is the same thing written /=<url>
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
          --tls                   Serve over HTTPS on 127.0.0.1 with a generated gateway certificate,
                                  so Secure and __Host-/__Secure- cookies work. Binding the port needs
                                  an elevated prompt the first time; the exact netsh command is printed
                                  if it isn't available
          --tls-trust             Also install that certificate into CurrentUser\Root so the browser
                                  accepts it silently (explicit, logged, and reversible)
          --tls-untrust           Remove a previously trusted gateway certificate and exit

        Browser (so a page on another origin can call the upstream through the gateway):
          --browser               The bundle: turns on all four accommodations below at once
          --cors [<origins>]      Answer CORS preflights here and add the response headers a
                                  browser needs. With no value the caller's own Origin is
                                  echoed; a comma-separated list allows only those origins and
                                  refuses the rest with 403
          --cors-max-age <n>      How long a browser may cache a preflight answer, in seconds
                                  (default 600). Only with --cors
          --rewrite-cookies       Set-Cookie loses Domain= and Secure, and SameSite=None becomes
                                  Lax, so the browser can store the cookie against the gateway
          --rewrite-location      A 3xx Location pointing at the upstream comes back pointing at
                                  this gateway (one pointing elsewhere is left alone and logged)
          --allow-upgrade         Relay WebSocket connections to the upstream as well

          Without these the gateway stays a byte-faithful relay: nothing is added or rewritten.
          A cookie named __Host-… or __Secure-… needs the Secure attribute, which a plaintext
          http://127.0.0.1 origin cannot carry, so run with --tls and it works; without --tls
          such cookies are relayed and logged as a warning.

          Chrome's Private Network Access (PNA) check is a further gate before a page on a
          public origin can reach a private or loopback address: its preflight carries
          Access-Control-Request-Private-Network: true, and the gateway answers
          Access-Control-Allow-Private-Network: true only when --cors is on and the request's
          Origin passes that same allowlist. Letting a public origin reach a loopback service is
          a real exposure, which is why that answer follows the allowlist, and why naming the
          origins you develop from with --cors <origins> is the safer form than leaving it
          echoing whoever asks.

        Headers (apply to forwarded HTTP traffic, with or without --browser — a header rule is not
        a browser concern):
          --request-header "Name: value"    Set a header on the request before it reaches the
                                             upstream: replaces it if the caller already sent one,
                                             adds it if not. Repeatable
          --remove-request-header <name>    Strip a header from the request before it reaches the
                                             upstream. Repeatable. Removal wins over
                                             --request-header naming the same header
          --response-header "Name: value"   Set a header on the response before it reaches the
                                             caller, the same replace-or-add rule as above.
                                             Repeatable, and applied after --browser's own rewrites
                                             (CORS, cookies, Location), so a header you set here
                                             wins over one the gateway injected
          --remove-response-header <name>   Strip a header from the response before it reaches the
                                             caller. Repeatable. Removal wins over
                                             --response-header naming the same header

          Connection, Keep-Alive, Transfer-Encoding, Content-Length, TE, Trailer, Upgrade,
          Proxy-Authenticate, Proxy-Authorization and Host are refused with a usage error rather
          than silently ignored: the first nine frame the HTTP message and the HTTP stack manages
          them, and Host is set by the gateway's HTTP client from the upstream URI, so a rule here
          would only ever half-apply.

          A missing header name, or one carrying a character an HTTP field name cannot hold — a
          space, an embedded colon — is refused the same way: the header could never match, so the
          rule would be dropped rather than applied.

          These rules act on forwarded HTTP traffic only: never on a CORS/PNA preflight the gateway
          answers itself, its own 404/400/502 error pages, or a relayed WebSocket upgrade. With
          none of the four flags the gateway stays the byte-faithful relay it has always been.


        """ + TransportFlags.Help + """


          Only the proxy options and --resolve change how the gateway reaches an upstream. The
          redirect, decompression and HTTP-version flags are accepted but do not apply: the
          gateway always hands back the 3xx and the exact bytes the upstream sent.

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi serve https://internal-api.example.com --cert "CN=My Client" --port 8443
          certapi serve https://internal-api.example.com --cert "CN=My Client" --port 8443 --insecure

          # Two upstreams behind one port, with every browser accommodation on
          certapi serve --port 8443 --cert "CN=My Client" --browser --upstream /api=https://api.internal --upstream /auth=https://login.internal

          # Just CORS, and only for the page you are developing
          certapi serve https://internal-api.example.com --port 8443 --cert "CN=My Client" --cors http://localhost:3000

          # HTTPS on the gateway itself, so Secure and __Host-/__Secure- cookies survive
          certapi serve https://internal-api.example.com --port 8443 --cert "CN=My Client" --browser --tls --tls-trust

          # Inject an API key the calling app never has to know
          certapi serve https://internal-api.example.com --port 8443 --cert "CN=My Client" --request-header "X-Api-Key: s3cret"

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
        var upstreamSpecs = args.Values("--upstream");
        // --browser is a bundle over the four accommodations, each of which still works on its own;
        // the flag is read first so every one of them is consumed either way.
        bool browserBundle = args.Flag("--browser");
        bool cors = args.OptionalValue(out var corsOrigins, "--cors") || browserBundle;
        bool rewriteCookies = args.Flag("--rewrite-cookies") || browserBundle;
        bool rewriteLocation = args.Flag("--rewrite-location") || browserBundle;
        bool allowUpgrade = args.Flag("--allow-upgrade") || browserBundle;
        string? corsMaxAgeRaw = args.Value("--cors-max-age");
        // Header rules are not a browser concern (they work with or without --browser), but they are
        // read alongside the browser flags here so every repeatable option is consumed before
        // Positionals() runs.
        var requestHeaderSets = args.Values("--request-header");
        var requestHeaderRemovals = args.Values("--remove-request-header");
        var responseHeaderSets = args.Values("--response-header");
        var responseHeaderRemovals = args.Values("--remove-response-header");
        var transportOverrides = TransportFlags.Parse(args, out _);

        bool tls = args.Flag("--tls");
        bool tlsTrust = args.Flag("--tls-trust");
        bool tlsUntrust = args.Flag("--tls-untrust");

        if (tlsUntrust)
        {
            // A standalone maintenance action: it removes a certificate this tool installed and has
            // nothing to do with starting a listener.
            if (tls || tlsTrust)
                throw new CliUsageException("--tls-untrust is a standalone action; run 'certapi serve --tls-untrust' on its own.");
            return UntrustGatewayCertificate(stderr);
        }
        if (tlsTrust && !tls)
            throw new CliUsageException("--tls-trust only applies together with --tls.");

        // Resolve the certificate before Positionals() rejects its options (store or a file). The
        // standalone --tls-untrust path above returns before this, so it never touches the store to
        // resolve a client certificate it will never use.
        var cert = CliCert.Resolve(args, store, services, stderr);

        var positionals = args.Positionals();
        // The positional upstream is sugar for --upstream /=<url>, so one form, the other, or both
        // is fine — but a gateway with nothing to forward to, or a second bare URL with no mount
        // point to tell it apart from the first, has no sensible reading.
        if (positionals.Count > 1) throw new CliUsageException(Help);
        if (positionals.Count == 0 && upstreamSpecs.Count == 0) throw new CliUsageException(Help);
        if (portRaw is null) throw new CliUsageException("serve needs --port <n>.");
        if (!int.TryParse(portRaw, out int port) || port is < 1 or > 65535)
            throw new CliUsageException($"--port expects 1-65535, got '{portRaw}'.");

        int timeoutSeconds = 100;
        if (timeoutRaw is not null && (!int.TryParse(timeoutRaw, out timeoutSeconds) || timeoutSeconds <= 0))
            throw new CliUsageException($"--timeout expects a positive number of seconds, got '{timeoutRaw}'.");

        int corsMaxAge = BrowserOptions.DefaultCorsMaxAgeSeconds;
        if (corsMaxAgeRaw is not null)
        {
            if (!int.TryParse(corsMaxAgeRaw, out corsMaxAge) || corsMaxAge < 0)
                throw new CliUsageException(
                    $"--cors-max-age expects a whole number of seconds (0 or more), got '{corsMaxAgeRaw}'.");
            if (!cors)
                throw new CliUsageException("--cors-max-age only applies together with --cors (or --browser).");
        }

        // Built here, before the listener binds or the gateway is constructed, rather than lazily on
        // the first request that would need them: a refused header is a mistake in the command line,
        // and the operator should learn about it from a usage error, not by discovering it later from
        // traffic that already went through.
        var requestSets = ParseHeaderSets(requestHeaderSets, "--request-header");
        var requestRemovals = ParseHeaderRemovals(requestHeaderRemovals, "--remove-request-header");
        var responseSets = ParseHeaderSets(responseHeaderSets, "--response-header");
        var responseRemovals = ParseHeaderRemovals(responseHeaderRemovals, "--remove-response-header");
        var headerRules = HeaderRules.TryCreate(
                              requestSets, requestRemovals, responseSets, responseRemovals,
                              out var headerRuleProblem)
                          ?? throw new CliUsageException(headerRuleProblem!);

        var routeList = new List<GatewayRoute>();
        // Resolve the positional upstream: an absolute http(s) URL, or a saved-website name.
        if (positionals.Count == 1)
            routeList.Add(new GatewayRoute("/", ResolveUpstream(positionals[0], workspace, services, stderr)));
        foreach (var spec in upstreamSpecs) routeList.Add(ParseUpstream(spec));

        GatewayRoutes routes;
        // Two upstreams on one mount point is a configuration mistake with no right answer, so it is
        // refused here rather than quietly sending that prefix's traffic to whichever came last.
        try { routes = new GatewayRoutes(routeList); }
        // The parameter-name suffix ArgumentException appends is about the API it guards, not about
        // the command line the user typed; the sentence before it is the part they need.
        catch (ArgumentException ex) { throw new CliUsageException(ex.Message.Split(" (Parameter")[0]); }

        // Only the settings that decide how the gateway *reaches* an upstream matter here; the rest
        // are accepted (the shared parser consumes them) and documented as not applying. Validated
        // per route before the listener binds, so an unusable combination is a usage error rather
        // than a setting silently dropped on the floor.
        var transport = transportOverrides.ApplyTo(new TransportOptions());
        foreach (var route in routes.All)
            if (ApiClient.ValidateTransport(transport, route.Upstream.ToString()) is { } transportProblem)
                throw new CliUsageException(transportProblem);

        var logLock = new object();
        void Log(string line) { if (!quiet) lock (logLock) stderr.WriteLine(line); }

        // The origin the browser is actually talking to, which is what a rewritten Location and an
        // allow-origin decision are measured against. https when --tls is on, so Location rewriting
        // targets the right scheme and Secure/SameSite=None survive as the upstream sent them.
        string scheme = tls ? "https" : "http";
        var browser = new BrowserOptions(
            cors, ParseOrigins(corsOrigins), rewriteCookies, rewriteLocation, allowUpgrade,
            LocalOrigin: new Uri($"{scheme}://127.0.0.1:{port}")) { SecureOrigin = tls, CorsMaxAgeSeconds = corsMaxAge };

        // Pins come from the same state file saved-website names resolve against; loaded here (not
        // lazily) because the gateway's upstream connections need them for their whole lifetime.
        // With this, an upstream pinned via `certapi trust add` is reachable without --insecure —
        // the gateway was the last connection in the product that could not say that.
        var trustState = CliWorkspace.Load(workspace, services.LiveStatePath, stderr);
        var trustPredicates = new TrustPredicates(trustState);
        using var gateway = services.GatewayFactory(
            routes, cert, insecure, TimeSpan.FromSeconds(timeoutSeconds), transport, trustPredicates.For);
        // An upgrade cannot go through the gateway at all — HttpClient cannot carry one — so it
        // gets its own relay, built only when upgrades are actually allowed.
        var relay = allowUpgrade ? new GatewayWebSocketRelay(cert, insecure) : null;

        X509Certificate2? gatewayCert = null;
        string? gatewayThumb = null;
        if (tls)
        {
            gatewayCert = GatewayCertificate.LoadOrCreate();
            gatewayThumb = LoopbackTlsBinding.NormalizeThumbprint(gatewayCert.Thumbprint!);

            if (tlsTrust)
            {
                // Explicit, logged, and reversible — never done silently.
                try { GatewayCertificate.Trust(gatewayCert); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Could not install the gateway certificate into CurrentUser\\Root.", ex);
                }
                Log($"trusted the gateway certificate {gatewayThumb} in CurrentUser\\Root (undo with certapi serve --tls-untrust).");
            }

            var bound = LoopbackTlsBinding.Describe(port);
            if (bound is null)
            {
                if (!LoopbackTlsBinding.IsElevated())
                    throw new CliUsageException(LoopbackTlsBinding.NotElevatedMessage(port, gatewayThumb));
                var result = LoopbackTlsBinding.Bind(port, gatewayCert);
                Log($"bound the gateway certificate {result.Thumbprint} to 127.0.0.1:{port}");
                Log($"  {result.Command}");
            }
            else if (!bound.Thumbprint.Equals(gatewayThumb, StringComparison.OrdinalIgnoreCase))
            {
                // Someone else's binding: report it and stop, never replace it.
                throw new CliUsageException(LoopbackTlsBinding.AlreadyBoundMessage(port, bound));
            }
            else
            {
                Log($"reusing the certificate already bound to 127.0.0.1:{port} ({bound.Thumbprint}).");
            }
        }

        var listener = new HttpListener();
        listener.Prefixes.Add($"{scheme}://127.0.0.1:{port}/");
        try { listener.Start(); }
        catch (HttpListenerException ex) { throw new CliDataException($"Could not listen on port {port}: {ex.Message}"); }

        Log($"listening on {scheme}://127.0.0.1:{port}   (cert: {cert?.Subject ?? "none"})");
        if (tls)
        {
            Log(tlsTrust || GatewayCertificate.IsTrusted(gatewayThumb!)
                ? "the gateway certificate is trusted in CurrentUser\\Root, so the browser accepts it silently."
                : "the gateway certificate is self-signed; the browser will warn once. Run with " +
                  "--tls-trust to install it into CurrentUser\\Root (reversible with --tls-untrust).");
        }
        foreach (var route in routes.All) Log($"  {route.PathPrefix}  ->  {route.Upstream}");

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

                var task = HandleAsync(context, gateway, routes, browser, headerRules, relay, token, Log, ct);
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
            gatewayCert?.Dispose();
        }

        Log("stopped.");
        return ExitCodes.Ok;
    }

    /// <summary>`serve --tls-untrust`: remove a previously trusted gateway certificate and exit. A
    /// standalone maintenance action — it never generates a certificate or binds a port, so it reads
    /// only the cached one on disk, if any.</summary>
    private static int UntrustGatewayCertificate(TextWriter stderr)
    {
        using var cert = GatewayCertificate.TryLoadCached();
        if (cert is null)
        {
            stderr.WriteLine("no gateway certificate is cached, so there is nothing to untrust.");
            return ExitCodes.Ok;
        }

        string thumb = LoopbackTlsBinding.NormalizeThumbprint(cert.Thumbprint!);
        if (GatewayCertificate.Untrust(thumb))
            stderr.WriteLine($"removed the gateway certificate {thumb} from CurrentUser\\Root.");
        else
            stderr.WriteLine($"the gateway certificate {thumb} was not in CurrentUser\\Root; nothing to remove.");
        return ExitCodes.Ok;
    }

    /// <summary>Parses a repeatable "Name: value" flag using the same first-colon rule as the request
    /// commands: a header value is allowed to contain a colon of its own, so only the first one
    /// separates the name from the value.</summary>
    private static List<KeyValuePair<string, string>> ParseHeaderSets(IReadOnlyList<string> raw, string flag)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var entry in raw)
        {
            int colon = entry.IndexOf(':');
            if (colon <= 0) throw new CliUsageException($"{flag} expects \"Name: value\", got '{entry}'.");
            result.Add(new(entry[..colon].Trim(), entry[(colon + 1)..].Trim()));
        }
        return result;
    }

    /// <summary>Parses a repeatable bare-header-name flag: an empty or whitespace-only name is a
    /// usage error rather than a removal rule that silently matches nothing.</summary>
    private static List<string> ParseHeaderRemovals(IReadOnlyList<string> raw, string flag)
    {
        var result = new List<string>();
        foreach (var entry in raw)
        {
            string name = entry.Trim();
            if (name.Length == 0) throw new CliUsageException($"{flag} needs a header name.");
            result.Add(name);
        }
        return result;
    }

    /// <summary>One --upstream &lt;prefix&gt;=&lt;url&gt; mount. The URL must be spelled out in full: a mount
    /// point is a routing decision, and resolving a saved-website name per prefix would make it
    /// ambiguous which half of the spec a bare word belonged to.</summary>
    private static GatewayRoute ParseUpstream(string spec)
    {
        // The first '=' only: a URL is allowed to contain more of them, in a query string.
        int eq = spec.IndexOf('=');
        if (eq <= 0)
            throw new CliUsageException(
                $"--upstream expects <prefix>=<url> (for example /api=https://api.internal), got '{spec}'.");

        string prefix = spec[..eq].Trim();
        string url = spec[(eq + 1)..].Trim();
        if (prefix.Length == 0)
            throw new CliUsageException($"--upstream needs a path prefix before the '=', got '{spec}'.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var upstream) ||
            (upstream.Scheme != Uri.UriSchemeHttp && upstream.Scheme != Uri.UriSchemeHttps))
            throw new CliUsageException($"--upstream needs an absolute http(s) URL after the '=', got '{spec}'.");

        // Scheme + host + port only, as the positional form is: the forwarded path always replaces
        // whatever path was written here, so keeping one would only misdescribe where traffic goes.
        return new GatewayRoute(prefix, new Uri(upstream.GetLeftPart(UriPartial.Authority)));
    }

    private static Uri ResolveUpstream(string value, string? workspace, CliServices services, TextWriter stderr)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var abs) &&
            (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
            return new Uri(abs.GetLeftPart(UriPartial.Authority));   // scheme + host + port only

        var state = CliWorkspace.Load(workspace, services.LiveStatePath, stderr);
        var saved = state.SavedBaseUrls.FirstOrDefault(b =>
            b.Equals(value, StringComparison.OrdinalIgnoreCase) ||
            (Uri.TryCreate(b, UriKind.Absolute, out var u) && u.Host.Equals(value, StringComparison.OrdinalIgnoreCase)));
        if (saved is null || !Uri.TryCreate(saved, UriKind.Absolute, out var savedUri))
            throw new CliDataException($"'{value}' is not an absolute URL or a saved website. Use https://… or a saved website name.");
        return new Uri(savedUri.GetLeftPart(UriPartial.Authority));
    }

    /// <summary>The origins named by --cors, or null when it was given without a value — which means
    /// "echo whoever asks", the loopback-only default.</summary>
    private static IReadOnlyList<string>? ParseOrigins(string? raw) =>
        raw is null
            ? null
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Relay a WebSocket upgrade to the matched route's upstream. This path never enters
    /// MtlsGateway.ForwardAsync — HttpClient cannot carry an upgrade — so the plain HTTP relay keeps
    /// its exact semantics.</summary>
    private static async Task RelayUpgradeAsync(
        HttpListenerContext context, GatewayRoutes routes, GatewayWebSocketRelay relay,
        Action<string> log, CancellationToken ct)
    {
        var req = context.Request;
        string method = req.HttpMethod;
        string pathAndQuery = req.RawUrl ?? "/";

        var route = routes.Resolve(pathAndQuery, out var forwardedPathAndQuery);
        if (route is null)
        {
            // Nothing is contacted and nothing is upgraded: there is no upstream under this path.
            try { context.Response.StatusCode = 404; context.Response.Close(); } catch { }
            log($"{method,-6} {pathAndQuery}  -> 404 (no route)");
            return;
        }

        // The browser's own choice, passed on so the negotiated subprotocol means the same thing at
        // both ends of the relay. The first when several are offered — picking is the upstream's job.
        string? subProtocol = req.Headers["Sec-WebSocket-Protocol"]?.Split(',')[0].Trim();
        if (string.IsNullOrEmpty(subProtocol)) subProtocol = null;

        var upstream = GatewayWebSocketRelay.ToWebSocketUri(new Uri(route.Upstream, forwardedPathAndQuery));
        HttpListenerWebSocketContext accepted;
        // Still an ordinary HTTP exchange until the accept succeeds, so a refusal is an ordinary
        // HTTP answer rather than a close frame.
        try { accepted = await context.AcceptWebSocketAsync(subProtocol); }
        catch (Exception ex)
        {
            try { context.Response.StatusCode = 400; context.Response.Close(); } catch { }
            log($"{method,-6} {pathAndQuery}  -> 400 (upgrade refused: {ex.Message})");
            return;
        }

        try
        {
            await relay.RelayAsync(accepted.WebSocket, upstream, subProtocol, ct);
            log($"{method,-6} {pathAndQuery}  -> 101 (websocket closed)");
        }
        catch (Exception ex)
        {
            // The relay has already closed the browser with 1011 and told it why, so there is
            // nothing left to send: the reason is only worth writing down.
            log($"{method,-6} {pathAndQuery}  -> websocket failed ({ex.Message})");
        }
        // The socket belongs to the context that accepted it, so it is disposed here; the relay
        // never disposes what it was handed.
        finally { accepted.WebSocket.Dispose(); }
    }

    private static async Task HandleAsync(
        HttpListenerContext context, MtlsGateway gateway, GatewayRoutes routes,
        BrowserOptions browser, HeaderRules headerRules, GatewayWebSocketRelay? relay, string? token,
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

            // Without --allow-upgrade an upgrade request falls through to the plain relay exactly as
            // it always has, so nothing changes for callers who never asked for WebSockets.
            if (relay is not null && browser.AllowUpgrade && req.IsWebSocketRequest)
            {
                await RelayUpgradeAsync(context, routes, relay, log, ct);
                return;
            }

            var headers = new List<KeyValuePair<string, string>>();
            foreach (string? key in req.Headers.AllKeys)
                if (key is not null)
                    foreach (var v in req.Headers.GetValues(key) ?? Array.Empty<string>())
                        headers.Add(new(key, v));

            var gwReq = new GatewayRequest(method, pathAndQuery, headers,
                req.HasEntityBody ? req.InputStream : null, req.ContentType);

            // A preflight is the browser asking the gateway's own permission, so the gateway answers
            // it. Forwarding it would ask the upstream a question about a policy that is not theirs.
            if (BrowserRewriter.TryPreflight(gwReq, browser) is { } preflight)
            {
                res.StatusCode = preflight.StatusCode;
                foreach (var h in preflight.Headers) res.Headers.Add(h.Key, h.Value);
                res.Close();
                log($"{method,-6} {pathAndQuery}  -> {preflight.StatusCode} (preflight)");
                return;
            }

            // The rules describe what reaches the upstream, so they are applied to the forwarded
            // request and not to the preflight decision above — a preflight is answered here and
            // never forwarded, and a user rule must not be able to change the Origin that decision is
            // made on. With no request rules this hands back the very same header list, so the
            // forwarded request is identical to today's.
            var forwardReq = gwReq with { Headers = headerRules.ApplyToRequest(headers) };

            var gwResp = await gateway.ForwardAsync(forwardReq, ct);
            using (gwResp.Lifetime)
            {
                res.StatusCode = gwResp.StatusCode;
                if (gwResp.ReasonPhrase is { } reason) res.StatusDescription = reason;

                // The forward above refused a path that matches nothing, so this route is the one
                // the response came from. With every accommodation off the rewriter hands back the
                // upstream's headers as they were, which is what keeps the default relay faithful.
                var responseHeaders = gwResp.Headers;
                if (routes.Resolve(pathAndQuery, out _) is { } route)
                {
                    responseHeaders = BrowserRewriter.RewriteResponseHeaders(
                        gwResp.Headers, req.Headers["Origin"], route, browser, out var warnings);
                    foreach (var warning in warnings) log("warning: " + warning);
                }

                // After the browser rewrites, so a header the operator configured explicitly wins
                // over one the gateway injected — someone overriding Access-Control-Allow-Origin
                // deliberately gets their value. Applied whether or not any browser accommodation is
                // on: a header rule is not a browser concern.
                responseHeaders = headerRules.ApplyToResponse(responseHeaders);

                long? contentLength = null;
                foreach (var h in responseHeaders)
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
        catch (GatewayRouteNotFoundException ex)
        {
            // Nothing was contacted: there is no upstream mounted under this path to contact.
            try
            {
                res.StatusCode = 404;
                var bytes = System.Text.Encoding.UTF8.GetBytes("404 Not Found: " + ex.Message);
                res.ContentType = "text/plain";
                res.ContentLength64 = bytes.Length;
                await res.OutputStream.WriteAsync(bytes, ct);
                res.Close();
            }
            catch { /* client already gone */ }
            log($"{method,-6} {pathAndQuery}  -> 404 (no route)");
            return;
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
