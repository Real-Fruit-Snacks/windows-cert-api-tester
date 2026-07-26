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
          --proxy <url>           Route through this proxy (e.g. http://proxy.corp:8080)
          --no-proxy              Ignore the system/PAC proxy — also restores TLS diagnostics
          --proxy-user <u:pass>   Proxy credentials
          --no-redirect           Do not follow 3xx redirects
          --max-redirs <n>        Redirect limit (default 20)
          --show-redirects        Print the redirect hop chain to stderr
          --no-decompress         Relay compressed bytes exactly as received
          --http1.1 / --http2     Pin the HTTP version
          --resolve <host:port:ip>  Pin a host to an address (repeatable; not valid with a proxy)
        """;

    /// <summary>Consume the shared transport flags. <paramref name="showRedirects"/> comes back
    /// separately because printing the hop chain is an output choice, not a transport one.</summary>
    public static TransportOverrides Parse(Args args, out bool showRedirects)
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
        showRedirects = args.Flag("--show-redirects");

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
            Resolve = resolve
        };
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
        return options;
    }
}
