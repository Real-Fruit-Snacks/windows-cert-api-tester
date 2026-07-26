using ApiTester.Core;

namespace ApiTester.Tests;

public class HarReplaySourceTests
{
    private static HarEntry BuildEntry(
        string method, string url, int status,
        string body = "", string? contentType = "application/json",
        (string Name, string Value)[]? responseHeaders = null,
        (string Name, string Value)[]? queryString = null,
        string? contentEncoding = null)
    {
        return new HarEntry
        {
            Request = new HarRequest
            {
                Method = method,
                Url = url,
                QueryString = (queryString ?? Array.Empty<(string, string)>())
                    .Select(q => new HarNameValue(q.Name, q.Value)).ToList()
            },
            Response = new HarResponse
            {
                Status = status,
                StatusText = status is >= 200 and < 300 ? "OK" : "Error",
                Headers = (responseHeaders ?? Array.Empty<(string, string)>())
                    .Select(h => new HarNameValue(h.Name, h.Value)).ToList(),
                Content = new HarContent
                {
                    MimeType = contentType ?? "",
                    Text = body,
                    Encoding = contentEncoding
                }
            }
        };
    }

    private static Har BuildHar(params HarEntry[] entries)
    {
        var har = new Har();
        har.Log.Entries.AddRange(entries);
        return har;
    }

    [Fact]
    public void Exact_method_path_and_query_match_wins_over_a_path_only_recording()
    {
        var har = BuildHar(
            BuildEntry("GET", "https://api.example.com/orders?x=1", 200, body: "path-only-recording"),
            BuildEntry("GET", "https://api.example.com/orders?a=1&b=2", 200, body: "exact-recording"));

        var source = new HarReplaySource(har);

        var recorded = source.Match("GET", "/orders?a=1&b=2", out var kind);

        Assert.NotNull(recorded);
        Assert.Equal(ReplayMatch.Exact, kind);
        Assert.Equal("exact-recording", System.Text.Encoding.UTF8.GetString(recorded!.Body));
    }

    [Fact]
    public void A_query_that_matches_no_recording_falls_back_to_the_method_and_path_recording()
    {
        var har = BuildHar(BuildEntry("GET", "https://api.example.com/orders?a=1", 200, body: "path-recording"));
        var source = new HarReplaySource(har);

        var recorded = source.Match("GET", "/orders?z=99", out var kind);

        Assert.NotNull(recorded);
        Assert.Equal(ReplayMatch.Path, kind);
        Assert.Equal("path-recording", System.Text.Encoding.UTF8.GetString(recorded!.Body));
    }

    [Fact]
    public void An_unknown_path_returns_no_match()
    {
        var har = BuildHar(BuildEntry("GET", "https://api.example.com/orders", 200));
        var source = new HarReplaySource(har);

        var recorded = source.Match("GET", "/somewhere-else", out var kind);

        Assert.Null(recorded);
        Assert.Equal(ReplayMatch.None, kind);
    }

    [Fact]
    public void A_known_path_with_a_different_method_returns_no_match()
    {
        var har = BuildHar(BuildEntry("GET", "https://api.example.com/orders", 200));
        var source = new HarReplaySource(har);

        var recorded = source.Match("POST", "/orders", out var kind);

        Assert.Null(recorded);
        Assert.Equal(ReplayMatch.None, kind);
    }

    [Fact]
    public void The_incoming_method_matches_case_insensitively()
    {
        var har = BuildHar(BuildEntry("GET", "https://api.example.com/orders", 200, body: "found-it"));
        var source = new HarReplaySource(har);

        var recorded = source.Match("get", "/orders");

        Assert.NotNull(recorded);
        Assert.Equal("found-it", System.Text.Encoding.UTF8.GetString(recorded!.Body));
    }

    [Fact]
    public void Sequential_repeats_replay_in_recorded_order_then_repeat_the_last_recording()
    {
        var har = BuildHar(
            BuildEntry("GET", "https://api.example.com/poll", 200, body: "first"),
            BuildEntry("GET", "https://api.example.com/poll", 200, body: "second"),
            BuildEntry("GET", "https://api.example.com/poll", 200, body: "third"));

        var source = new HarReplaySource(har);

        string Body() => System.Text.Encoding.UTF8.GetString(source.Match("GET", "/poll")!.Body);

        Assert.Equal("first", Body());
        Assert.Equal("second", Body());
        Assert.Equal("third", Body());
        Assert.Equal("third", Body());
        Assert.Equal("third", Body());
    }

    [Fact]
    public void Disabling_sequential_repeats_always_returns_the_first_recording()
    {
        var har = BuildHar(
            BuildEntry("GET", "https://api.example.com/poll", 200, body: "first"),
            BuildEntry("GET", "https://api.example.com/poll", 200, body: "second"));

        var source = new HarReplaySource(har, new HarReplayOptions { SequentialRepeats = false });

        string Body() => System.Text.Encoding.UTF8.GetString(source.Match("GET", "/poll")!.Body);

        Assert.Equal("first", Body());
        Assert.Equal("first", Body());
        Assert.Equal("first", Body());
    }

    [Fact]
    public void Disabling_match_query_still_matches_a_request_carrying_a_query_to_the_path_recording()
    {
        var har = BuildHar(BuildEntry("GET", "https://api.example.com/orders", 200, body: "path-recording"));
        var source = new HarReplaySource(har, new HarReplayOptions { MatchQuery = false });

        var recorded = source.Match("GET", "/orders?a=1&b=2", out var kind);

        Assert.NotNull(recorded);
        Assert.Equal(ReplayMatch.Path, kind);
        Assert.Equal("path-recording", System.Text.Encoding.UTF8.GetString(recorded!.Body));
    }

    [Fact]
    public void Query_parameter_order_does_not_matter_for_an_exact_match()
    {
        var har = BuildHar(BuildEntry("GET", "https://api.example.com/orders?a=1&b=2", 200, body: "exact-recording"));
        var source = new HarReplaySource(har);

        var recorded = source.Match("GET", "/orders?b=2&a=1", out var kind);

        Assert.NotNull(recorded);
        Assert.Equal(ReplayMatch.Exact, kind);
        Assert.Equal("exact-recording", System.Text.Encoding.UTF8.GetString(recorded!.Body));
    }

    [Fact]
    public void Framing_headers_are_excluded_while_set_cookie_replays_exactly_as_recorded()
    {
        var har = BuildHar(BuildEntry("GET", "https://api.example.com/login", 200,
            responseHeaders: new[]
            {
                ("Transfer-Encoding", "chunked"),
                ("Connection", "keep-alive"),
                ("Content-Length", "42"),
                ("Set-Cookie", "session=[redacted]; Path=/")
            }));

        var source = new HarReplaySource(har);
        var recorded = source.Match("GET", "/login");

        Assert.NotNull(recorded);
        Assert.DoesNotContain(recorded!.Headers, h => h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(recorded.Headers, h => h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(recorded.Headers, h => h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(recorded.Headers, h => h.Key == "Set-Cookie" && h.Value == "session=[redacted]; Path=/");
    }

    [Fact]
    public void A_base64_encoded_body_decodes_to_the_recorded_bytes()
    {
        byte[] originalBytes = { 0, 1, 2, 3, 250, 251, 252 };
        string base64 = Convert.ToBase64String(originalBytes);
        var har = BuildHar(BuildEntry("GET", "https://api.example.com/binary", 200,
            body: base64, contentEncoding: "base64"));

        var source = new HarReplaySource(har);
        var recorded = source.Match("GET", "/binary");

        Assert.NotNull(recorded);
        Assert.Equal(originalBytes, recorded!.Body);
    }

    [Fact]
    public void A_plain_text_body_is_encoded_as_utf8_bytes()
    {
        var har = BuildHar(BuildEntry("GET", "https://api.example.com/text", 200, body: "hello world"));
        var source = new HarReplaySource(har);

        var recorded = source.Match("GET", "/text");

        Assert.NotNull(recorded);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("hello world"), recorded!.Body);
    }

    [Fact]
    public void Count_equals_the_number_of_entries_loaded()
    {
        var har = BuildHar(
            BuildEntry("GET", "https://api.example.com/a", 200),
            BuildEntry("GET", "https://api.example.com/b", 200),
            BuildEntry("POST", "https://api.example.com/a", 201));

        var source = new HarReplaySource(har);

        Assert.Equal(3, source.Count);
    }

    [Fact]
    public void Content_type_comes_from_the_recorded_mime_type()
    {
        var har = BuildHar(BuildEntry("GET", "https://api.example.com/data", 200, contentType: "application/xml"));
        var source = new HarReplaySource(har);

        var recorded = source.Match("GET", "/data");

        Assert.NotNull(recorded);
        Assert.Equal("application/xml", recorded!.ContentType);
    }

    [Fact]
    public void Content_type_falls_back_to_the_recorded_content_type_header_when_mime_type_is_empty()
    {
        var har = BuildHar(BuildEntry("GET", "https://api.example.com/data", 200, contentType: "",
            responseHeaders: new[] { ("Content-Type", "text/csv") }));
        var source = new HarReplaySource(har);

        var recorded = source.Match("GET", "/data");

        Assert.NotNull(recorded);
        Assert.Equal("text/csv", recorded!.ContentType);
    }
}
