namespace ApiTester.Core;

/// <summary>One candidate endpoint to probe: a path (bare, absolute, or full URL) and an
/// optional method that overrides the fuzz plan's method set for this entry.</summary>
public sealed record EndpointEntry(string Path, string? Method);

/// <summary>Parses a wordlist of candidate endpoints, one per line.</summary>
public static class EndpointList
{
    public static readonly IReadOnlyList<string> HttpMethods =
        new[] { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" };

    /// <summary>Parse wordlist text into entries. Blank lines and lines starting with '#' are
    /// skipped; a line may be "PATH" or "METHOD PATH" (a recognized HTTP verb prefix pins the
    /// method); identical (method, path) pairs are de-duplicated, input order preserved.</summary>
    public static IReadOnlyList<EndpointEntry> Parse(string text)
    {
        var result = new List<EndpointEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            string? method = null;
            string path = line;
            int sp = line.IndexOf(' ');
            if (sp > 0)
            {
                var head = line[..sp];
                if (HttpMethods.Contains(head, StringComparer.OrdinalIgnoreCase))
                {
                    method = head.ToUpperInvariant();
                    path = line[(sp + 1)..].TrimStart();
                }
            }
            if (path.Length == 0) continue;

            var key = (method ?? "") + " " + path;
            if (seen.Add(key)) result.Add(new EndpointEntry(path, method));
        }
        return result;
    }
}
