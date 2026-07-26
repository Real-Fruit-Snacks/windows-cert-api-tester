using System.Net.Http;
using ApiTester.Core;

namespace ApiTester.Tests;

public class MockReplayTests
{
    private static HarEntry BuildEntry(string method, string url, int status, string body,
        (string Name, string Value)[]? responseHeaders = null)
    {
        return new HarEntry
        {
            Request = new HarRequest { Method = method, Url = url },
            Response = new HarResponse
            {
                Status = status,
                StatusText = status is >= 200 and < 300 ? "OK" : "Error",
                Headers = (responseHeaders ?? Array.Empty<(string, string)>())
                    .Select(h => new HarNameValue(h.Name, h.Value)).ToList(),
                Content = new HarContent { MimeType = "application/json", Text = body }
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
    public async Task A_recorded_route_returns_the_recorded_status_header_and_body()
    {
        var har = BuildHar(BuildEntry("GET", "https://api.example.com/widgets", 201, "{\"id\":1}",
            responseHeaders: new[] { ("X-Widget-Source", "recorded") }));
        var source = new HarReplaySource(har);

        await using var srv = MockServer.Start(0, MockTlsMode.Http, replay: source);
        using var http = new HttpClient();
        var resp = await http.GetAsync(srv.BaseUrl + "widgets");

        Assert.Equal(201, (int)resp.StatusCode);
        Assert.Equal("recorded", resp.Headers.GetValues("X-Widget-Source").Single());
        Assert.Equal("{\"id\":1}", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_request_matching_nothing_returns_the_configured_no_match_status()
    {
        var har = BuildHar(BuildEntry("GET", "https://api.example.com/widgets", 200, "{}"));
        var source = new HarReplaySource(har, new HarReplayOptions { NoMatchStatus = 418 });

        await using var srv = MockServer.Start(0, MockTlsMode.Http, replay: source);
        using var http = new HttpClient();
        var resp = await http.GetAsync(srv.BaseUrl + "not-recorded");

        Assert.Equal(418, (int)resp.StatusCode);
    }

    [Fact]
    public async Task Two_calls_to_the_same_recorded_route_return_the_recorded_bodies_in_order()
    {
        var har = BuildHar(
            BuildEntry("GET", "https://api.example.com/poll", 200, "first"),
            BuildEntry("GET", "https://api.example.com/poll", 200, "second"));
        var source = new HarReplaySource(har);

        await using var srv = MockServer.Start(0, MockTlsMode.Http, replay: source);
        using var http = new HttpClient();

        var first = await http.GetAsync(srv.BaseUrl + "poll");
        var second = await http.GetAsync(srv.BaseUrl + "poll");

        Assert.Equal("first", await first.Content.ReadAsStringAsync());
        Assert.Equal("second", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Replay_mode_shadows_the_built_in_status_route()
    {
        var har = BuildHar(BuildEntry("GET", "https://api.example.com/widgets", 200, "{}"));
        var source = new HarReplaySource(har, new HarReplayOptions { NoMatchStatus = 418 });

        await using var srv = MockServer.Start(0, MockTlsMode.Http, replay: source);
        using var http = new HttpClient();
        var resp = await http.GetAsync(srv.BaseUrl + "status/500");

        Assert.Equal(418, (int)resp.StatusCode);
    }
}
