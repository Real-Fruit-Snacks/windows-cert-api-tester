using System.Text;

namespace ApiTester.Core;

/// <summary>Parse and compose the query-string portion of a URL with correct encoding.</summary>
public static class QueryString
{
    /// <summary>Split a URL (or path) into the part before '?' and the raw query (no leading '?').</summary>
    public static (string Path, string Query) Split(string url)
    {
        url ??= "";
        int i = url.IndexOf('?');
        return i < 0 ? (url, "") : (url[..i], url[(i + 1)..]);
    }

    /// <summary>Parse a raw query string (no leading '?') into decoded key/value pairs.</summary>
    public static List<KeyValuePair<string, string>> Parse(string query)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(query)) return list;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            string k = eq < 0 ? part : part[..eq];
            string v = eq < 0 ? "" : part[(eq + 1)..];
            list.Add(new(Decode(k), Decode(v)));
        }
        return list;
    }

    /// <summary>Build a raw query string (encoded, no leading '?') from pairs; "" if none.</summary>
    public static string Build(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var sb = new StringBuilder();
        foreach (var p in pairs)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(p.Key)).Append('=').Append(Uri.EscapeDataString(p.Value));
        }
        return sb.ToString();
    }

    /// <summary>Replace the query portion of <paramref name="url"/> with the given pairs.</summary>
    public static string Compose(string url, IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var (path, _) = Split(url);
        var q = Build(pairs);
        return q.Length == 0 ? path : path + "?" + q;
    }

    private static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
}
