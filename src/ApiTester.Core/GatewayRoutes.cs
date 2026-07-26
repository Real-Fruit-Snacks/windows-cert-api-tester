namespace ApiTester.Core;

/// <summary>One upstream mounted at a path prefix on the gateway.</summary>
public sealed record GatewayRoute(string PathPrefix, Uri Upstream);

/// <summary>The gateway's routing table: which upstream a request path belongs to, and what path
/// to forward once the mount point has been stripped off.</summary>
public sealed class GatewayRoutes
{
    private const string Root = "/";

    public GatewayRoutes(IReadOnlyList<GatewayRoute> routes)
    {
        if (routes.Count == 0)
            throw new ArgumentException("A gateway needs at least one upstream route.", nameof(routes));

        var normalized = new List<GatewayRoute>(routes.Count);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            string prefix = Normalize(route.PathPrefix);
            // Two upstreams on one mount point is a configuration mistake with no right answer;
            // silently keeping the last one would send traffic to a host the user did not mean.
            if (!taken.Add(prefix))
                throw new ArgumentException(
                    $"Two upstreams are mounted at the same path prefix '{prefix}'.", nameof(routes));
            normalized.Add(route with { PathPrefix = prefix });
        }
        All = normalized;
    }

    public IReadOnlyList<GatewayRoute> All { get; }

    /// <summary>The route that owns <paramref name="pathAndQuery"/>, or null when none does — in
    /// which case <paramref name="forwardedPathAndQuery"/> is the input unchanged and the caller
    /// answers 404 without contacting anything. Longest prefix wins.
    /// <para>A request target that is not a rooted path — HttpListener will hand over
    /// "//evil.com/x" or "https://evil.com/x" as written — cannot match a mounted prefix, so it can
    /// only ever land on a root route, where it is passed through untouched. That is deliberate:
    /// routing is not the place to decide it is hostile, and the gateway's authority check refuses
    /// it a moment later, before the client certificate can reach that host.</para></summary>
    public GatewayRoute? Resolve(string pathAndQuery, out string forwardedPathAndQuery)
    {
        forwardedPathAndQuery = pathAndQuery;

        int mark = pathAndQuery.IndexOf('?');
        string path = mark < 0 ? pathAndQuery : pathAndQuery[..mark];
        string query = mark < 0 ? string.Empty : pathAndQuery[mark..];

        GatewayRoute? best = null;
        foreach (var route in All)
        {
            if (!Matches(route.PathPrefix, path)) continue;
            if (best is null || route.PathPrefix.Length > best.PathPrefix.Length) best = route;
        }
        if (best is null) return null;

        // The mount point is the gateway's, not the upstream's: strip it so the upstream sees the
        // path it would have served directly. A bare prefix leaves nothing, which is the root.
        // The root route mounts the whole namespace, so there is nothing to strip.
        if (best.PathPrefix != Root)
        {
            string rest = path[best.PathPrefix.Length..];
            forwardedPathAndQuery = (rest.Length == 0 ? Root : rest) + query;
        }
        return best;
    }

    /// <summary>One spelling per mount point, so "api", "/api" and "/api/" are the same route and
    /// the matching and stripping below can assume the canonical form.</summary>
    private static string Normalize(string pathPrefix)
    {
        string prefix = pathPrefix.Trim();
        if (prefix.Length == 0) return Root;
        if (prefix[0] != '/') prefix = Root + prefix;
        while (prefix.Length > 1 && prefix[^1] == '/') prefix = prefix[..^1];
        return prefix;
    }

    /// <summary>Whether <paramref name="prefix"/> owns <paramref name="path"/>. Matching is on whole
    /// segments, so /api owns /api, /api/ and /api/orders but not /apiary — a mount point that
    /// swallowed a longer word would send that word's traffic to the wrong host.</summary>
    private static bool Matches(string prefix, string path)
    {
        if (prefix == Root) return true;
        // Case-insensitively: a browser that upper-cases a path segment must still reach the host
        // the user mounted, and the paths themselves are relayed to the upstream verbatim.
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return path.Length == prefix.Length || path[prefix.Length] == '/';
    }
}
