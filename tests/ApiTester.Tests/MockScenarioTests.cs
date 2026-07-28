using System.IO;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The declared-routes matcher and parser as data: glob and regular-expression paths,
/// required query and headers, first-match-wins ordering, body files resolved against the
/// scenario's own folder, and the named-warning contract for a route that cannot be used.</summary>
public class MockScenarioTests
{
    private static readonly Dictionary<string, string> NoHeaders = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] entries)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in entries) d[name] = value;
        return d;
    }

    private static string Body(MockRoute route) => Encoding.UTF8.GetString(route.Response.Body);

    // ---------------------------------------------------------------- globs (pure)

    [Theory]
    [InlineData("/api/orders", "/api/orders", true)]
    [InlineData("/api/orders", "/api/orders/1", false)]     // anchored: no accidental prefix match
    [InlineData("/api/*", "/api/orders", true)]
    [InlineData("/api/*", "/api/orders/1", false)]          // * stays within one segment
    [InlineData("/api/**", "/api/orders/1/items", true)]    // ** crosses them
    [InlineData("/api/orders/*/items", "/api/orders/7/items", true)]
    [InlineData("/API/Orders", "/api/orders", true)]        // paths compare case-insensitively
    public void Glob_matching_is_anchored_and_segment_aware(string glob, string path, bool expected)
    {
        Assert.Equal(expected, MockScenario.GlobMatches(glob, path));
    }

    // ---------------------------------------------------------------- matching

    [Fact]
    public void The_first_matching_route_wins_so_a_narrow_route_can_shadow_a_broad_one()
    {
        var scenario = MockScenario.Parse("""
            {
              "routes": [
                { "match": { "path": "/api/orders/special" }, "respond": { "status": 200, "body": "narrow" } },
                { "match": { "path": "/api/orders/*" },       "respond": { "status": 200, "body": "broad" } }
              ]
            }
            """);

        Assert.Equal("narrow", Body(scenario.Match("GET", "/api/orders/special", NoHeaders)!));
        Assert.Equal("broad", Body(scenario.Match("GET", "/api/orders/7", NoHeaders)!));
    }

    [Fact]
    public void A_route_can_require_a_method_a_query_pair_and_a_header()
    {
        var scenario = MockScenario.Parse("""
            {
              "routes": [
                { "match": { "method": "POST", "path": "/orders",
                             "query": { "status": "open" },
                             "headers": { "Accept": "application/json" } },
                  "respond": { "status": 201, "body": "created" } }
              ]
            }
            """);

        var accept = Headers(("Accept", "application/json"));
        Assert.NotNull(scenario.Match("POST", "/orders?status=open", accept));

        // Each requirement is load-bearing: drop any one and the route stops matching.
        Assert.Null(scenario.Match("GET", "/orders?status=open", accept));            // wrong method
        Assert.Null(scenario.Match("POST", "/orders?status=closed", accept));         // wrong query
        Assert.Null(scenario.Match("POST", "/orders", accept));                       // query missing
        Assert.Null(scenario.Match("POST", "/orders?status=open", NoHeaders));        // header missing
    }

    [Fact]
    public void Extra_query_and_headers_on_the_request_do_not_prevent_a_match()
    {
        // A scenario says what a route REQUIRES, not everything a caller may send.
        var scenario = MockScenario.Parse("""
            {"routes":[{"match":{"path":"/x","query":{"a":"1"}},"respond":{"status":200}}]}
            """);

        Assert.NotNull(scenario.Match("GET", "/x?a=1&b=2", Headers(("X-Extra", "yes"))));
    }

    [Fact]
    public void A_path_regular_expression_matches_where_a_glob_cannot()
    {
        var scenario = MockScenario.Parse("""
            {"routes":[{"match":{"pathRegex":"^/orders/[0-9]+$"},"respond":{"status":200,"body":"numeric"}}]}
            """);

        Assert.NotNull(scenario.Match("GET", "/orders/123", NoHeaders));
        Assert.Null(scenario.Match("GET", "/orders/abc", NoHeaders));
    }

    [Fact]
    public void A_route_with_no_method_matches_every_method()
    {
        var scenario = MockScenario.Parse("""{"routes":[{"match":{"path":"/any"},"respond":{"status":204}}]}""");

        Assert.NotNull(scenario.Match("GET", "/any", NoHeaders));
        Assert.NotNull(scenario.Match("DELETE", "/any", NoHeaders));
    }

    // ---------------------------------------------------------------- responses

    [Fact]
    public void A_response_carries_its_status_headers_and_body()
    {
        var scenario = MockScenario.Parse("""
            {
              "routes": [
                { "match": { "path": "/orders" },
                  "respond": { "status": 201, "headers": { "Content-Type": "application/json", "X-Trace": "abc" },
                               "body": "{\"id\":1}" } }
              ]
            }
            """);

        var response = scenario.Match("GET", "/orders", NoHeaders)!.Response;
        Assert.Equal(201, response.Status);
        Assert.Equal("application/json", response.ContentType);
        Assert.Contains(response.Headers, h => h.Key == "X-Trace" && h.Value == "abc");
        Assert.Equal("{\"id\":1}", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public void A_body_file_is_read_relative_to_the_scenarios_own_folder()
    {
        // Injected reader: the rule under test is which PATH is asked for, not disk access.
        string? asked = null;
        var scenario = MockScenario.Parse(
            """{"routes":[{"match":{"path":"/x"},"respond":{"bodyFile":"orders.json"}}]}""",
            baseDirectory: Path.Combine("C:", "scenarios"),
            readFile: path => { asked = path; return "from-file"; });

        Assert.Equal(Path.Combine("C:", "scenarios", "orders.json"), asked);
        Assert.Equal("from-file", Body(scenario.Match("GET", "/x", NoHeaders)!));
    }

    [Fact]
    public void An_unreadable_body_file_warns_and_answers_empty_rather_than_dropping_the_route()
    {
        var scenario = MockScenario.Parse(
            """{"routes":[{"match":{"path":"/x"},"respond":{"status":200,"bodyFile":"gone.json"}}]}""",
            baseDirectory: "C:/s",
            readFile: _ => throw new FileNotFoundException("no such file"));

        // The route still answers — its status is usually the point — and the problem is named.
        var route = scenario.Match("GET", "/x", NoHeaders);
        Assert.NotNull(route);
        Assert.Empty(route!.Response.Body);
        Assert.Contains(scenario.Warnings, w => w.Contains("gone.json"));
    }

    [Fact]
    public void The_fallback_answers_a_request_that_matches_nothing()
    {
        var scenario = MockScenario.Parse("""
            {"routes":[{"match":{"path":"/known"},"respond":{"status":200}}],
             "fallback":{"status":418,"body":"nope"}}
            """);

        Assert.Null(scenario.Match("GET", "/unknown", NoHeaders));
        Assert.Equal(418, scenario.Fallback!.Status);
        Assert.Equal("nope", Encoding.UTF8.GetString(scenario.Fallback.Body));
    }

    // ---------------------------------------------------------------- warnings and errors

    [Fact]
    public void A_route_that_cannot_be_used_is_dropped_and_named()
    {
        var scenario = MockScenario.Parse("""
            {
              "routes": [
                { "match": { "pathRegex": "([unclosed" }, "respond": { "status": 200 } },
                { "match": { "path": "/no-respond" } },
                { "match": { "path": "/bad-status" }, "respond": { "status": 99 } },
                { "match": { "path": "/fine" }, "respond": { "status": 200 } }
              ]
            }
            """);

        Assert.Single(scenario.Routes);                 // only the usable one survives
        Assert.NotNull(scenario.Match("GET", "/fine", NoHeaders));
        Assert.Equal(3, scenario.Warnings.Count);
        Assert.Contains(scenario.Warnings, w => w.Contains("pathRegex"));
        Assert.Contains(scenario.Warnings, w => w.Contains("respond"));
        Assert.Contains(scenario.Warnings, w => w.Contains("99"));
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated_because_people_write_these_by_hand()
    {
        var scenario = MockScenario.Parse("""
            {
              // the routes our tests need
              "routes": [ { "match": { "path": "/x" }, "respond": { "status": 200 }, }, ],
            }
            """);

        Assert.Single(scenario.Routes);
    }

    [Fact]
    public void Not_a_scenario_is_a_format_error_naming_what_was_expected()
    {
        var ex = Assert.Throws<FormatException>(() => MockScenario.Parse("""{"log":{"entries":[]}}"""));
        Assert.Contains("routes", ex.Message);

        Assert.Throws<FormatException>(() => MockScenario.Parse("not json"));
    }
}
