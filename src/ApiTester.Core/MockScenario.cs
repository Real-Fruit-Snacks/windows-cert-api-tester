using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApiTester.Core;

/// <summary>What a route answers with.</summary>
public sealed record MockResponse(
    int Status,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[] Body,
    string? ContentType);

/// <summary>One declared route: what it matches, and what it answers. Matching is top-to-bottom,
/// first match winning, so an earlier narrow route can shadow a later broad one deliberately.</summary>
public sealed record MockRoute(
    string? Method,
    string? PathGlob,
    Regex? PathRegex,
    IReadOnlyList<KeyValuePair<string, string>> Query,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    MockResponse Response);

/// <summary>A parsed scenario file: the routes in file order, the answer for a request that matches
/// none, and warnings for anything that could not be carried across — a route with an
/// uncompilable pattern or an unreadable body file is dropped and named, never silently ignored.</summary>
public sealed record MockScenario(
    IReadOnlyList<MockRoute> Routes,
    MockResponse? Fallback,
    IReadOnlyList<string> Warnings)
{
    /// <summary>The first route that matches this request, or null when none does.</summary>
    public MockRoute? Match(string method, string pathAndQuery, IReadOnlyDictionary<string, string> headers)
    {
        var (path, rawQuery) = SplitPath(pathAndQuery);
        var query = QueryString.Parse(rawQuery);

        foreach (var route in Routes)
        {
            if (route.Method is { } m && !m.Equals(method, StringComparison.OrdinalIgnoreCase)) continue;
            if (route.PathRegex is { } regex) { if (!regex.IsMatch(path)) continue; }
            else if (route.PathGlob is { } glob && !GlobMatches(glob, path)) continue;

            // Every declared pair must be present; extras on the request are fine, because a
            // scenario says what a route REQUIRES, not everything the caller may send.
            bool ok = true;
            foreach (var want in route.Query)
                if (!query.Any(q => q.Key.Equals(want.Key, StringComparison.OrdinalIgnoreCase) && q.Value == want.Value))
                { ok = false; break; }
            if (!ok) continue;

            foreach (var want in route.Headers)
                if (!headers.TryGetValue(want.Key, out var actual) ||
                    !actual.Contains(want.Value, StringComparison.OrdinalIgnoreCase))
                { ok = false; break; }
            if (!ok) continue;

            return route;
        }
        return null;
    }

    /// <summary>Glob matching over a URL path: <c>*</c> matches within one segment, <c>**</c>
    /// matches across segments. Anchored at both ends, so <c>/api/x</c> does not match
    /// <c>/api/x/y</c> unless the pattern says so.</summary>
    internal static bool GlobMatches(string glob, string path)
    {
        var pattern = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            if (glob[i] == '*')
            {
                bool doubled = i + 1 < glob.Length && glob[i + 1] == '*';
                if (doubled) { pattern.Append(".*"); i++; }
                else pattern.Append("[^/]*");
                continue;
            }
            pattern.Append(Regex.Escape(glob[i].ToString()));
        }
        pattern.Append('$');
        return Regex.IsMatch(path, pattern.ToString(), RegexOptions.IgnoreCase);
    }

    private static (string Path, string RawQuery) SplitPath(string pathAndQuery)
    {
        int q = pathAndQuery.IndexOf('?');
        return q < 0 ? (pathAndQuery, "") : (pathAndQuery[..q], pathAndQuery[(q + 1)..]);
    }

    /// <summary>Read a scenario. <paramref name="baseDirectory"/> is what a <c>bodyFile</c> is
    /// resolved against — the scenario file's own folder, so a scenario is portable as a unit.
    /// Throws <see cref="FormatException"/> only when the document is not a scenario at all;
    /// anything less total is a warning and a dropped route.</summary>
    public static MockScenario Parse(string json, string? baseDirectory = null, Func<string, string>? readFile = null)
    {
        var read = readFile ?? File.ReadAllText;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException ex) { throw new FormatException("Not JSON: " + ex.Message); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("routes", out var routesElement) ||
                routesElement.ValueKind != JsonValueKind.Array)
                throw new FormatException(
                    "Not a mock scenario: expected an object with a 'routes' array.");

            var warnings = new List<string>();
            var routes = new List<MockRoute>();
            int index = 0;

            foreach (var element in routesElement.EnumerateArray())
            {
                index++;
                if (element.ValueKind != JsonValueKind.Object)
                {
                    warnings.Add($"route {index} is not an object and was dropped.");
                    continue;
                }

                var match = element.TryGetProperty("match", out var m) && m.ValueKind == JsonValueKind.Object
                    ? m : default;

                string? method = Str(match, "method");
                string? pathGlob = Str(match, "path");
                string? pathRegexRaw = Str(match, "pathRegex");
                Regex? pathRegex = null;
                if (pathRegexRaw is not null)
                {
                    try { pathRegex = new Regex(pathRegexRaw, RegexOptions.IgnoreCase); }
                    catch (ArgumentException ex)
                    {
                        warnings.Add($"route {index} has a pathRegex that will not compile ({ex.Message}) and was dropped.");
                        continue;
                    }
                }

                if (!element.TryGetProperty("respond", out var respond) || respond.ValueKind != JsonValueKind.Object)
                {
                    warnings.Add($"route {index} has no 'respond' object and was dropped.");
                    continue;
                }

                var response = ReadResponse(respond, index, baseDirectory, read, warnings);
                if (response is null) continue;

                routes.Add(new MockRoute(method, pathGlob, pathRegex,
                    Pairs(match, "query"), Pairs(match, "headers"), response));
            }

            MockResponse? fallback = null;
            if (root.TryGetProperty("fallback", out var fb) && fb.ValueKind == JsonValueKind.Object)
                fallback = ReadResponse(fb, 0, baseDirectory, read, warnings);

            return new MockScenario(routes, fallback, warnings);
        }
    }

    private static MockResponse? ReadResponse(
        JsonElement element, int routeIndex, string? baseDirectory, Func<string, string> read, List<string> warnings)
    {
        string where = routeIndex == 0 ? "the fallback" : $"route {routeIndex}";

        int status = 200;
        if (element.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var parsed))
        {
            if (parsed is < 100 or > 599)
            {
                warnings.Add($"{where} has a status of {parsed}, which is not an HTTP status, and was dropped.");
                return null;
            }
            status = parsed;
        }

        byte[] body = Array.Empty<byte>();
        if (Str(element, "bodyFile") is { } file)
        {
            // Resolved against the scenario file's own folder, so a scenario plus its bodies moves
            // as one unit rather than depending on where the command happened to be run.
            string path = baseDirectory is null ? file : Path.Combine(baseDirectory, file);
            try { body = Encoding.UTF8.GetBytes(read(path)); }
            catch (Exception ex)
            {
                warnings.Add($"{where} names bodyFile '{file}' which could not be read ({ex.Message}); it answers with an empty body.");
            }
        }
        else if (Str(element, "body") is { } inline)
        {
            body = Encoding.UTF8.GetBytes(inline);
        }

        var headers = Pairs(element, "headers");
        string? contentType = headers
            .FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value;

        return new MockResponse(status, headers, body, contentType);
    }

    private static IReadOnlyList<KeyValuePair<string, string>> Pairs(JsonElement parent, string name)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Object)
            foreach (var property in o.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String)
                    list.Add(new(property.Name, property.Value.GetString()!));
        return list;
    }

    private static string? Str(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
