using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>Characterizes <c>certapi run --chain</c>'s CURRENT observable behavior — exit codes,
/// stdout/stderr text (including exact line shapes), the <c>--json</c> envelope, and the workspace
/// file it writes — as of the release that is about to extract chain execution out of
/// <c>RunCommand</c> and into <c>ApiTester.Core</c>. This file is the frozen contract: it must stay
/// green, unedited, through every later slice of that extraction. It intentionally duplicates
/// helpers from <c>ChainCliTests.cs</c> rather than sharing them, so nothing else can weaken it.</summary>
public class ChainCharacterizationTests
{
    // ---------------------------------------------------------------- A. per-step PASS/FAIL line shape

    [Fact]
    public async Task A_passing_step_line_shows_verdict_status_time_size_and_label_in_that_order()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var ws = Workspace(state =>
            {
                state.Collections.Add(Request("ping", "r1", upstream.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain { Name = "solo", Steps = { new ChainStep { RequestId = "r1" } } });
            });
            try
            {
                var so = new StringWriter();
                int code = CliApp.Run(new[] { "run", "--chain", "solo", "--workspace", ws },
                                      so, new StringWriter(), services: Services(client, ws));
                Assert.Equal(0, code);

                const string label = "solo/1. ping";
                string line = Lines(so.ToString()).Single(l => l.Contains(label));
                // Shape only — never a specific millisecond or byte value.
                Assert.Matches(@"^PASS\s+\d+\s+\d+ ms\s+[\d.]+\s+(B|KB|MB)\s+" + Regex.Escape(label) + "$", line);
            }
            finally { File.Delete(ws); }
        }
    }

    // ---------------------------------------------------------------- B. chain step label numbering

    [Fact]
    public async Task Chain_step_label_is_1_based_and_disambiguates_the_same_request_used_twice()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var ws = Workspace(state =>
            {
                state.Collections.Add(Request("dup", "r1", upstream.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain
                {
                    Name = "twice",
                    Steps = { new ChainStep { RequestId = "r1" }, new ChainStep { RequestId = "r1" } }
                });
            });
            try
            {
                var so = new StringWriter();
                int code = CliApp.Run(new[] { "run", "--chain", "twice", "--workspace", ws },
                                      so, new StringWriter(), services: Services(client, ws));
                Assert.Equal(0, code);
                Assert.Contains("twice/1. dup", so.ToString());
                Assert.Contains("twice/2. dup", so.ToString());
                // The number in the label is not decorative: the same request really ran twice.
                Assert.Equal(2, upstream.RequestCount);
            }
            finally { File.Delete(ws); }
        }
    }

    // ---------------------------------------------------------------- C. skipped-step report

    [Fact]
    public async Task Skipped_step_line_shows_SKIP_em_dashes_the_label_and_the_reason()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var down = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 99, failStatus: 500);
            await using var later = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var ws = Workspace(state =>
            {
                state.Collections.Add(Request("login", "r1", down.BaseUrl, client.Thumbprint!));
                state.Collections.Add(Request("browse", "r2", later.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain
                {
                    Name = "halts",
                    Steps = { new ChainStep { RequestId = "r1" }, new ChainStep { RequestId = "r2" } }
                });
            });
            try
            {
                var so = new StringWriter();
                int code = CliApp.Run(new[] { "run", "--chain", "halts", "--workspace", ws },
                                      so, new StringWriter(), services: Services(client, ws));
                Assert.Equal(1, code);

                const string label = "halts/2. browse";
                // Built with the same interpolation the production line uses, so the exact widths and
                // the em dash are pinned without hand-counting whitespace.
                string expected = $"SKIP  {"—",4}  {"—",6} ms  {"—",9}  {label}  (an earlier step failed)";
                Assert.Contains(expected, so.ToString());
            }
            finally { File.Delete(ws); }
        }
    }

    // ---------------------------------------------------------------- D. footer line

    [Fact]
    public async Task Footer_uses_singular_request_and_omits_the_skipped_clause_for_one_passing_step()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var ws = Workspace(state =>
            {
                state.Collections.Add(Request("ping", "r1", upstream.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain { Name = "solo", Steps = { new ChainStep { RequestId = "r1" } } });
            });
            try
            {
                var so = new StringWriter();
                int code = CliApp.Run(new[] { "run", "--chain", "solo", "--workspace", ws },
                                      so, new StringWriter(), services: Services(client, ws));
                Assert.Equal(0, code);

                var lines = Lines(so.ToString());
                int dash = lines.IndexOf("----");
                Assert.True(dash >= 0 && dash + 1 < lines.Count);
                // Elapsed seconds is never pinned to a value — only its shape.
                Assert.Matches(@"^1 request · 1 passed · 0 failed · [\d.]+ s$", lines[dash + 1]);
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task Footer_uses_plural_requests_when_more_than_one_step_ran()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var a = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            await using var b = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var ws = Workspace(state =>
            {
                state.Collections.Add(Request("one", "r1", a.BaseUrl, client.Thumbprint!));
                state.Collections.Add(Request("two", "r2", b.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain
                {
                    Name = "pair",
                    Steps = { new ChainStep { RequestId = "r1" }, new ChainStep { RequestId = "r2" } }
                });
            });
            try
            {
                var so = new StringWriter();
                int code = CliApp.Run(new[] { "run", "--chain", "pair", "--workspace", ws },
                                      so, new StringWriter(), services: Services(client, ws));
                Assert.Equal(0, code);

                var lines = Lines(so.ToString());
                int dash = lines.IndexOf("----");
                Assert.True(dash >= 0 && dash + 1 < lines.Count);
                Assert.Matches(@"^2 requests · 2 passed · 0 failed · [\d.]+ s$", lines[dash + 1]);
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task Footer_gains_a_skipped_clause_when_a_step_was_skipped()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var down = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 99, failStatus: 500);
            await using var later = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var ws = Workspace(state =>
            {
                state.Collections.Add(Request("login", "r1", down.BaseUrl, client.Thumbprint!));
                state.Collections.Add(Request("browse", "r2", later.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain
                {
                    Name = "halts",
                    Steps = { new ChainStep { RequestId = "r1" }, new ChainStep { RequestId = "r2" } }
                });
            });
            try
            {
                var so = new StringWriter();
                int code = CliApp.Run(new[] { "run", "--chain", "halts", "--workspace", ws },
                                      so, new StringWriter(), services: Services(client, ws));
                Assert.Equal(1, code);

                var lines = Lines(so.ToString());
                int dash = lines.IndexOf("----");
                Assert.True(dash >= 0 && dash + 1 < lines.Count);
                // Only step 1 ran (singular "request"), it failed, and step 2 is reported skipped.
                Assert.Matches(@"^1 request · 0 passed · 1 failed · 1 skipped · [\d.]+ s$", lines[dash + 1]);
            }
            finally { File.Delete(ws); }
        }
    }

    // ---------------------------------------------------------------- E. stop-on-failure stderr line

    [Fact]
    public async Task Stop_on_failure_writes_the_stopping_the_chain_line_to_stderr_verbatim()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var down = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 99, failStatus: 500);
            await using var later = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var ws = Workspace(state =>
            {
                state.Collections.Add(Request("login", "r1", down.BaseUrl, client.Thumbprint!));
                state.Collections.Add(Request("browse", "r2", later.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain
                {
                    Name = "halts",
                    Steps = { new ChainStep { RequestId = "r1" }, new ChainStep { RequestId = "r2" } }
                });
            });
            try
            {
                var se = new StringWriter();
                int code = CliApp.Run(new[] { "run", "--chain", "halts", "--workspace", ws },
                                      new StringWriter(), se, services: Services(client, ws));
                Assert.Equal(1, code);

                const string label = "halts/1. login";
                string expected = $"{label}: step failed — stopping the chain (the later steps would only " +
                                   "report the consequences).";
                Assert.Contains(expected, se.ToString());
            }
            finally { File.Delete(ws); }
        }
    }

    // ---------------------------------------------------------------- F. exit codes

    [Fact]
    public async Task Exit_code_is_0_when_every_chain_step_passes()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var ws = Workspace(state =>
            {
                state.Collections.Add(Request("ping", "r1", upstream.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain { Name = "solo", Steps = { new ChainStep { RequestId = "r1" } } });
            });
            try
            {
                int code = CliApp.Run(new[] { "run", "--chain", "solo", "--workspace", ws },
                                      new StringWriter(), new StringWriter(), services: Services(client, ws));
                Assert.Equal(0, code);
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task Exit_code_is_1_when_a_chain_step_fails()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var down = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 99, failStatus: 500);
            var ws = Workspace(state =>
            {
                state.Collections.Add(Request("login", "r1", down.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain { Name = "solo", Steps = { new ChainStep { RequestId = "r1" } } });
            });
            try
            {
                int code = CliApp.Run(new[] { "run", "--chain", "solo", "--workspace", ws },
                                      new StringWriter(), new StringWriter(), services: Services(client, ws));
                Assert.Equal(1, code);
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public void Exit_code_is_3_for_an_unknown_chain_and_the_message_lists_the_chains_that_exist()
    {
        var ws = Workspace(state =>
        {
            state.Chains.Add(new RequestChain { Name = "alpha" });
            state.Chains.Add(new RequestChain { Name = "beta" });
        });
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(new[] { "run", "--chain", "missing", "--workspace", ws },
                                  new StringWriter(), se, services: new CliServices { IsGuiRunning = () => false });
            Assert.Equal(3, code);
            Assert.Contains("No chain named 'missing'", se.ToString());
            Assert.Contains("alpha", se.ToString());
            Assert.Contains("beta", se.ToString());
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public void Exit_code_is_3_for_an_unknown_chain_and_says_none_when_no_chains_exist()
    {
        var ws = Workspace(_ => { });
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(new[] { "run", "--chain", "missing", "--workspace", ws },
                                  new StringWriter(), se, services: new CliServices { IsGuiRunning = () => false });
            Assert.Equal(3, code);
            Assert.Contains("(none)", se.ToString());
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public void Exit_code_is_3_for_a_chain_with_no_steps()
    {
        var ws = Workspace(state => state.Chains.Add(new RequestChain { Name = "empty" }));
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(new[] { "run", "--chain", "empty", "--workspace", ws },
                                  new StringWriter(), se, services: new CliServices { IsGuiRunning = () => false });
            Assert.Equal(3, code);
            Assert.Contains("empty", se.ToString());
            Assert.Contains("no steps", se.ToString());
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public async Task Exit_code_is_3_when_a_step_references_a_deleted_request_and_nothing_is_sent()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var ws = Workspace(state =>
            {
                state.Collections.Add(Request("first", "r1", upstream.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain
                {
                    Name = "broken",
                    Steps = { new ChainStep { RequestId = "r1" }, new ChainStep { RequestId = "missing-id" } }
                });
            });
            try
            {
                var se = new StringWriter();
                int code = CliApp.Run(new[] { "run", "--chain", "broken", "--workspace", ws },
                                      new StringWriter(), se, services: Services(client, ws));
                Assert.Equal(3, code);
                Assert.Contains("step 2", se.ToString());          // 1-based
                Assert.Contains("missing-id", se.ToString());
                // Resolution happens up front: step 1 never went out over the wire either.
                Assert.Equal(0, upstream.RequestCount);
            }
            finally { File.Delete(ws); }
        }
    }

    // ---------------------------------------------------------------- G. --json envelope

    [Fact]
    public async Task Json_results_array_has_one_element_per_step_that_ran_with_the_exact_property_set()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var down = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 99, failStatus: 500);
            await using var later = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var ws = Workspace(state =>
            {
                state.Collections.Add(Request("login", "r1", down.BaseUrl, client.Thumbprint!));
                state.Collections.Add(Request("browse", "r2", later.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain
                {
                    Name = "halts",
                    Steps = { new ChainStep { RequestId = "r1" }, new ChainStep { RequestId = "r2" } }
                });
            });
            try
            {
                var so = new StringWriter();
                CliApp.Run(new[] { "run", "--chain", "halts", "--workspace", ws, "--json" },
                          so, new StringWriter(), services: Services(client, ws));
                using var doc = JsonDocument.Parse(so.ToString());
                var results = doc.RootElement.GetProperty("results").EnumerateArray().ToList();
                Assert.Single(results);   // the skipped step contributes nothing to the array

                var names = results[0].EnumerateObject().Select(p => p.Name).ToHashSet();
                Assert.Equal(
                    new HashSet<string> { "path", "method", "url", "status", "elapsedMs", "sizeBytes", "passed", "assertions", "error" },
                    names);
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task Json_path_is_the_step_label_and_url_is_the_resolved_url_not_the_unresolved_token()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var node = Request("templated", "r1", upstream.BaseUrl, client.Thumbprint!);
            node.Request!.QueryParams.Add(new ParamRow { Key = "tok", Value = "{{tok}}" });
            var ws = Workspace(state =>
            {
                state.Collections.Add(node);
                state.Chains.Add(new RequestChain { Name = "solo", Steps = { new ChainStep { RequestId = "r1" } } });
            });
            try
            {
                var so = new StringWriter();
                int code = CliApp.Run(
                    new[] { "run", "--chain", "solo", "--workspace", ws, "--var", "tok=resolved-value", "--json" },
                    so, new StringWriter(), services: Services(client, ws));
                Assert.Equal(0, code);

                using var doc = JsonDocument.Parse(so.ToString());
                var result = doc.RootElement.GetProperty("results")[0];
                Assert.Equal("solo/1. templated", result.GetProperty("path").GetString());
                string url = result.GetProperty("url").GetString()!;
                Assert.Contains("tok=resolved-value", url);
                Assert.DoesNotContain("{{tok}}", url);
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task Json_assertions_field_is_null_when_the_request_has_no_enabled_assertions()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var ws = Workspace(state =>
            {
                state.Collections.Add(Request("ping", "r1", upstream.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain { Name = "solo", Steps = { new ChainStep { RequestId = "r1" } } });
            });
            try
            {
                var so = new StringWriter();
                CliApp.Run(new[] { "run", "--chain", "solo", "--workspace", ws, "--json" },
                          so, new StringWriter(), services: Services(client, ws));
                using var doc = JsonDocument.Parse(so.ToString());
                var assertions = doc.RootElement.GetProperty("results")[0].GetProperty("assertions");
                Assert.Equal(JsonValueKind.Null, assertions.ValueKind);
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task Json_assertions_field_carries_Description_Passed_and_actual_for_an_enabled_assertion()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var node = Request("checked", "r1", upstream.BaseUrl, client.Thumbprint!);
            node.Request!.Assertions.Add(new AssertionRule { Target = AssertTarget.Status, Op = AssertOp.Equals, Value = "200" });
            var ws = Workspace(state =>
            {
                state.Collections.Add(node);
                state.Chains.Add(new RequestChain { Name = "solo", Steps = { new ChainStep { RequestId = "r1" } } });
            });
            try
            {
                var so = new StringWriter();
                int code = CliApp.Run(new[] { "run", "--chain", "solo", "--workspace", ws, "--json" },
                                      so, new StringWriter(), services: Services(client, ws));
                Assert.Equal(0, code);

                using var doc = JsonDocument.Parse(so.ToString());
                var assertions = doc.RootElement.GetProperty("results")[0].GetProperty("assertions");
                Assert.Equal(JsonValueKind.Array, assertions.ValueKind);
                var only = Assert.Single(assertions.EnumerateArray());
                // Casing pinned exactly as the current serializer emits it: PascalCase for the two
                // members named straight from the record, camelCase for "actual" (renamed in the
                // anonymous projection).
                Assert.Equal("Status == 200", only.GetProperty("Description").GetString());
                Assert.True(only.GetProperty("Passed").GetBoolean());
                Assert.Equal("200", only.GetProperty("actual").GetString());
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task Json_summary_has_no_skipped_key_when_nothing_was_skipped()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var ws = Workspace(state =>
            {
                state.Collections.Add(Request("ping", "r1", upstream.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain { Name = "solo", Steps = { new ChainStep { RequestId = "r1" } } });
            });
            try
            {
                var so = new StringWriter();
                CliApp.Run(new[] { "run", "--chain", "solo", "--workspace", ws, "--json" },
                          so, new StringWriter(), services: Services(client, ws));
                using var doc = JsonDocument.Parse(so.ToString());
                var summary = doc.RootElement.GetProperty("summary");
                Assert.True(summary.TryGetProperty("total", out _));
                Assert.True(summary.TryGetProperty("passed", out _));
                Assert.True(summary.TryGetProperty("failed", out _));
                Assert.True(summary.TryGetProperty("elapsedMs", out _));
                Assert.False(summary.TryGetProperty("skipped", out _));
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task Json_summary_gains_a_skipped_key_and_the_skipped_step_is_not_counted_in_total_or_passed()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var down = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 99, failStatus: 500);
            await using var later = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var ws = Workspace(state =>
            {
                state.Collections.Add(Request("login", "r1", down.BaseUrl, client.Thumbprint!));
                state.Collections.Add(Request("browse", "r2", later.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain
                {
                    Name = "halts",
                    Steps = { new ChainStep { RequestId = "r1" }, new ChainStep { RequestId = "r2" } }
                });
            });
            try
            {
                var so = new StringWriter();
                CliApp.Run(new[] { "run", "--chain", "halts", "--workspace", ws, "--json" },
                          so, new StringWriter(), services: Services(client, ws));
                using var doc = JsonDocument.Parse(so.ToString());
                var summary = doc.RootElement.GetProperty("summary");
                Assert.Equal(1, summary.GetProperty("skipped").GetInt32());
                Assert.Equal(1, summary.GetProperty("total").GetInt32());     // the skipped step ran nothing
                Assert.Equal(0, summary.GetProperty("passed").GetInt32());    // and is never "passed"
                Assert.Equal(1, summary.GetProperty("failed").GetInt32());
            }
            finally { File.Delete(ws); }
        }
    }

    // ---------------------------------------------------------------- H. known-good recording, per step

    [Fact]
    public async Task Known_good_recording_happens_per_step_of_a_two_step_chain()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var s1 = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            await using var s2 = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var ws = Workspace(state =>
            {
                state.Collections.Add(Request("one", "r1", s1.BaseUrl, client.Thumbprint!));
                state.Collections.Add(Request("two", "r2", s2.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain
                {
                    Name = "pair",
                    Steps = { new ChainStep { RequestId = "r1" }, new ChainStep { RequestId = "r2" } }
                });
            });
            try
            {
                int code = CliApp.Run(new[] { "run", "--chain", "pair", "--workspace", ws, "--record" },
                                      new StringWriter(), new StringWriter(), services: Services(client, ws));
                Assert.Equal(0, code);

                var back = AppState.LoadFrom(ws);
                Assert.Equal(200, back.Collections[0].LastStatusCode);
                Assert.NotNull(back.Collections[0].KnownGood);
                Assert.Equal(200, back.Collections[1].LastStatusCode);
                Assert.NotNull(back.Collections[1].KnownGood);
            }
            finally { File.Delete(ws); }
        }
    }

    // ---------------------------------------------------------------- I. captured values land in the target environment

    [Fact]
    public async Task A_chain_creates_its_named_environment_and_the_capture_lands_there_as_the_active_environment()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var login = await LoopbackMtlsServer.StartOAuthTokenAsync(server, client.Thumbprint!, "cid", "shh");
            var ws = Workspace(state =>
            {
                state.Collections.Add(LoginRequest("r1", login.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain
                {
                    Name = "fresh", EnvironmentName = "Chain Made This",
                    Steps = { new ChainStep { RequestId = "r1" } }
                });
            });
            try
            {
                int code = CliApp.Run(new[] { "run", "--chain", "fresh", "--workspace", ws },
                                      new StringWriter(), new StringWriter(), services: Services(client, ws));
                Assert.Equal(0, code);

                var back = AppState.LoadFrom(ws);
                var env = Assert.Single(back.Environments, e => e.Name == "Chain Made This");
                Assert.Equal("at-client_credentials", env.Variables.Single(v => v.Key == "token").Value);
                Assert.Equal(env.Id, back.ActiveEnvironmentId);
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task Explicit_env_flag_wins_over_the_chains_own_environment_and_the_stored_default_is_never_created()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var login = await LoopbackMtlsServer.StartOAuthTokenAsync(server, client.Thumbprint!, "cid", "shh");
            var ws = Workspace(state =>
            {
                state.Environments.Add(new ApiEnvironment { Id = "e-explicit", Name = "Explicit" });
                state.Collections.Add(LoginRequest("r1", login.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain
                {
                    Name = "override me", EnvironmentName = "Stored Default",
                    Steps = { new ChainStep { RequestId = "r1" } }
                });
            });
            try
            {
                int code = CliApp.Run(new[] { "run", "--chain", "override me", "--workspace", ws, "--env", "Explicit" },
                                      new StringWriter(), new StringWriter(), services: Services(client, ws));
                Assert.Equal(0, code);

                var back = AppState.LoadFrom(ws);
                var env = Assert.Single(back.Environments, e => e.Name == "Explicit");
                Assert.Equal("at-client_credentials", env.Variables.Single(v => v.Key == "token").Value);
                Assert.DoesNotContain(back.Environments, e => e.Name == "Stored Default");
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task A_captured_value_crosses_to_the_next_step_through_what_the_second_server_received()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var login = await LoopbackMtlsServer.StartOAuthTokenAsync(server, client.Thumbprint!, "cid", "shh");
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);

            var step1 = LoginRequest("r-login", login.BaseUrl, client.Thumbprint!);
            var step2 = Request("call the API", "r-call", api.BaseUrl, client.Thumbprint!);
            step2.Request!.Headers.Add(new HeaderRow { Name = "Authorization", Value = "Bearer {{token}}" });

            var har = Path.Combine(Path.GetTempPath(), $"certapi-charac-{Guid.NewGuid():N}.har");
            var ws = Workspace(state =>
            {
                state.Collections.Add(step1);
                state.Collections.Add(step2);
                state.Chains.Add(new RequestChain
                {
                    Name = "login then call", EnvironmentName = "ChainEnv",
                    Steps = { new ChainStep { RequestId = "r-login" }, new ChainStep { RequestId = "r-call" } }
                });
            });
            try
            {
                // --no-auto-token leaves the chain's own capture as the only way a token can reach the
                // second server, so a pass here cannot be the automatic session-token machinery.
                int code = CliApp.Run(
                    new[] { "run", "--chain", "login then call", "--workspace", ws, "--no-auto-token", "--har", har },
                    new StringWriter(), new StringWriter(), services: Services(client, ws));
                Assert.Equal(0, code);

                using var doc = JsonDocument.Parse(File.ReadAllText(har));
                var entries = doc.RootElement.GetProperty("log").GetProperty("entries").EnumerateArray().ToList();
                string echoed = entries[^1].GetProperty("response").GetProperty("content").GetProperty("text").GetString()!;
                Assert.Contains("Authorization: Bearer at-client_credentials", echoed);
            }
            finally { File.Delete(ws); File.Delete(har); }
        }
    }

    // ---------------------------------------------------------------- J. StopOnFailure, both directions

    [Fact]
    public async Task StopOnFailure_true_halts_the_chain_before_the_next_step_is_sent()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var down = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 99, failStatus: 500);
            await using var later = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var ws = Workspace(state =>
            {
                state.Collections.Add(Request("login", "r1", down.BaseUrl, client.Thumbprint!));
                state.Collections.Add(Request("browse", "r2", later.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain
                {
                    Name = "halts",
                    // StopOnFailure defaults to true — not set explicitly, on purpose.
                    Steps = { new ChainStep { RequestId = "r1" }, new ChainStep { RequestId = "r2" } }
                });
            });
            try
            {
                int code = CliApp.Run(new[] { "run", "--chain", "halts", "--workspace", ws },
                                      new StringWriter(), new StringWriter(), services: Services(client, ws));
                Assert.Equal(1, code);
                Assert.Equal(0, later.RequestCount);
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task StopOnFailure_false_lets_the_chain_carry_on_after_a_failing_step()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var down = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 99, failStatus: 500);
            await using var later = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var ws = Workspace(state =>
            {
                state.Collections.Add(Request("optional", "r1", down.BaseUrl, client.Thumbprint!));
                state.Collections.Add(Request("browse", "r2", later.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain
                {
                    Name = "carries on",
                    Steps =
                    {
                        new ChainStep { RequestId = "r1", StopOnFailure = false },
                        new ChainStep { RequestId = "r2" }
                    }
                });
            });
            try
            {
                var so = new StringWriter();
                int code = CliApp.Run(new[] { "run", "--chain", "carries on", "--workspace", ws },
                                      so, new StringWriter(), services: Services(client, ws));
                Assert.Equal(1, code);                      // a step still failed
                Assert.Equal(1, later.RequestCount);        // but step 2 was sent
                Assert.DoesNotContain("SKIP", so.ToString());
            }
            finally { File.Delete(ws); }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static (X509Certificate2 ca, X509Certificate2 server, X509Certificate2 client) Certs()
    {
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        var server = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        var client = SelfSignedCertificateFactory.CreateSignedCertificate("ChainClient", ca, false, true);
        return (ca, server, client);
    }

    private static CliServices Services(X509Certificate2 client, string livePath) => new()
    {
        FindCertificate = _ => client,
        IsGuiRunning = () => false,
        LiveStatePath = livePath
    };

    /// <summary>A saved request as a top-level collection node with a known id, so a chain step can
    /// point at it the way the GUI will.</summary>
    private static CollectionNode Request(string name, string id, string url, string certThumbprint) =>
        new()
        {
            Id = id, Name = name, IsFolder = false,
            Request = new RequestModel
            {
                Method = "GET", Path = url, IgnoreServerCert = true, CertThumbprint = certThumbprint
            }
        };

    /// <summary>A saved request that logs in against <see cref="LoopbackMtlsServer.StartOAuthTokenAsync"/>
    /// and captures the issued access_token into {{token}} — the first half of every chain here.</summary>
    private static CollectionNode LoginRequest(string id, string url, string certThumbprint)
    {
        var node = Request("login", id, url, certThumbprint);
        node.Request!.Method = "POST";
        node.Request.ContentType = "application/x-www-form-urlencoded";
        node.Request.Body = "grant_type=client_credentials&client_id=cid&client_secret=shh";
        node.Request.Captures.Add(new CaptureRule { Variable = "token", Source = CaptureSource.Body, Path = "access_token" });
        return node;
    }

    private static string Workspace(Action<AppState> build)
    {
        var state = new AppState();
        build(state);
        var path = Path.Combine(Path.GetTempPath(), $"certapi-charac-{Guid.NewGuid():N}.json");
        state.SaveTo(path);
        return path;
    }

    /// <summary>Stdout's lines, normalized for the mixed line endings the plain-text report actually
    /// produces (the footer embeds a bare '\n' between "----" and the summary inside one WriteLine
    /// call, while every other line ends with WriteLine's own Environment.NewLine).</summary>
    private static List<string> Lines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
}
