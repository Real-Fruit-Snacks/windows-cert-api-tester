using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApiTester.Core;

/// <summary>How a response misbehaves after its headers are decided — the reason a test server
/// earns its keep. <see cref="Abort"/> closes the connection once what it had is sent;
/// <see cref="Reset"/> tears it down without a close, which is what a client sees when a middlebox
/// or a crash takes the connection away mid-response.</summary>
public enum MockFault { None, Abort, Reset }

/// <summary>What a route answers with, including how slowly and how badly.</summary>
public sealed record MockResponse(
    int Status,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[] Body,
    string? ContentType)
{
    /// <summary>A fixed pause before the first byte — the timeout exerciser.</summary>
    public int DelayMs { get; init; }

    /// <summary>Uniform random spread added to <see cref="DelayMs"/>, so repeated calls do not all
    /// take exactly the same time; a retry test wants variation, not a metronome.</summary>
    public int JitterMs { get; init; }

    /// <summary>Send the body this slowly rather than all at once, to exercise a read timeout on a
    /// response whose headers arrived promptly. Zero means "as fast as possible".</summary>
    public int DripBytesPerSecond { get; init; }

    public MockFault Fault { get; init; }
}

/// <summary>One declared route: what it matches, and what it answers. Matching is top-to-bottom,
/// first match winning, so an earlier narrow route can shadow a later broad one deliberately.</summary>
public sealed record MockRoute(
    string? Method,
    string? PathGlob,
    Regex? PathRegex,
    IReadOnlyList<KeyValuePair<string, string>> Query,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    MockResponse Response)
{
    /// <summary>Answers for successive calls to this route, when it declares a sequence: the first
    /// call gets the first entry, and once the list is exhausted the last entry repeats. That is
    /// what lets a scenario say "fail twice with 503, then succeed" — the shape a retry policy has
    /// to be tested against, and one nothing in this product could express before.
    /// Empty when the route answers the same way every time.</summary>
    public IReadOnlyList<MockResponse> Sequence { get; init; } = Array.Empty<MockResponse>();

    private int _calls;

    /// <summary>The answer for the next call. Thread-safe: the mock serves connections
    /// concurrently, and a sequence that lost count under load would make a retry test lie.</summary>
    public MockResponse Next()
    {
        if (Sequence.Count == 0) return Response;
        int index = Interlocked.Increment(ref _calls) - 1;
        return Sequence[Math.Min(index, Sequence.Count - 1)];
    }

    /// <summary>How many times this route has answered — what a test asserts on to prove a client
    /// really did retry rather than merely reporting that it would.</summary>
    public int Calls => Volatile.Read(ref _calls);
}

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

                // A route answers either one way every time, or a different way per call. Declaring
                // both is a contradiction rather than a merge, so it is refused by name.
                bool hasSequence = element.TryGetProperty("respondSequence", out var sequenceElement) &&
                                   sequenceElement.ValueKind == JsonValueKind.Array;
                bool hasRespond = element.TryGetProperty("respond", out var respond) &&
                                  respond.ValueKind == JsonValueKind.Object;

                if (hasSequence && hasRespond)
                {
                    warnings.Add($"route {index} declares both 'respond' and 'respondSequence' and was dropped — use one.");
                    continue;
                }
                if (!hasSequence && !hasRespond)
                {
                    warnings.Add($"route {index} has no 'respond' object and was dropped.");
                    continue;
                }

                var sequence = new List<MockResponse>();
                if (hasSequence)
                {
                    foreach (var entry in sequenceElement.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object) continue;
                        if (ReadResponse(entry, index, baseDirectory, read, warnings) is { } step) sequence.Add(step);
                    }
                    if (sequence.Count == 0)
                    {
                        warnings.Add($"route {index} has an empty or unusable 'respondSequence' and was dropped.");
                        continue;
                    }
                }

                var response = hasSequence
                    ? sequence[0]
                    : ReadResponse(respond, index, baseDirectory, read, warnings);
                if (response is null) continue;

                routes.Add(new MockRoute(method, pathGlob, pathRegex,
                    Pairs(match, "query"), Pairs(match, "headers"), response)
                { Sequence = sequence });
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

        var fault = Str(element, "then")?.ToLowerInvariant() switch
        {
            "abort" => MockFault.Abort,
            "reset" => MockFault.Reset,
            null => MockFault.None,
            var other => Warn(warnings, $"{where} has an unknown 'then' value '{other}'; it is ignored.")
        };

        return new MockResponse(status, headers, body, contentType)
        {
            DelayMs = Math.Max(0, Int(element, "delayMs") ?? 0),
            JitterMs = Math.Max(0, Int(element, "jitterMs") ?? 0),
            DripBytesPerSecond = Math.Max(0, Int(element, "dripBytesPerSec") ?? 0),
            Fault = fault
        };
    }

    /// <summary>Record a warning and answer <see cref="MockFault.None"/>, so an unrecognised
    /// setting degrades to "behave normally" while still being named.</summary>
    private static MockFault Warn(List<string> warnings, string message)
    {
        warnings.Add(message);
        return MockFault.None;
    }

    private static int? Int(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n : null;

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
