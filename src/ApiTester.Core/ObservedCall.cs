namespace ApiTester.Core;

/// <summary>One request/response seen during a capture session, for the "save as requests" step.</summary>
public sealed record ObservedCall(string Method, string Url, int StatusCode, string? ContentType)
{
    private string Key =>
        Method.ToUpperInvariant() + " " +
        (Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : Url);

    /// <summary>One entry per method+path (query ignored); the last occurrence wins, first-seen order kept.</summary>
    public static IReadOnlyList<ObservedCall> Dedup(IEnumerable<ObservedCall> calls)
    {
        var order = new List<string>();
        var map = new Dictionary<string, ObservedCall>();
        foreach (var c in calls)
        {
            if (!map.ContainsKey(c.Key)) order.Add(c.Key);
            map[c.Key] = c;
        }
        return order.Select(k => map[k]).ToList();
    }
}
