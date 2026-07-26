namespace ApiTester.Core;

/// <summary>The redirect chain as text, one line per hop, with the two facts that matter for a
/// client certificate called out. Empty when nothing was followed.
/// <para>In Core rather than the command line because a chain step reports its hops through the
/// shared runner, and two copies of this format would drift the moment one of them changed.</para></summary>
public static class RedirectReport
{
    public static string Lines(IReadOnlyList<RedirectHop> hops) =>
        string.Join("\n", hops.Select(h =>
            $"  {h.StatusCode} {h.From} -> {h.To}"
            + (h.AuthorizationDropped ? "  (authorization dropped)" : "")
            + (h.SchemeDowngrade ? "  (scheme downgrade)" : "")));
}
