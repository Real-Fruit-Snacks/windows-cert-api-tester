namespace ApiTester.Core;

/// <summary>The header rules named on `certapi serve`'s four repeatable flags — set (replace or
/// add) and remove, independently for the request side and the response side. This works with or
/// without --browser: it is not a browser concern, so it lives in its own pure, UI-free,
/// socket-free type rather than inside <see cref="BrowserRewriter"/>'s browser-mode gating, and the
/// `serve` command applies it on the way in and out, regardless of browser mode — <see
/// cref="MtlsGateway"/> itself stays a faithful byte relay and knows nothing about these rules.
/// There is no public constructor:
/// <see cref="TryCreate"/> is the only way to build one, so a rule set that exists has already
/// passed validation.</summary>
public sealed class HeaderRules
{
    /// <summary>The header names a user may not set or remove, matched case-insensitively.
    /// Deliberately its own set rather than a reference to <c>MtlsGateway.HopByHop</c>: that set
    /// answers "never relay this through a proxy" and this one answers "a user may not manage this
    /// on the command line" — today the two judgements happen to name the same ten headers, but
    /// that is not a reason to couple them, since a future change to one must not silently change
    /// the other.</summary>
    private static readonly HashSet<string> Refused = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "Content-Length", "TE", "Trailer",
        "Upgrade", "Proxy-Authenticate", "Proxy-Authorization", "Host"
    };

    private readonly Direction _request;
    private readonly Direction _response;

    private HeaderRules(
        IReadOnlyList<KeyValuePair<string, string>> setRequest,
        IReadOnlyList<string> removeRequest,
        IReadOnlyList<KeyValuePair<string, string>> setResponse,
        IReadOnlyList<string> removeResponse)
    {
        _request = new Direction(setRequest, removeRequest);
        _response = new Direction(setResponse, removeResponse);
    }

    /// <summary>The rules named on the command line, or null with <paramref name="problem"/> set to
    /// the usage error when a rule names a header the gateway refuses to let a user manage.</summary>
    public static HeaderRules? TryCreate(
        IReadOnlyList<KeyValuePair<string, string>> setRequest,
        IReadOnlyList<string> removeRequest,
        IReadOnlyList<KeyValuePair<string, string>> setResponse,
        IReadOnlyList<string> removeResponse,
        out string? problem)
    {
        // Scanned in the order the flags are documented — request before response, set before
        // remove within each — so the first bad name a user would notice reading their own command
        // line back is the one named in the error.
        problem = FirstRefusal(setRequest.Select(h => h.Key), "--request-header")
                  ?? FirstRefusal(removeRequest, "--remove-request-header")
                  ?? FirstRefusal(setResponse.Select(h => h.Key), "--response-header")
                  ?? FirstRefusal(removeResponse, "--remove-response-header");
        if (problem is not null) return null;

        return new HeaderRules(setRequest, removeRequest, setResponse, removeResponse);
    }

    /// <summary>The usage error for the first refused name in <paramref name="names"/>, or null
    /// when every name in this list is one the gateway lets a user manage.</summary>
    private static string? FirstRefusal(IEnumerable<string> names, string flag)
    {
        foreach (var name in names)
        {
            if (!Refused.Contains(name)) continue;
            // Host gets its own message: the other nine are refused because the HTTP stack frames
            // the message with them, but Host is refused for an unrelated reason — the client sets
            // it from the upstream URI, so a rule here could never actually take effect.
            return name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                ? $"{flag} cannot name 'Host': the gateway's HTTP client sets it from the upstream " +
                  "URI, so a rule here would only half-apply — and a half-working override is worse " +
                  "than none."
                : $"{flag} cannot name '{name}': it frames the HTTP message and the HTTP stack " +
                  "manages it, so a rule here would corrupt the exchange.";
        }
        return null;
    }

    /// <summary>The request headers with this rule set's request-side rules applied. Returns the
    /// very same list instance when there are no request rules, so the default relay is provably
    /// byte-faithful; otherwise a new list, and the input is never mutated.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ApplyToRequest(
        IReadOnlyList<KeyValuePair<string, string>> headers) => Apply(headers, _request);

    /// <summary>The response headers with this rule set's response-side rules applied. Returns the
    /// very same list instance when there are no response rules; otherwise a new list, and the
    /// input is never mutated.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ApplyToResponse(
        IReadOnlyList<KeyValuePair<string, string>> headers) => Apply(headers, _response);

    private static IReadOnlyList<KeyValuePair<string, string>> Apply(
        IReadOnlyList<KeyValuePair<string, string>> headers, Direction rules)
    {
        // Nothing to do for this direction: handing back the exact instance we were given (rather
        // than an equal copy) is what lets a caller prove the default relay never touches a header
        // at all, not merely that it produces the same values.
        if (!rules.HasRules) return headers;

        var sets = rules.Sets;
        var result = new List<KeyValuePair<string, string>>(headers.Count + sets.Length);
        // Which set rules have already been written — whether by replacing an original occurrence
        // in place or by appending below — so a repeated original occurrence of a set name
        // collapses to one, and a set rule already satisfied by an original header is not appended
        // again. Indexed by rule rather than keyed by name, because Direction has already reduced
        // the rules to one entry per name.
        var written = new bool[sets.Length];

        foreach (var h in headers)
        {
            if (rules.Removals.Contains(h.Key)) continue;   // removal already beat any set rule
            if (rules.IndexByName.TryGetValue(h.Key, out int at))
            {
                // The first occurrence keeps its position but takes the rule's value and the rule's
                // spelling of the name — the user asked for that header, so that is what is written.
                if (!written[at])
                {
                    written[at] = true;
                    result.Add(sets[at]);
                }
                continue;   // every later occurrence of this name is dropped
            }
            result.Add(h);
        }

        // Any set rule not already satisfied above — the header was simply never present — is new
        // and is appended, in the order the rules were given.
        for (int i = 0; i < sets.Length; i++)
            if (!written[i]) result.Add(sets[i]);

        return result;
    }

    /// <summary>One direction's rules reduced, once, to the form <see cref="Apply"/> wants. Both
    /// precedence decisions are settled here rather than per call: removal beats setting, so a name
    /// on both lists never reaches <see cref="Sets"/> at all, and a name set more than once keeps
    /// only the last rule, because that is the last thing the user said. The rules cannot change
    /// after construction, so rebuilding these lookups on every proxied request would be recomputing
    /// a constant — the per-request work is then just the walk over the headers themselves.</summary>
    private sealed class Direction
    {
        /// <summary>The surviving set rules, one per name, each at the position of that name's first
        /// mention and carrying the last value given for it.</summary>
        public readonly KeyValuePair<string, string>[] Sets;

        /// <summary>Where each set name sits in <see cref="Sets"/>, matched case-insensitively.</summary>
        public readonly Dictionary<string, int> IndexByName;

        /// <summary>The names to strip, matched case-insensitively.</summary>
        public readonly HashSet<string> Removals;

        public Direction(IReadOnlyList<KeyValuePair<string, string>> set, IReadOnlyList<string> remove)
        {
            Removals = new HashSet<string>(remove, StringComparer.OrdinalIgnoreCase);
            IndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var sets = new List<KeyValuePair<string, string>>(set.Count);
            foreach (var rule in set)
            {
                if (Removals.Contains(rule.Key)) continue;               // removal wins over setting
                if (IndexByName.TryGetValue(rule.Key, out int at)) sets[at] = rule;   // last value wins
                else
                {
                    IndexByName[rule.Key] = sets.Count;                  // first mention fixes the position
                    sets.Add(rule);
                }
            }
            Sets = sets.ToArray();
        }

        /// <summary>False when this direction carries no rule at all, which is what lets Apply hand
        /// its input straight back.</summary>
        public bool HasRules => Sets.Length > 0 || Removals.Count > 0;
    }
}
