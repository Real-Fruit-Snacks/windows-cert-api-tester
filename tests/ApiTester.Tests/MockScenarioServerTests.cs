using System.IO;
using System.Net;
using System.Net.Http;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Declared routes over the wire: the mock actually answers them, they win over the
/// built-in echo routes, a miss gets the scenario's fallback, and — when a recording is supplied
/// too — a miss falls through to it instead. The parser's rules are covered as data in
/// <see cref="MockScenarioTests"/>; these are the ones that need a socket.</summary>
public class MockScenarioServerTests
{
    private static MockScenario Scenario(string json) => MockScenario.Parse(json);

    [Fact]
    public async Task A_declared_route_answers_over_the_wire()
    {
        var scenario = Scenario("""
            {
              "routes": [
                { "match": { "method": "GET", "path": "/api/orders" },
                  "respond": { "status": 201, "headers": { "Content-Type": "application/json", "X-Trace": "abc" },
                               "body": "{\"orders\":[]}" } }
              ]
            }
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);
        using var http = new HttpClient();

        var response = await http.GetAsync($"http://127.0.0.1:{mock.Port}/api/orders");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("abc", response.Headers.GetValues("X-Trace").Single());
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("{\"orders\":[]}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_declared_route_wins_over_the_built_in_routes()
    {
        // /status/500 is a built-in; a scenario that claims it must be the one that answers,
        // because a declared route is something someone deliberately wrote.
        var scenario = Scenario("""
            {"routes":[{"match":{"path":"/status/500"},"respond":{"status":200,"body":"mine"}}]}
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);
        using var http = new HttpClient();

        var response = await http.GetAsync($"http://127.0.0.1:{mock.Port}/status/500");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("mine", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_miss_gets_the_scenarios_fallback_and_never_the_built_in_routes()
    {
        var scenario = Scenario("""
            {"routes":[{"match":{"path":"/known"},"respond":{"status":200}}],
             "fallback":{"status":418,"body":"declared nothing for this"}}
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);
        using var http = new HttpClient();

        var response = await http.GetAsync($"http://127.0.0.1:{mock.Port}/status/200");

        // Compared as a number: 418 has no member in HttpStatusCode, and an arbitrary status is
        // exactly what a scenario is allowed to declare.
        Assert.Equal(418, (int)response.StatusCode);
        Assert.Equal("declared nothing for this", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Without_a_fallback_a_miss_is_a_404_that_says_why()
    {
        var scenario = Scenario("""{"routes":[{"match":{"path":"/known"},"respond":{"status":200}}]}""");
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);
        using var http = new HttpClient();

        var response = await http.GetAsync($"http://127.0.0.1:{mock.Port}/elsewhere");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("no route", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task With_a_recording_too_a_missed_route_falls_through_to_it()
    {
        // The combination the design is for: declare the handful of routes you care about, and let
        // a captured session cover everything else.
        var recorder = new GatewayRecorder();
        recorder.Record("GET", "http://127.0.0.1/recorded", Array.Empty<KeyValuePair<string, string>>(),
            Array.Empty<byte>(), null, 200, "OK", Array.Empty<KeyValuePair<string, string>>(),
            System.Text.Encoding.UTF8.GetBytes("from the recording"), 1.0);
        var path = Path.Combine(Path.GetTempPath(), $"scenario-{Guid.NewGuid():N}.har");
        recorder.Save(path);
        try
        {
            var replay = new HarReplaySource(HarReader.Parse(File.ReadAllText(path)));
            var scenario = Scenario("""
                {"routes":[{"match":{"path":"/declared"},"respond":{"status":200,"body":"from the scenario"}}]}
                """);
            await using var mock = MockServer.Start(0, MockTlsMode.Http, replay: replay, scenario: scenario);
            using var http = new HttpClient();

            Assert.Equal("from the scenario",
                await http.GetStringAsync($"http://127.0.0.1:{mock.Port}/declared"));
            Assert.Equal("from the recording",
                await http.GetStringAsync($"http://127.0.0.1:{mock.Port}/recorded"));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task A_header_value_carrying_a_newline_cannot_inject_a_second_response()
    {
        // The scenario file is written by a person, but it may be shared; a header value must not
        // be able to forge a response, exactly as for a recorded one.
        var scenario = Scenario("""
            {"routes":[{"match":{"path":"/x"},
              "respond":{"status":200,"headers":{"X-Bad":"a\r\nHTTP/1.1 500 Injected\r\nX-Evil: 1"},"body":"ok"}}]}
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);
        using var http = new HttpClient();

        var response = await http.GetAsync($"http://127.0.0.1:{mock.Port}/x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Evil"));
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }
}
