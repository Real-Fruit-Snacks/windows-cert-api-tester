using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The investigation note. Pure over a <see cref="DoctorReport"/>, so every case here is
/// a hand-built report — the renderer is what is under test, not the doctor.</summary>
public class DoctorMarkdownTests
{
    private static readonly DateTimeOffset When =
        new(2026, 7, 28, 9, 14, 0, TimeSpan.Zero);

    private static DoctorReport Failed() => new("https://api.internal/health", new[]
    {
        DoctorStage.Pass("url", "https://api.internal:443/health", TimeSpan.FromMilliseconds(1)),
        DoctorStage.Pass("dns", "api.internal → 1 address", TimeSpan.FromMilliseconds(12)),
        DoctorStage.Pass("tcp", "connected to api.internal:443", TimeSpan.FromMilliseconds(31)),
        DoctorStage.Fail("tls", "handshake failed — the server refused the client certificate",
            TimeSpan.FromMilliseconds(210),
            new[]
            {
                "server accepts client certificates issued by: CN=Corp Issuing CA, O=Corp",
                "none of your 3 certificates are issued by those",
            },
            "Ask for a certificate from CN=Corp Issuing CA, or use one you already have from it."),
    });

    [Fact]
    public void The_frontmatter_is_what_a_vault_can_search_on()
    {
        string note = DoctorMarkdown.Render(Failed(), When);

        Assert.StartsWith("---", note);
        Assert.Contains("tags: [certapi/investigation]", note);
        Assert.Contains("host: api.internal", note);
        Assert.Contains("outcome: failed", note);
        Assert.Contains("failedStage: tls", note);
        Assert.Contains("ran: 2026-07-28T09:14:00Z", note);
        Assert.Contains("elapsedMs: 254", note);
    }

    [Fact]
    public void A_passing_run_says_so_in_the_frontmatter_and_the_title()
    {
        var report = new DoctorReport("https://api.internal/health", new[]
        {
            DoctorStage.Pass("url", "ok", TimeSpan.FromMilliseconds(1)),
            DoctorStage.Pass("tls", "TLS 1.3", TimeSpan.FromMilliseconds(40)),
        });

        string note = DoctorMarkdown.Render(report, When);

        Assert.Contains("outcome: ok", note);
        Assert.DoesNotContain("failedStage:", note);
        Assert.Contains("all stages passed", note);
        Assert.Contains("Every stage passed", note);
    }

    [Fact]
    public void The_title_names_what_broke_without_opening_the_note()
    {
        Assert.Contains("— TLS handshake failed", DoctorMarkdown.Render(Failed(), When));
    }

    [Fact]
    public void Every_stage_appears_in_the_table_with_its_timing()
    {
        string note = DoctorMarkdown.Render(Failed(), When);

        Assert.Contains("| url | ok |", note);
        Assert.Contains("| tls | **FAIL** |", note);
        Assert.Contains("| 210 ms |", note);
    }

    [Fact]
    public void The_acceptable_authorities_and_the_advice_survive_verbatim()
    {
        // This is the part that makes the note worth keeping: it is what a normal request cannot
        // report, and what a ticket needs quoted rather than paraphrased.
        string note = DoctorMarkdown.Render(Failed(), When);

        Assert.Contains("server accepts client certificates issued by: CN=Corp Issuing CA, O=Corp", note);
        Assert.Contains("none of your 3 certificates are issued by those", note);
        Assert.Contains("> Ask for a certificate from CN=Corp Issuing CA", note);
    }

    [Fact]
    public void An_interception_finding_reaches_the_note()
    {
        var report = new DoctorReport("https://api.internal/health", new[]
        {
            DoctorStage.Pass("tls", "TLS 1.2", TimeSpan.FromMilliseconds(80),
                new[] { "chain root: CN=Zscaler Root CA" },
                DoctorReport.InterceptionNote("CN=Zscaler Root CA", rootIsLocallyTrusted: true)),
        });

        string note = DoctorMarkdown.Render(report, When);

        Assert.Contains("Zscaler", note);
        Assert.Contains("decrypted and re-signed in the middle", note);
    }

    [Fact]
    public void A_stage_with_nothing_to_add_does_not_pad_the_note()
    {
        var report = new DoctorReport("https://api.internal/health", new[]
        {
            DoctorStage.Pass("url", "https://api.internal:443/health", TimeSpan.FromMilliseconds(1)),
        });

        string note = DoctorMarkdown.Render(report, When);

        Assert.Contains("| url | ok |", note);     // in the table
        Assert.DoesNotContain("## Detail", note);  // but not given a section of its own
    }

    [Fact]
    public void A_report_with_no_stages_says_so_rather_than_rendering_an_empty_table()
    {
        string note = DoctorMarkdown.Render(new DoctorReport("https://x/", Array.Empty<DoctorStage>()), When);
        Assert.Contains("_No stages ran._", note);
    }

    // ---------------------------------------------------------------- secrets

    [Fact]
    public void An_api_key_in_the_url_does_not_reach_the_note()
    {
        // The leak this guards: a credential in a query string reads as part of the address, so it
        // survives the review a header would not — and the note is bound for a folder that syncs.
        var report = new DoctorReport("https://api.internal/health?api_key=sekrit-42&page=1", new[]
        {
            DoctorStage.Fail("tls", "handshake failed for https://api.internal/health?api_key=sekrit-42",
                TimeSpan.FromMilliseconds(10)),
        });

        string note = DoctorMarkdown.Render(report, When);

        Assert.DoesNotContain("sekrit-42", note);
        Assert.Contains("api_key=REDACTED", note);
        Assert.Contains("page=1", note);           // a harmless parameter is left alone
    }

    [Theory]
    [InlineData("https://svc:hunter2@api.internal/health")]
    [InlineData("https://svc:hunter2@api.internal/health?page=1")]
    public void A_password_in_the_url_itself_does_not_reach_the_note(string url)
    {
        // The oldest way to put a credential in a URL, and the easiest to miss because it does not
        // look like a parameter. This note is filed into a folder that syncs.
        var report = new DoctorReport(url, new[]
        {
            DoctorStage.Fail("tls", $"handshake failed for {url}", TimeSpan.FromMilliseconds(10)),
        });

        string note = DoctorMarkdown.Render(report, When);

        Assert.DoesNotContain("hunter2", note);
        Assert.Contains("svc:REDACTED@", note);      // the username survives, as a header name does
        Assert.Contains("api.internal", note);
    }

    [Fact]
    public void A_username_with_no_password_is_left_alone()
    {
        // There is no secret half to hide, and blanking it would lose real information.
        var report = new DoctorReport("https://svc@api.internal/health", Array.Empty<DoctorStage>());

        Assert.Contains("svc@api.internal", DoctorMarkdown.Render(report, When));
    }

    [Fact]
    public void Include_secrets_keeps_the_value()
    {
        var report = new DoctorReport("https://api.internal/health?token=sekrit-42", Array.Empty<DoctorStage>());

        Assert.Contains("sekrit-42", DoctorMarkdown.Render(report, When, includeSecrets: true));
    }

    // ---------------------------------------------------------------- vault paths

    [Fact]
    public void A_vault_note_is_named_for_the_host_and_the_moment()
    {
        string path = DoctorMarkdown.VaultPath(Failed(), When);
        Assert.Equal("certapi/investigations/api.internal-20260728-091400.md", path);
    }

    [Fact]
    public void Two_runs_against_the_same_host_do_not_overwrite_each_other()
    {
        // The asymmetry with the catalogue: current state is re-exported in place, history is not.
        string first = DoctorMarkdown.VaultPath(Failed(), When);
        string second = DoctorMarkdown.VaultPath(Failed(), When.AddSeconds(1));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_url_that_is_not_parseable_still_produces_a_usable_path()
    {
        var report = new DoctorReport("not-a-url", Array.Empty<DoctorStage>());
        string path = DoctorMarkdown.VaultPath(report, When);

        Assert.StartsWith("certapi/investigations/unknown-host-", path);
        Assert.EndsWith(".md", path);
    }
}

/// <summary>The shared vault redaction rules.</summary>
public class MarkdownSecretsTests
{
    [Theory]
    [InlineData("https://x/a?token=abc", "https://x/a?token=REDACTED")]
    [InlineData("https://x/a?api_key=abc&page=2", "https://x/a?api_key=REDACTED&page=2")]
    [InlineData("https://x/a?Access_Token=abc", "https://x/a?Access_Token=REDACTED")]
    [InlineData("https://x/a?page=2", "https://x/a?page=2")]
    [InlineData("https://x/a", "https://x/a")]
    [InlineData("https://x/a?", "https://x/a?")]
    [InlineData("not a url at all", "not a url at all")]
    public void Credential_query_values_are_replaced_and_nothing_else_is(string url, string expected)
    {
        Assert.Equal(expected, MarkdownSecrets.RedactUrl(url));
    }

    [Fact]
    public void Matching_is_on_the_whole_parameter_name_not_a_substring()
    {
        // A substring rule would redact these, and a note full of spurious redactions teaches
        // people to pass --include-secrets reflexively — worse than the risk it addresses.
        Assert.Equal("https://x/a?keyword=abc", MarkdownSecrets.RedactUrl("https://x/a?keyword=abc"));
        Assert.Equal("https://x/a?tokenCount=3", MarkdownSecrets.RedactUrl("https://x/a?tokenCount=3"));
    }

    [Theory]
    [InlineData("https://svc:hunter2@host/x", "https://svc:REDACTED@host/x")]
    [InlineData("http://svc:hunter2@proxy.corp:8080", "http://svc:REDACTED@proxy.corp:8080")]
    [InlineData("https://svc@host/x", "https://svc@host/x")]                 // no password
    [InlineData("https://host/x", "https://host/x")]                          // no userinfo
    [InlineData("https://svc:hunter2@host/x?token=t", "https://svc:REDACTED@host/x?token=REDACTED")]
    [InlineData("not a url", "not a url")]
    [InlineData("https://host/path@with@ats", "https://host/path@with@ats")]  // '@' after the authority
    public void A_password_in_the_authority_is_masked(string url, string expected)
    {
        Assert.Equal(expected, MarkdownSecrets.RedactUrl(url));
    }

    [Fact]
    public void A_fragment_survives_redaction()
    {
        Assert.Equal("https://x/a?token=REDACTED#section",
                     MarkdownSecrets.RedactUrl("https://x/a?token=abc#section"));
    }

    [Fact]
    public void Include_secrets_returns_the_url_untouched()
    {
        Assert.Equal("https://x/a?token=abc", MarkdownSecrets.RedactUrl("https://x/a?token=abc", includeSecrets: true));
    }
}
