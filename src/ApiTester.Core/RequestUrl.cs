namespace ApiTester.Core;

/// <summary>URL composition shared by the request editor: base URL + path + query parameters.</summary>
public static class RequestUrl
{
    /// <summary>Combine a base URL, a path, and the enabled query parameters into a full URL.</summary>
    public static string Effective(string? baseUrl, string path,
        IEnumerable<KeyValuePair<string, string>> enabledParams)
    {
        var combined = UrlCombine(baseUrl, path);
        var (bare, _) = QueryString.Split(combined);
        var q = QueryString.Build(enabledParams);
        return q.Length == 0 ? bare : bare + "?" + q;
    }

    /// <summary>Split a path or full URL into its path and the parsed query parameters.</summary>
    public static (string PathNoQuery, List<KeyValuePair<string, string>> Params) SplitForEditing(string pathOrUrl)
    {
        var (path, query) = QueryString.Split(pathOrUrl ?? "");
        return (path, QueryString.Parse(query));
    }

    // Mirror of the App's UrlHelper.Combine, kept here so Core stays self-contained and testable.
    private static string UrlCombine(string? baseUrl, string? path)
    {
        path = (path ?? "").Trim();
        baseUrl = (baseUrl ?? "").Trim();
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return path;
        if (string.IsNullOrEmpty(baseUrl)) return path;
        if (string.IsNullOrEmpty(path)) return baseUrl;
        return baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
    }
}
