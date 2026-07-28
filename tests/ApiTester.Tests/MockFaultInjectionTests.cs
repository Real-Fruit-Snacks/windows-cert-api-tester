using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Fault injection: the reason a test server earns its keep. The headline case is
/// `respondSequence`, which finally lets this product's own retry policy be exercised end to end
/// against a real socket — "fail twice, then succeed" was not expressible before, so retry was
/// only ever tested against fixtures inside the suite.</summary>
public class MockFaultInjectionTests
{
    private static readonly Dictionary<string, string> NoHeaders = new(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- sequences (pure)

    [Fact]
    public void A_sequence_answers_in_order_then_repeats_its_last_entry()
    {
        var scenario = MockScenario.Parse("""
            {
              "routes": [
                { "match": { "path": "/flaky" },
                  "respondSequence": [
                    { "status": 503, "headers": { "Retry-After": "1" } },
                    { "status": 503 },
                    { "status": 200, "body": "ok" }
                  ] }
              ]
            }
            """);
        var route = scenario.Match("GET", "/flaky", NoHeaders)!;

        Assert.Equal(503, route.Next().Status);
        Assert.Equal(503, route.Next().Status);
        Assert.Equal(200, route.Next().Status);
        Assert.Equal(200, route.Next().Status);   // exhausted: the last entry stands
        Assert.Equal(4, route.Calls);
    }

    [Fact]
    public void A_route_without_a_sequence_answers_the_same_way_every_time()
    {
        var scenario = MockScenario.Parse("""{"routes":[{"match":{"path":"/x"},"respond":{"status":204}}]}""");
        var route = scenario.Match("GET", "/x", NoHeaders)!;

        Assert.Equal(204, route.Next().Status);
        Assert.Equal(204, route.Next().Status);
    }

    [Fact]
    public void A_sequence_keeps_count_correctly_under_concurrent_calls()
    {
        // The mock serves connections concurrently; a sequence that lost count under load would
        // make a retry test quietly lie about what happened.
        var scenario = MockScenario.Parse("""
            {"routes":[{"match":{"path":"/x"},"respondSequence":[{"status":500},{"status":200}]}]}
            """);
        var route = scenario.Match("GET", "/x", NoHeaders)!;

        Parallel.For(0, 500, _ => route.Next());

        Assert.Equal(500, route.Calls);
    }

    [Fact]
    public void Declaring_both_respond_and_respondSequence_is_refused_by_name()
    {
        var scenario = MockScenario.Parse("""
            {"routes":[{"match":{"path":"/x"},"respond":{"status":200},"respondSequence":[{"status":500}]}]}
            """);

        Assert.Empty(scenario.Routes);
        Assert.Contains(scenario.Warnings, w => w.Contains("respondSequence") && w.Contains("use one"));
    }

    [Fact]
    public void Fault_and_timing_settings_are_read_and_an_unknown_fault_is_named()
    {
        var scenario = MockScenario.Parse("""
            {
              "routes": [
                { "match": { "path": "/slow" },
                  "respond": { "status": 200, "delayMs": 250, "jitterMs": 50, "dripBytesPerSec": 128, "then": "abort" } },
                { "match": { "path": "/odd" }, "respond": { "status": 200, "then": "explode" } }
              ]
            }
            """);

        var slow = scenario.Match("GET", "/slow", NoHeaders)!.Response;
        Assert.Equal(250, slow.DelayMs);
        Assert.Equal(50, slow.JitterMs);
        Assert.Equal(128, slow.DripBytesPerSecond);
        Assert.Equal(MockFault.Abort, slow.Fault);

        // An unrecognised fault degrades to behaving normally — and says so.
        Assert.Equal(MockFault.None, scenario.Match("GET", "/odd", NoHeaders)!.Response.Fault);
        Assert.Contains(scenario.Warnings, w => w.Contains("explode"));
    }

    // ---------------------------------------------------------------- over the wire

    [Fact]
    public async Task A_sequence_makes_the_retry_policy_observable_end_to_end()
    {
        // This is the test the whole feature exists for: the client is told to retry, the server
        // fails twice and then succeeds, and the route's own counter proves three real requests
        // reached it — not that a retry was merely intended.
        var scenario = MockScenario.Parse("""
            {
              "routes": [
                { "match": { "path": "/flaky" },
                  "respondSequence": [
                    { "status": 503 }, { "status": 503 }, { "status": 200, "body": "recovered" }
                  ] }
              ]
            }
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);

        var response = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = $"http://127.0.0.1:{mock.Port}/flaky" },
            clientCertificate: null,
            transport: new TransportOptions
            {
                Retries = 3,
                RetryOn = new[] { 503 },
                RetryDelay = TimeSpan.FromMilliseconds(1)
            });

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("recovered", System.Text.Encoding.UTF8.GetString(response.Body));

        var route = scenario.Routes.Single();
        Assert.Equal(3, route.Calls);   // two failures plus the success, all real round trips
    }

    [Fact]
    public async Task Without_retries_the_first_failure_of_a_sequence_is_what_the_caller_sees()
    {
        // The control for the test above: the sequence is not what makes it succeed — retrying is.
        var scenario = MockScenario.Parse("""
            {"routes":[{"match":{"path":"/flaky"},"respondSequence":[{"status":503},{"status":200}]}]}
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);
        using var http = new HttpClient();

        var response = await http.GetAsync($"http://127.0.0.1:{mock.Port}/flaky");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, scenario.Routes.Single().Calls);
    }

    [Fact]
    public async Task A_declared_delay_actually_delays_the_response()
    {
        var scenario = MockScenario.Parse("""
            {"routes":[{"match":{"path":"/slow"},"respond":{"status":200,"delayMs":300,"body":"late"}}]}
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);
        using var http = new HttpClient();

        var clock = Stopwatch.StartNew();
        var response = await http.GetAsync($"http://127.0.0.1:{mock.Port}/slow");
        clock.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // A lower bound only: asserting an upper bound would be a race against a loaded machine,
        // and the claim under test is "it waited", not "it waited exactly".
        Assert.True(clock.ElapsedMilliseconds >= 250,
            $"expected the declared delay to be honoured, took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_delay_is_what_a_client_timeout_trips_over()
    {
        var scenario = MockScenario.Parse("""
            {"routes":[{"match":{"path":"/slow"},"respond":{"status":200,"delayMs":3000}}]}
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);

        var response = await new ApiClient().SendAsync(
            new ApiRequest
            {
                Method = HttpMethod.Get,
                Url = $"http://127.0.0.1:{mock.Port}/slow",
                Timeout = TimeSpan.FromMilliseconds(300)
            },
            clientCertificate: null);

        Assert.False(response.IsSuccess);
        Assert.Equal(ApiErrorKind.Timeout, response.Error?.Kind);
    }

    [Fact]
    public async Task An_aborted_response_fails_the_client_rather_than_arriving_truncated()
    {
        // Headers promise a body; the connection goes away instead. A client must report that as a
        // failure, not hand back a short body as though it were complete.
        var scenario = MockScenario.Parse("""
            {"routes":[{"match":{"path":"/cut"},"respond":{"status":200,"body":"never arrives","then":"abort"}}]}
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);

        var response = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = $"http://127.0.0.1:{mock.Port}/cut" },
            clientCertificate: null);

        Assert.False(response.IsSuccess);
    }

    [Fact]
    public async Task A_reset_connection_fails_the_client()
    {
        var scenario = MockScenario.Parse("""
            {"routes":[{"match":{"path":"/rst"},"respond":{"status":200,"then":"reset"}}]}
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);

        var response = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = $"http://127.0.0.1:{mock.Port}/rst" },
            clientCertificate: null);

        Assert.False(response.IsSuccess);
    }

    [Fact]
    public async Task A_dripped_body_still_arrives_whole_when_the_client_waits_for_it()
    {
        // Drip is about pacing, not corruption: a patient client gets every byte.
        var scenario = MockScenario.Parse("""
            {"routes":[{"match":{"path":"/drip"},"respond":{"status":200,"dripBytesPerSec":200,"body":"0123456789012345678901234567890123456789"}}]}
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        string body = await http.GetStringAsync($"http://127.0.0.1:{mock.Port}/drip");

        Assert.Equal(40, body.Length);
    }
}
