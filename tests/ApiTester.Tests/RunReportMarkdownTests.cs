using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The run report note. Pure over a <see cref="RunReport"/>, so every case builds the
/// result by hand — the renderer is under test, not the runner.</summary>
public class RunReportMarkdownTests
{
    private static readonly DateTimeOffset When = new(2026, 7, 28, 9, 14, 0, TimeSpan.Zero);

    private static RunReportRow Row(string label, bool passed, int? status = 200,
                                    IReadOnlyList<AssertionResult>? assertions = null,
                                    string? error = null,
                                    IReadOnlyList<(string, bool, string?)>? captures = null) =>
        new(label, "GET", "https://api.internal/orders", status, TimeSpan.FromMilliseconds(142), 512,
            passed, assertions ?? Array.Empty<AssertionResult>(), error,
            captures ?? Array.Empty<(string, bool, string?)>());

    [Fact]
    public void The_frontmatter_is_what_makes_a_vault_chartable()
    {
        // The whole reason to keep these: a suite's health as a trend, which one terminal run
        // can never show.
        var report = new RunReport(null, new[] { Row("Orders/Get", true), Row("Orders/Create", false) },
                                   Array.Empty<string>(), TimeSpan.FromMilliseconds(400));

        string note = RunReportMarkdown.Render(report, When);

        Assert.Contains("tags: [certapi/run]", note);
        Assert.Contains("total: 2", note);
        Assert.Contains("passed: 1", note);
        Assert.Contains("failed: 1", note);
        Assert.Contains("outcome: fail", note);
        Assert.Contains("ran: 2026-07-28T09:14:00Z", note);
    }

    [Fact]
    public void A_clean_run_is_marked_as_passing()
    {
        var report = new RunReport(null, new[] { Row("Orders/Get", true) },
                                   Array.Empty<string>(), TimeSpan.FromMilliseconds(100));

        string note = RunReportMarkdown.Render(report, When);

        Assert.Contains("outcome: pass", note);
        Assert.Contains("# Run — passed", note);
        Assert.DoesNotContain("## Failures", note);
    }

    [Fact]
    public void Each_request_appears_with_its_verdict_status_and_timing()
    {
        var report = new RunReport(null, new[] { Row("Orders/Get", true), Row("Orders/Create", false, 500) },
                                   Array.Empty<string>(), TimeSpan.FromMilliseconds(400));

        string note = RunReportMarkdown.Render(report, When);

        Assert.Contains("| Orders/Get | PASS | 200 | 142 ms |", note);
        Assert.Contains("| Orders/Create | **FAIL** | 500 | 142 ms |", note);
    }

    [Fact]
    public void A_failed_assertion_shows_what_arrived_instead()
    {
        // Expected against actual is the entire content of a failure investigation.
        var assertions = new[]
        {
            new AssertionResult(true, "status == 200", "200"),
            new AssertionResult(false, "body.orders exists", null),
            new AssertionResult(false, "status == 200", "503"),
        };
        var report = new RunReport(null, new[] { Row("Orders/Get", false, 503, assertions) },
                                   Array.Empty<string>(), TimeSpan.FromMilliseconds(100));

        string note = RunReportMarkdown.Render(report, When);

        Assert.Contains("## Failures", note);
        Assert.Contains("| body.orders exists | (nothing) |", note);
        Assert.Contains("| status == 200 | 503 |", note);
        // A passing assertion is not a failure and must not be listed as one.
        Assert.DoesNotContain("| status == 200 | 200 |", note);
    }

    [Fact]
    public void A_transport_error_is_reported_as_such_rather_than_as_a_missing_assertion()
    {
        var report = new RunReport(null,
            new[] { Row("Orders/Get", false, null, error: "No such host is known") },
            Array.Empty<string>(), TimeSpan.FromMilliseconds(50));

        string note = RunReportMarkdown.Render(report, When);

        Assert.Contains("| Orders/Get | **FAIL** | ERR |", note);
        Assert.Contains("Transport error: No such host is known", note);
    }

    // ---------------------------------------------------------------- chains

    [Fact]
    public void A_chain_is_numbered_and_links_back_to_the_catalogue()
    {
        // A chain report should land IN the catalogue rather than beside it.
        var report = new RunReport("Login then fetch",
            new[] { Row("Auth/Log in", true), Row("Orders/Get orders", true) },
            Array.Empty<string>(), TimeSpan.FromMilliseconds(300));

        string note = RunReportMarkdown.Render(report, When);

        Assert.Contains("tags: [certapi/chain-run]", note);
        Assert.Contains("chain: \"Login then fetch\"", note);
        Assert.Contains("| 1 | [[Log in]] | PASS |", note);
        Assert.Contains("| 2 | [[Get orders]] | PASS |", note);
    }

    [Fact]
    public void A_step_skipped_after_a_failure_is_shown_not_dropped()
    {
        // An output that just stops leaves the reader guessing whether the rest passed.
        var report = new RunReport("Login then fetch",
            new[] { Row("Auth/Log in", false, 401) },
            new[] { "Orders/Get orders" }, TimeSpan.FromMilliseconds(120));

        string note = RunReportMarkdown.Render(report, When);

        Assert.Contains("skipped: 1", note);
        Assert.Contains("| — | Orders/Get orders | SKIP | — | — |", note);
        Assert.Contains("1 skipped", note);
    }

    [Theory]
    [InlineData("Orders/Get orders", "Get orders")]
    [InlineData("[row 3] Orders/Get orders", "Get orders")]
    [InlineData("Get orders", "Get orders")]
    [InlineData("A/B/C/Deep one", "Deep one")]
    public void A_suite_label_reduces_to_the_request_name_the_note_is_filed_under(string label, string expected)
    {
        // The wikilink has to name the request note, which carries neither the collection path nor
        // a data-driven run's row prefix.
        Assert.Equal(expected, RunReportMarkdown.RequestName(label, chain: false));
    }

    [Theory]
    // The real shape a chain produces: "<chain>/<n>. <request>". Missing the ordinal linked to
    // [[1. Get orders]], which resolves to nothing — found by running an actual chain, not by
    // reading the format.
    [InlineData("Login then fetch/1. Get orders", "Get orders")]
    [InlineData("Login then fetch/12. Get orders", "Get orders")]
    public void A_chain_label_also_sheds_its_step_number(string label, string expected)
    {
        Assert.Equal(expected, RunReportMarkdown.RequestName(label, chain: true));
    }

    // ---------------------------------------------------------------- captures and secrets

    [Fact]
    public void A_request_genuinely_named_with_a_number_is_not_mangled_in_a_suite()
    {
        // "2. Follow-up" as a request name is indistinguishable from a chain ordinal by looking at
        // the string, which is exactly why the caller passes what it knows instead of guessing.
        Assert.Equal("2. Follow-up", RunReportMarkdown.RequestName("Orders/2. Follow-up", chain: false));
    }

    [Fact]
    public void Captured_variables_are_named_never_valued()
    {
        // A captured value is very often the credential the next step authenticates with, and this
        // note is bound for a folder that syncs.
        var report = new RunReport("Login then fetch",
            new[] { Row("Auth/Log in", true, captures: new (string, bool, string?)[]
            {
                ("authToken", true, null),
                ("refreshToken", false, "no such field"),
            }) },
            Array.Empty<string>(), TimeSpan.FromMilliseconds(90));

        string note = RunReportMarkdown.Render(report, When);

        Assert.Contains("## Captured", note);
        Assert.Contains("`{{authToken}}`", note);
        Assert.Contains("`{{refreshToken}}`", note);
        Assert.Contains("**failed**: no such field", note);
    }

    [Fact]
    public void A_credential_in_a_failing_requests_url_is_redacted()
    {
        var row = new RunReportRow("Orders/Get", "GET", "https://api.internal/orders?api_key=sekrit-42",
            500, TimeSpan.FromMilliseconds(10), 0, false, Array.Empty<AssertionResult>(), null,
            Array.Empty<(string, bool, string?)>());
        var report = new RunReport(null, new[] { row }, Array.Empty<string>(), TimeSpan.FromMilliseconds(10));

        string note = RunReportMarkdown.Render(report, When);

        Assert.DoesNotContain("sekrit-42", note);
        Assert.Contains("api_key=REDACTED", note);

        Assert.Contains("sekrit-42", RunReportMarkdown.Render(report, When, includeSecrets: true));
    }

    [Fact]
    public void An_empty_run_says_so_rather_than_rendering_an_empty_table()
    {
        var report = new RunReport(null, Array.Empty<RunReportRow>(), Array.Empty<string>(), TimeSpan.Zero);
        Assert.Contains("_Nothing ran._", RunReportMarkdown.Render(report, When));
    }

    // ---------------------------------------------------------------- vault paths

    [Fact]
    public void A_vault_report_is_named_for_the_chain_and_the_moment()
    {
        var report = new RunReport("Login then fetch", Array.Empty<RunReportRow>(),
                                   Array.Empty<string>(), TimeSpan.Zero);

        Assert.Equal("certapi/runs/Login then fetch-20260728-091400.md",
                     RunReportMarkdown.VaultPath(report, When));
    }

    [Fact]
    public void A_suite_run_without_a_chain_name_still_gets_a_path()
    {
        var report = new RunReport(null, Array.Empty<RunReportRow>(), Array.Empty<string>(), TimeSpan.Zero);

        Assert.Equal("certapi/runs/run-20260728-091400.md", RunReportMarkdown.VaultPath(report, When));
    }

    [Fact]
    public void Two_runs_do_not_overwrite_each_other()
    {
        var report = new RunReport(null, Array.Empty<RunReportRow>(), Array.Empty<string>(), TimeSpan.Zero);

        Assert.NotEqual(RunReportMarkdown.VaultPath(report, When),
                        RunReportMarkdown.VaultPath(report, When.AddSeconds(1)));
    }
}
