using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

public class AssertionEvaluatorTests
{
    private static ApiResponse Resp(int? status = 200, string? body = null, long elapsedMs = 10,
        params (string, string)[] headers) => new()
    {
        StatusCode = status,
        Body = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body),
        Elapsed = TimeSpan.FromMilliseconds(elapsedMs),
        Headers = headers.Select(h => new KeyValuePair<string, string>(h.Item1, h.Item2)).ToList()
    };

    private static AssertionRule Rule(AssertTarget t, AssertOp op, string value = "", string path = "") =>
        new() { Target = t, Op = op, Value = value, Path = path };

    private static bool Pass(AssertionRule r, ApiResponse resp) => AssertionEvaluator.Evaluate(new[] { r }, resp)[0].Passed;

    [Fact]
    public void Matches_bounds_a_catastrophic_pattern_instead_of_hanging()
    {
        // A classic ReDoS pattern against non-matching input would backtrack for a very long time
        // without a timeout. The evaluator must return (fail) quickly rather than hang the run.
        var resp = Resp(200, new string('a', 40) + "!");
        var rule = Rule(AssertTarget.BodyText, AssertOp.Matches, "(a+)+$");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool passed = Pass(rule, resp);
        sw.Stop();

        Assert.False(passed);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"regex should have been bounded, took {sw.Elapsed}");
    }

    [Fact]
    public void Status_equals()
    {
        Assert.True(Pass(Rule(AssertTarget.Status, AssertOp.Equals, "200"), Resp(200)));
        Assert.False(Pass(Rule(AssertTarget.Status, AssertOp.Equals, "200"), Resp(404)));
    }

    [Fact]
    public void Status_numeric_comparisons()
    {
        Assert.True(Pass(Rule(AssertTarget.Status, AssertOp.LessThan, "300"), Resp(201)));
        Assert.True(Pass(Rule(AssertTarget.Status, AssertOp.GreaterThan, "199"), Resp(200)));
        Assert.False(Pass(Rule(AssertTarget.Status, AssertOp.LessThan, "300"), Resp(404)));
    }

    [Fact]
    public void Time_less_than()
    {
        Assert.True(Pass(Rule(AssertTarget.Time, AssertOp.LessThan, "500"), Resp(elapsedMs: 120)));
        Assert.False(Pass(Rule(AssertTarget.Time, AssertOp.LessThan, "100"), Resp(elapsedMs: 800)));
    }

    [Fact]
    public void Header_exists_equals_contains()
    {
        var r = Resp(200, headers: ("Content-Type", "application/json; charset=utf-8"));
        Assert.True(Pass(Rule(AssertTarget.Header, AssertOp.Exists, path: "content-type"), r)); // case-insensitive name
        Assert.True(Pass(Rule(AssertTarget.Header, AssertOp.Contains, "json", "Content-Type"), r));
        Assert.False(Pass(Rule(AssertTarget.Header, AssertOp.Exists, path: "X-Missing"), r));
        Assert.True(Pass(Rule(AssertTarget.Header, AssertOp.NotExists, path: "X-Missing"), r));
    }

    [Fact]
    public void Body_json_path_exists_equals_and_numeric()
    {
        var r = Resp(200, "{\"data\":{\"id\":42,\"name\":\"Ada\"}}");
        Assert.True(Pass(Rule(AssertTarget.Body, AssertOp.Exists, path: "data.id"), r));
        Assert.True(Pass(Rule(AssertTarget.Body, AssertOp.Equals, "Ada", "data.name"), r));
        Assert.True(Pass(Rule(AssertTarget.Body, AssertOp.Equals, "42", "data.id"), r));
        Assert.True(Pass(Rule(AssertTarget.Body, AssertOp.GreaterThan, "40", "data.id"), r));
        Assert.True(Pass(Rule(AssertTarget.Body, AssertOp.NotExists, path: "data.missing"), r));
        Assert.False(Pass(Rule(AssertTarget.Body, AssertOp.Exists, path: "data.missing"), r));
    }

    [Fact]
    public void Body_on_non_json_does_not_throw_and_fails_exists()
    {
        var r = Resp(200, "<html>not json</html>");
        Assert.False(Pass(Rule(AssertTarget.Body, AssertOp.Exists, path: "data.id"), r));
    }

    [Fact]
    public void BodyText_contains_and_matches()
    {
        var r = Resp(200, "hello world 123");
        Assert.True(Pass(Rule(AssertTarget.BodyText, AssertOp.Contains, "world"), r));
        Assert.True(Pass(Rule(AssertTarget.BodyText, AssertOp.Matches, "\\d{3}"), r));
        Assert.False(Pass(Rule(AssertTarget.BodyText, AssertOp.Contains, "absent"), r));
    }

    [Fact]
    public void Invalid_regex_never_throws_and_fails()
    {
        var r = Resp(200, "abc");
        Assert.False(Pass(Rule(AssertTarget.BodyText, AssertOp.Matches, "([unclosed"), r));
    }

    [Fact]
    public void Transport_error_fails_status_assertion_gracefully()
    {
        var errored = new ApiResponse { Error = new ApiError(ApiErrorKind.Network, "down"), Elapsed = TimeSpan.FromMilliseconds(5) };
        Assert.False(Pass(Rule(AssertTarget.Status, AssertOp.Equals, "200"), errored));
        Assert.True(Pass(Rule(AssertTarget.Status, AssertOp.NotExists), errored));   // no status came back
    }

    [Fact]
    public void Disabled_rules_are_skipped()
    {
        var rule = Rule(AssertTarget.Status, AssertOp.Equals, "999");
        rule.Enabled = false;
        Assert.Empty(AssertionEvaluator.Evaluate(new[] { rule }, Resp(200)));
        Assert.True(AssertionEvaluator.AllPass(new[] { rule }, Resp(200)));  // vacuously true
    }

    [Fact]
    public void AllPass_requires_every_enabled_rule()
    {
        var r = Resp(200, "{\"ok\":true}");
        var rules = new[]
        {
            Rule(AssertTarget.Status, AssertOp.Equals, "200"),
            Rule(AssertTarget.Body, AssertOp.Equals, "true", "ok"),
        };
        Assert.True(AssertionEvaluator.AllPass(rules, r));
        rules[1].Value = "false";
        Assert.False(AssertionEvaluator.AllPass(rules, r));
    }
}
