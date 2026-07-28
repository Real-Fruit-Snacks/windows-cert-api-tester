using ApiTester.Core;

namespace ApiTester.Cli;

/// <summary>The transport flags shared by send/run/fuzz, parsed once and applied over whatever
/// baseline the command has (defaults for send/fuzz, the saved request's own settings for run).</summary>
public static class TransportFlags
{
    /// <summary>The one description of these flags, spliced into send/run/fuzz help at compile time
    /// so the three cannot drift apart.</summary>
    public const string Help = """
        Transport:
          --proxy <url>           Route through this proxy: http(s), or socks4/socks4a/socks5.
                                  An SSH jump host works here: `ssh -D 1080 user@jump` then
                                  socks5://127.0.0.1:1080 reaches what the jump host can reach,
                                  mutual TLS intact (SOCKS relays bytes, it never ends TLS)
          --no-proxy              Ignore the system/PAC proxy — also restores TLS diagnostics
          --proxy-user <u:pass>   Proxy credentials
          --no-redirect           Do not follow 3xx redirects
          --max-redirs <n>        Redirect limit (default 20)
          --show-redirects        Print the redirect hop chain to stderr
          --no-decompress         Relay compressed bytes exactly as received
          --http1.1 / --http2     Pin the HTTP version
          --resolve <host:port:ip>  Pin a host to an address (repeatable; not valid with a proxy)
          --noproxy <list>        Hosts that bypass the proxy, comma-separated, NO_PROXY-style:
                                  internal.corp, .corp, *.corp, 10.0.0.0/8, or * for everything
                                  (append :port to pin one port). Narrows --proxy or the system
                                  proxy; not valid with --no-proxy. Defaults to the NO_PROXY
                                  environment variable, which an explicit --noproxy overrides.
          --revocation <mode>     Check whether the server's certificate has been revoked by its
                                  issuer: none (the default — no checking, as before), offline
                                  (cached certificate revocation lists (CRLs) only), or online (may
                                  fetch a fresh list or query an Online Certificate Status Protocol
                                  (OCSP) responder). A status that cannot be determined is reported
                                  and is not fatal, because a blocked revocation endpoint is the
                                  ordinary case on a corporate network.
          --revocation-strict     Make an undeterminable revocation status fatal (needs --revocation
                                  offline or online). --insecure still overrides both, and says so.

        Retries:
          --retry <n>             Retry a failed request up to n times (default 0 = off)
          --retry-on <codes>      Comma-separated statuses that earn a retry (default 429,502,503,504)
          --retry-delay <ms>      First backoff delay (default 500); doubles each attempt with
                                  ±10% jitter, capped at 30s. A Retry-After header overrides it.
          --retry-unsafe          Also retry POST/PATCH (only GET/HEAD/OPTIONS/PUT/DELETE by default,
                                  because re-sending a POST nobody confirmed can charge a card twice)
          --no-retry-transport    Don't retry connection failures and timeouts
        """;

    /// <summary>Consume the shared transport flags. <paramref name="showRedirects"/> comes back
    /// separately because printing the hop chain is an output choice, not a transport one.
    /// <paramref name="environment"/> is how a test supplies NO_PROXY without touching the process's
    /// own environment, which is global state a parallel test run must never mutate; every real call
    /// site leaves it null and gets <see cref="Environment.GetEnvironmentVariable"/>.</summary>
    public static TransportOverrides Parse(Args args, out bool showRedirects, Func<string, string?>? environment = null)
    {
        // Every option is consumed unconditionally, even when this command will not act on it:
        // Args.Positionals() rejects anything option-shaped that is left over.
        string? proxyUrl = args.Value("--proxy");
        bool noProxy = args.Flag("--no-proxy");
        string? proxyUser = args.Value("--proxy-user");
        bool noRedirect = args.Flag("--no-redirect");
        string? maxRedirsRaw = args.Value("--max-redirs");
        bool noDecompress = args.Flag("--no-decompress");
        bool http11 = args.Flag("--http1.1");
        bool http2 = args.Flag("--http2");
        var resolveSpecs = args.Values("--resolve");
        string? noProxySpec = args.Value("--noproxy");
        showRedirects = args.Flag("--show-redirects");
        string? retryRaw = args.Value("--retry");
        string? retryOnRaw = args.Value("--retry-on");
        string? retryDelayRaw = args.Value("--retry-delay");
        bool retryUnsafe = args.Flag("--retry-unsafe");
        bool noRetryTransport = args.Flag("--no-retry-transport");
        string? revocationRaw = args.Value("--revocation");
        bool revocationStrict = args.Flag("--revocation-strict");

        if (proxyUrl is not null && noProxy)
            throw new CliUsageException("--proxy and --no-proxy are mutually exclusive.");

        string? user = null, password = null;
        if (proxyUser is not null)
        {
            if (proxyUrl is null)
                throw new CliUsageException("--proxy-user needs --proxy (there is no proxy to authenticate to).");
            // First colon only: a proxy password is allowed to contain colons, and often does.
            int colon = proxyUser.IndexOf(':');
            if (colon < 0)
                throw new CliUsageException($"--proxy-user expects user:password, got '{proxyUser}'.");
            user = proxyUser[..colon];
            password = proxyUser[(colon + 1)..];
        }

        int? maxRedirects = null;
        if (maxRedirsRaw is not null)
        {
            if (!int.TryParse(maxRedirsRaw, out var n) || n < 1)
                throw new CliUsageException(
                    $"--max-redirs expects a number of at least 1, got '{maxRedirsRaw}' (use --no-redirect to stop following redirects).");
            maxRedirects = n;
        }

        if (http11 && http2)
            throw new CliUsageException("--http1.1 and --http2 are mutually exclusive.");

        var resolve = new List<ResolveOverride>();
        foreach (var raw in resolveSpecs) resolve.Add(ParseResolve(raw));

        var (noProxyRules, noProxyFromEnvironment) = ResolveNoProxy(noProxySpec, noProxy, environment);

        int? retries = null;
        if (retryRaw is not null)
        {
            if (!int.TryParse(retryRaw, out var n) || n < 0)
                throw new CliUsageException($"--retry expects a count of 0 or more, got '{retryRaw}'.");
            retries = n;
        }

        IReadOnlyList<int>? retryOn = retryOnRaw is null ? null : ParseRetryOn(retryOnRaw);

        int? retryDelayMs = null;
        if (retryDelayRaw is not null)
        {
            if (!int.TryParse(retryDelayRaw, out var ms) || ms < 0)
                throw new CliUsageException($"--retry-delay expects milliseconds of 0 or more, got '{retryDelayRaw}'.");
            retryDelayMs = ms;
        }

        // Parsed strictly, the same stance --noproxy and --retry-on take above: a typo'd mode is
        // refused by name rather than silently dropped, because a dropped --revocation would leave
        // the user believing they asked for a check they never got. Note this does NOT also refuse
        // --revocation-strict without a mode here — that combination is only invalid once a saved
        // request's own baseline is accounted for (certapi run applies these overrides ON TOP of a
        // saved request, so --revocation-strict alone is valid when the saved request already
        // carries a mode), so ApiClient.ValidateTransport owns that check, after ApplyTo below.
        RevocationMode? revocation = revocationRaw is null ? null : ParseRevocation(revocationRaw);

        return new TransportOverrides
        {
            Proxy = proxyUrl is not null ? ProxyMode.Explicit : noProxy ? ProxyMode.None : null,
            ProxyUrl = proxyUrl,
            ProxyUser = user,
            ProxyPassword = password,
            FollowRedirects = noRedirect ? false : null,
            MaxRedirects = maxRedirects,
            Decompress = noDecompress ? false : null,
            Version = http11 ? HttpVersionMode.Http11 : http2 ? HttpVersionMode.Http2 : null,
            Resolve = resolve,
            NoProxy = noProxyRules,
            NoProxyFromEnvironment = noProxyFromEnvironment,
            Retries = retries,
            RetryOn = retryOn,
            RetryDelayMs = retryDelayMs,
            RetryUnsafeMethods = retryUnsafe ? true : null,
            RetryOnTransportError = noRetryTransport ? false : null,
            Revocation = revocation,
            RevocationStrict = revocationStrict ? true : null
        };
    }

    /// <summary>The help block for the stream-transport subset — what `ws`, `sse`, and `token`
    /// carry. Spliced into their help the way <see cref="Help"/> is spliced into send/run/fuzz, so
    /// the three cannot drift from each other or from this parser.</summary>
    public const string StreamHelp = """
        Transport:
          --proxy <url>           Route through this proxy: http(s), or socks4/socks4a/socks5
                                  (an `ssh -D` tunnel is a socks5 proxy)
          --no-proxy              Ignore the system/PAC proxy
          --proxy-user <u:pass>   Proxy credentials
          --noproxy <list>        Hosts that bypass the proxy, comma-separated, NO_PROXY-style
                                  (an explicit list overrides the NO_PROXY environment variable)
          --revocation <mode>     Check whether the server's certificate has been revoked:
                                  none (default), offline, or online — same rules as `send`
          --revocation-strict     Make an undeterminable revocation status fatal (needs
                                  --revocation offline or online)
        """;

    /// <summary>The subset for connections outside <c>ApiClient</c>'s request pipeline — SSE
    /// streams, WebSocket handshakes, OAuth token fetches: proxy, bypass, and revocation. Retries,
    /// redirects, and HTTP-version pins are deliberately not consumed here: a stream re-subscribe
    /// has side effects a retry must not hide, and quietly accepting a flag these commands ignore
    /// would be the `mock --http` bug class in reverse. Shares every strict-parse rule with
    /// <see cref="Parse"/> via the same helpers.</summary>
    public static TransportOptions ParseStreamSubset(
        Args args, bool ignoreServerCertificateErrors, Func<string, string?>? environment = null)
    {
        string? proxyUrl = args.Value("--proxy");
        bool noProxy = args.Flag("--no-proxy");
        string? proxyUser = args.Value("--proxy-user");
        string? noProxySpec = args.Value("--noproxy");
        string? revocationRaw = args.Value("--revocation");
        bool revocationStrict = args.Flag("--revocation-strict");

        if (proxyUrl is not null && noProxy)
            throw new CliUsageException("--proxy and --no-proxy are mutually exclusive.");

        string? user = null, password = null;
        if (proxyUser is not null)
        {
            if (proxyUrl is null)
                throw new CliUsageException("--proxy-user needs --proxy (there is no proxy to authenticate to).");
            int colon = proxyUser.IndexOf(':');
            if (colon < 0)
                throw new CliUsageException($"--proxy-user expects user:password, got '{proxyUser}'.");
            user = proxyUser[..colon];
            password = proxyUser[(colon + 1)..];
        }

        var (fromFlag, fromEnvironment) = ResolveNoProxy(noProxySpec, noProxy, environment);

        var revocation = revocationRaw is null ? RevocationMode.None : ParseRevocation(revocationRaw);
        // These commands have no saved-request baseline that could already carry a mode, so the
        // strict-without-a-mode refusal that ApiClient.ValidateTransport owns for send/run is
        // decidable — and therefore decided — right here at parse time.
        if (revocationStrict && revocation == RevocationMode.None)
            throw new CliUsageException("--revocation-strict needs --revocation offline or online.");

        return new TransportOptions
        {
            Proxy = proxyUrl is not null ? ProxyMode.Explicit : noProxy ? ProxyMode.None : ProxyMode.System,
            ProxyUrl = proxyUrl,
            ProxyUser = user,
            ProxyPassword = password,
            NoProxy = fromFlag.Count > 0 ? fromFlag : fromEnvironment,
            Revocation = revocation,
            RevocationStrict = revocationStrict,
            IgnoreServerCertificateErrors = ignoreServerCertificateErrors
        };
    }

    /// <summary>Parses --revocation strictly — none/offline/online, case-insensitively, or a usage
    /// error naming the offending value. Shared by <see cref="Parse"/> (send/run/fuzz/serve) and
    /// <c>certapi grpc</c> directly, which builds its own <see cref="TransportOptions"/> and has no
    /// saved request to consult, so the two entry points can never drift on what a mode string
    /// means.</summary>
    internal static RevocationMode ParseRevocation(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "none" => RevocationMode.None,
        "offline" => RevocationMode.Offline,
        "online" => RevocationMode.Online,
        _ => throw new CliUsageException($"--revocation expects none, offline, or online, got '{raw}'.")
    };

    /// <summary>The bypass list a command line asks for, split by where it came from so a caller can
    /// apply the right precedence: an explicit <paramref name="spec"/> (--noproxy) always wins over
    /// the NO_PROXY environment variable, and the environment is not consulted at all when
    /// <paramref name="proxyOff"/> (--no-proxy) is set. Shared by every command that binds this flag —
    /// <see cref="Parse"/> for send/run/fuzz/serve, and <c>certapi grpc</c> directly, since it has no
    /// saved request to consult and so builds its own <c>TransportOptions</c> — so the blank-value
    /// check, the --no-proxy conflict, the strict parse, and the environment fallback can never drift
    /// between them.</summary>
    public static (IReadOnlyList<ProxyBypassRule> FromFlag, IReadOnlyList<ProxyBypassRule> FromEnvironment)
        ResolveNoProxy(string? spec, bool proxyOff, Func<string, string?>? environment = null)
    {
        if (spec is not null && string.IsNullOrWhiteSpace(spec))
            throw new CliUsageException($"--noproxy expects a comma-separated list of hosts, got '{spec}'.");

        // --noproxy needs a proxy to bypass; --no-proxy turns the proxy off entirely, so together
        // there is nothing left for a bypass list to narrow. ApiClient.ValidateTransport refuses the
        // same combination when it arrives via a saved request instead of the command line, off the
        // same shared message, so the two can never drift apart.
        if (spec is not null && proxyOff)
            throw new CliUsageException(ProxyBypass.NoProxyWithProxyOffMessage);

        IReadOnlyList<ProxyBypassRule> fromFlag = ProxyBypass.None;
        if (spec is not null)
        {
            // Strict, not lenient: a typo'd entry is refused rather than dropped, the same stance
            // ParseRetryOn takes below — a silently dropped bypass entry leaves the user believing
            // they configured a bypass they never got, and internal traffic then leaks through the proxy.
            if (!ProxyBypass.TryParse(spec, out fromFlag, out var problem))
                throw new CliUsageException($"--noproxy: {problem}.");
        }

        // The NO_PROXY environment fallback only applies when --noproxy was not named on this command
        // line. It is also skipped entirely when --no-proxy was given: the user did not name a bypass
        // list in that case, so an ambient NO_PROXY must not turn into the usage error above — it is
        // simply not consulted.
        IReadOnlyList<ProxyBypassRule> fromEnvironment = ProxyBypass.None;
        if (spec is null && !proxyOff && ProxyBypass.EnvironmentSpec(environment) is { } envSpec)
        {
            if (!ProxyBypass.TryParse(envSpec, out fromEnvironment, out var envProblem))
                throw new CliUsageException(
                    $"NO_PROXY: {envProblem}. Fix the environment variable, or override it for this run with --noproxy.");
        }

        return (fromFlag, fromEnvironment);
    }

    /// <summary>The comma-separated status list for --retry-on. Every token has to be a status code:
    /// dropping a typo'd one would leave the user believing they configured a retry they never got,
    /// which is worse than refusing the command line outright.</summary>
    private static IReadOnlyList<int> ParseRetryOn(string raw)
    {
        // Never empty — String.Split always yields at least one element — so "--retry-on ''" lands
        // on the empty token below and is refused rather than read as "retry on nothing".
        var codes = new List<int>();
        foreach (var token in raw.Split(','))
        {
            string trimmed = token.Trim();
            if (!int.TryParse(trimmed, out var code) || code is < 100 or > 599)
                throw new CliUsageException($"--retry-on expects comma-separated status codes, got '{trimmed}'.");
            codes.Add(code);
        }
        return codes;
    }

    /// <summary>host:port:ip, split on the first two colons only — curl's rule, and the only one that
    /// leaves an IPv6 address in the third field intact. Whether the address actually parses is left
    /// to <see cref="ApiTester.Core.ApiClient.ValidateTransport"/>, which owns that judgement.</summary>
    private static ResolveOverride ParseResolve(string raw)
    {
        int first = raw.IndexOf(':');
        int second = first < 0 ? -1 : raw.IndexOf(':', first + 1);
        if (first <= 0 || second < 0)
            throw new CliUsageException($"--resolve expects host:port:ip, got '{raw}'.");

        string portRaw = raw[(first + 1)..second];
        if (!int.TryParse(portRaw, out var port) || port is < 1 or > 65535)
            throw new CliUsageException($"--resolve needs a port between 1 and 65535, got '{raw}'.");

        return new ResolveOverride(raw[..first], port, raw[(second + 1)..]);
    }
}

/// <summary>Only the settings the user actually named. Nulls mean "leave the baseline alone", which
/// is what lets `run` keep a saved request's own transport choices unless a flag overrides them.</summary>
public sealed record TransportOverrides
{
    public ProxyMode? Proxy { get; init; }
    public string? ProxyUrl { get; init; }
    public string? ProxyUser { get; init; }
    public string? ProxyPassword { get; init; }
    public bool? FollowRedirects { get; init; }
    public int? MaxRedirects { get; init; }
    public bool? Decompress { get; init; }
    public HttpVersionMode? Version { get; init; }
    public IReadOnlyList<ResolveOverride> Resolve { get; init; } = Array.Empty<ResolveOverride>();

    public IReadOnlyList<ProxyBypassRule> NoProxy { get; init; } = ProxyBypass.None;
    public IReadOnlyList<ProxyBypassRule> NoProxyFromEnvironment { get; init; } = ProxyBypass.None;

    public int? Retries { get; init; }
    public IReadOnlyList<int>? RetryOn { get; init; }
    public int? RetryDelayMs { get; init; }
    public bool? RetryUnsafeMethods { get; init; }
    public bool? RetryOnTransportError { get; init; }

    public RevocationMode? Revocation { get; init; }
    public bool? RevocationStrict { get; init; }

    public TransportOptions ApplyTo(TransportOptions baseline)
    {
        var options = baseline;
        if (Proxy is { } proxy) options = options with { Proxy = proxy };
        if (ProxyUrl is not null) options = options with { ProxyUrl = ProxyUrl };
        if (ProxyUser is not null) options = options with { ProxyUser = ProxyUser };
        if (ProxyPassword is not null) options = options with { ProxyPassword = ProxyPassword };
        if (FollowRedirects is { } follow) options = options with { FollowRedirects = follow };
        if (MaxRedirects is { } max) options = options with { MaxRedirects = max };
        if (Decompress is { } decompress) options = options with { Decompress = decompress };
        if (Version is { } version) options = options with { Version = version };
        // An empty list means "not named", not "pin nothing" — the baseline keeps whatever it had.
        if (Resolve.Count > 0) options = options with { Resolve = Resolve };
        // Precedence: an explicit --noproxy wins, then whatever a saved request carries (a stored setting
        // is a choice someone made, where the environment is merely ambient), and NO_PROXY only when
        // neither has said anything. An empty list means "not named", exactly as Resolve above.
        if (NoProxy.Count > 0) options = options with { NoProxy = NoProxy };
        else if (NoProxyFromEnvironment.Count > 0 && options.NoProxy.Count == 0)
            options = options with { NoProxy = NoProxyFromEnvironment };
        if (Retries is { } retries) options = options with { Retries = retries };
        // RetryOn is nullable where Resolve above is an empty-list sentinel, and the two differ on
        // purpose: an empty pin list can only mean "not named", but an empty status list would be a
        // real instruction ("retry no status at all"), so absence needs a value of its own here.
        if (RetryOn is not null) options = options with { RetryOn = RetryOn };
        if (RetryDelayMs is { } delayMs) options = options with { RetryDelay = TimeSpan.FromMilliseconds(delayMs) };
        if (RetryUnsafeMethods is { } retryUnsafe) options = options with { RetryUnsafeMethods = retryUnsafe };
        if (RetryOnTransportError is { } retryTransport) options = options with { RetryOnTransportError = retryTransport };
        // Independent of each other, unlike the paired settings above: a saved request's own
        // revocation mode and its own --revocation-strict survive independently unless the matching
        // flag names that particular setting. This is exactly what lets `certapi run --revocation-
        // strict` (with no --revocation) stay valid when the saved request already carries a mode —
        // ApiClient.ValidateTransport enforces the strict-without-a-mode rule after both of these are
        // applied, so it sees the combined result, not either override in isolation.
        if (Revocation is { } revocation) options = options with { Revocation = revocation };
        if (RevocationStrict is { } strict) options = options with { RevocationStrict = strict };
        return options;
    }
}
