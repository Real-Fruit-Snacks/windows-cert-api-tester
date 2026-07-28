using System.Text;

namespace ApiTester.Core;

/// <summary>One request of a run, as the report needs it.</summary>
/// <param name="Captures">Variable names the step captured and whether each worked. Names only —
/// a captured value is very often the credential the next step authenticates with, and this note
/// is bound for a folder that syncs.</param>
public sealed record RunReportRow(
    string Label, string Method, string Url, int? Status, TimeSpan Elapsed, long SizeBytes,
    bool Passed, IReadOnlyList<AssertionResult> Assertions, string? Error,
    IReadOnlyList<(string Variable, bool Ok, string? Error)> Captures);

/// <summary>A whole run: its rows, anything a failure caused to be skipped, and how long it took.</summary>
/// <param name="ChainName">Set when this was a chain rather than a suite — a chain's report is
/// ordered and its steps can be skipped, which changes what the note should say.</param>
public sealed record RunReport(
    string? ChainName, IReadOnlyList<RunReportRow> Rows, IReadOnlyList<string> Skipped, TimeSpan Elapsed);

/// <summary>Renders a run or chain result as a markdown note.
///
/// <para><b>What makes this worth keeping rather than reading once.</b> The frontmatter carries
/// `passed`, `failed`, `total` and the timestamp, so a vault of these notes can be charted over
/// time — a suite's health as a trend is the thing a single terminal run can never show. The body
/// carries what a failure investigation actually needs: which assertion failed, what it expected,
/// and what arrived instead.</para>
///
/// <para>Chain steps link back to their request notes from the catalogue export, so a chain report
/// lands *in* the catalogue rather than beside it.</para>
///
/// <para>Pure over the report, like the doctor and catalogue renderers.</para></summary>
public static class RunReportMarkdown
{
    public static string Render(RunReport report, DateTimeOffset when, bool includeSecrets = false)
    {
        int passed = report.Rows.Count(r => r.Passed);
        int failed = report.Rows.Count - passed;
        bool chain = report.ChainName is not null;

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"tags: [certapi/{(chain ? "chain-run" : "run")}]");
        if (chain) sb.AppendLine($"chain: {Quote(report.ChainName!)}");
        sb.AppendLine($"total: {report.Rows.Count}");
        sb.AppendLine($"passed: {passed}");
        sb.AppendLine($"failed: {failed}");
        if (report.Skipped.Count > 0) sb.AppendLine($"skipped: {report.Skipped.Count}");
        sb.AppendLine($"outcome: {(failed == 0 ? "pass" : "fail")}");
        sb.AppendLine($"ran: {when.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine($"elapsedMs: {report.Elapsed.TotalMilliseconds:F0}");
        sb.AppendLine("---").AppendLine();

        string title = chain ? $"Chain: {report.ChainName}" : "Run";
        sb.AppendLine($"# {MarkdownExport.Escape(title)} — {(failed == 0 ? "passed" : $"{failed} failed")}")
          .AppendLine();
        sb.AppendLine($"{when.ToUniversalTime():yyyy-MM-dd HH:mm} UTC · "
                    + $"{report.Rows.Count} request(s) in {report.Elapsed.TotalMilliseconds:F0} ms")
          .AppendLine();

        if (report.Rows.Count == 0 && report.Skipped.Count == 0)
        {
            sb.AppendLine("_Nothing ran._");
            return sb.ToString();
        }

        // A chain is an ordered sequence, so its table is numbered; a suite is a set, so it is not.
        sb.AppendLine(chain ? "| # | Step | Result | Status | Time |" : "| Request | Result | Status | Time |");
        sb.AppendLine(chain ? "|---|---|---|---|---|" : "|---|---|---|---|");

        for (int i = 0; i < report.Rows.Count; i++)
        {
            var row = report.Rows[i];
            // A chain step links to its request note; the link resolves because both notes are named
            // from the same request name, through the same sanitiser.
            string label = chain
                ? $"[[{MarkdownExport.Link(RequestName(row.Label, chain))}]]"
                : MarkdownExport.Cell(row.Label);
            string status = row.Error is not null ? "ERR" : row.Status?.ToString() ?? "—";
            string verdict = row.Passed ? "PASS" : "**FAIL**";
            string time = $"{row.Elapsed.TotalMilliseconds:F0} ms";
            sb.AppendLine(chain
                ? $"| {i + 1} | {label} | {verdict} | {status} | {time} |"
                : $"| {label} | {verdict} | {status} | {time} |");
        }
        foreach (var label in report.Skipped)
            sb.AppendLine(chain
                ? $"| — | {MarkdownExport.Cell(label)} | SKIP | — | — |"
                : $"| {MarkdownExport.Cell(label)} | SKIP | — | — |");
        sb.AppendLine();

        var failures = report.Rows.Where(r => !r.Passed).ToList();
        if (failures.Count > 0)
        {
            sb.AppendLine("## Failures").AppendLine();
            foreach (var row in failures)
            {
                sb.AppendLine($"### {MarkdownExport.Escape(row.Label)}").AppendLine();
                sb.AppendLine($"`{row.Method} {MarkdownSecrets.RedactUrl(row.Url, includeSecrets)}`").AppendLine();

                if (row.Error is not null)
                {
                    sb.AppendLine($"Transport error: {MarkdownExport.Escape(row.Error)}").AppendLine();
                    continue;
                }

                var broken = row.Assertions.Where(a => !a.Passed).ToList();
                if (broken.Count == 0)
                {
                    sb.AppendLine($"Returned {row.Status?.ToString() ?? "no status"}.").AppendLine();
                    continue;
                }

                // Expected against actual, which is the whole content of a failure investigation.
                sb.AppendLine("| Assertion | Got |").AppendLine("|---|---|");
                foreach (var assertion in broken)
                    sb.AppendLine($"| {MarkdownExport.Cell(assertion.Description)} "
                                + $"| {MarkdownExport.Cell(assertion.Actual ?? "(nothing)")} |");
                sb.AppendLine();
            }
        }

        var withCaptures = report.Rows.Where(r => r.Captures.Count > 0).ToList();
        if (withCaptures.Count > 0)
        {
            sb.AppendLine("## Captured").AppendLine();
            sb.AppendLine("Variable names only — a captured value is usually the credential the next "
                        + "step authenticates with.").AppendLine();
            foreach (var row in withCaptures)
                foreach (var (variable, ok, error) in row.Captures)
                    sb.AppendLine($"- `{{{{{MarkdownExport.Escape(variable)}}}}}` from "
                                + $"{MarkdownExport.Escape(row.Label)}"
                                + (ok ? "" : $" — **failed**{(error is null ? "" : $": {MarkdownExport.Escape(error)}")}"));
            sb.AppendLine();
        }

        sb.AppendLine("## Totals").AppendLine();
        sb.Append($"{report.Rows.Count} request(s) · {passed} passed · {failed} failed");
        if (report.Skipped.Count > 0) sb.Append($" · {report.Skipped.Count} skipped");
        sb.AppendLine($" · {report.Elapsed.TotalMilliseconds:F0} ms.");
        return sb.ToString();
    }

    /// <summary>Where a vault note goes. Append-only like the investigation notes, and for the same
    /// reason: a run is history, and a suite's health over time is exactly what a vault of these is
    /// for — overwriting yesterday's would erase the trend.</summary>
    public static string VaultPath(RunReport report, DateTimeOffset when, string into = "certapi")
    {
        string name = report.ChainName ?? "run";
        string stamp = when.ToUniversalTime().ToString("yyyyMMdd-HHmmss");
        return $"{MarkdownExport.Sanitize(into, "certapi")}/runs/"
             + $"{MarkdownExport.Sanitize($"{name}-{stamp}", "run")}.md";
    }

    /// <summary>Reduce a run label to the request's own name, which is what a wikilink must say.
    ///
    /// <para>Three decorations have to come off, each from a different place: a data-driven run
    /// prefixes <c>[row N] </c>; every label is a collection path (<c>Orders/Get orders</c>); and a
    /// chain names its steps <c>&lt;chain&gt;/&lt;n&gt;. &lt;request&gt;</c>, so the final segment
    /// still begins "1. ". Missing that last one linked to <c>[[1. Get orders]]</c>, which resolves
    /// to nothing — found by running a real chain rather than by reading the format.</para>
    ///
    /// <para><paramref name="chain"/> is why this takes a flag instead of guessing. A request the
    /// user genuinely named <c>2. Follow-up</c> is indistinguishable from a chain ordinal by
    /// looking at the string, so stripping unconditionally would rename it into a broken link. The
    /// caller always knows which it has.</para></summary>
    internal static string RequestName(string label, bool chain)
    {
        string text = label;
        if (text.StartsWith("[row ", StringComparison.Ordinal))
        {
            int close = text.IndexOf("] ", StringComparison.Ordinal);
            if (close > 0) text = text[(close + 2)..];
        }

        int slash = text.LastIndexOf('/');
        if (slash >= 0 && slash < text.Length - 1) text = text[(slash + 1)..];

        if (!chain) return text;

        // "12. Get orders" → "Get orders", for chain steps only.
        int digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits])) digits++;
        if (digits > 0 && text.Length > digits + 1 && text[digits] == '.' && text[digits + 1] == ' ')
            text = text[(digits + 2)..];

        return text;
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
