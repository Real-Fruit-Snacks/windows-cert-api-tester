using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>Request chains: an ordered sequence of saved requests run as one unit, where a value
/// captured by one step is usable by the next. The point of the feature is that "log in, then call
/// the API" runs the same way a suite does, in a stated order — so these tests prove the crossing
/// of the variable through what the second server actually received, not through the state file.</summary>
public class ChainCliTests
{
    // ---------------------------------------------------------------- model & persistence

    [Fact]
    public void Chain_defaults_are_an_empty_step_list_and_a_stopping_step()
    {
        var chain = new RequestChain();
        Assert.Empty(chain.Steps);
        Assert.Equal(32, chain.Id.Length);          // Guid "n" — 32 hex characters, no dashes
        Assert.Null(chain.EnvironmentName);
        // Calling the API after the login failed produces a confusing 401, not a useful result.
        Assert.True(new ChainStep().StopOnFailure);
    }

    [Fact]
    public void Chains_round_trip_through_the_workspace_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"certapi-chain-rt-{Guid.NewGuid():N}.json");
        try
        {
            var state = new AppState
            {
                Chains =
                {
                    new RequestChain
                    {
                        Id = "c1", Name = "login then browse", EnvironmentName = "Staging",
                        Steps =
                        {
                            new ChainStep { RequestId = "r1" },
                            new ChainStep { RequestId = "r2", StopOnFailure = false }
                        }
                    }
                }
            };
            state.SaveTo(path);

            var chain = Assert.Single(AppState.LoadFrom(path).Chains);
            Assert.Equal("c1", chain.Id);
            Assert.Equal("login then browse", chain.Name);
            Assert.Equal("Staging", chain.EnvironmentName);
            Assert.Equal(2, chain.Steps.Count);
            Assert.Equal("r1", chain.Steps[0].RequestId);      // order is the whole point
            Assert.True(chain.Steps[0].StopOnFailure);
            Assert.Equal("r2", chain.Steps[1].RequestId);
            Assert.False(chain.Steps[1].StopOnFailure);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_state_file_written_before_chains_existed_loads_with_none()
    {
        var path = Path.Combine(Path.GetTempPath(), $"certapi-chain-old-{Guid.NewGuid():N}.json");
        try
        {
            // Hand-written: no Chains property at all, exactly as an older certapi would have left it.
            File.WriteAllText(path, """
                {
                  "SchemaVersion": 1,
                  "Collections": [ { "Id": "n1", "IsFolder": false, "Name": "ping" } ],
                  "Environments": []
                }
                """);

            var back = AppState.LoadFrom(path);
            Assert.Empty(back.Chains);
            Assert.Equal("ping", Assert.Single(back.Collections).Name);   // the rest still loaded
        }
        finally { File.Delete(path); }
    }

    // ---------------------------------------------------------------- resolution & errors

    [Fact]
    public void An_unknown_chain_lists_the_chains_that_do_exist()
    {
        var ws = Workspace(state =>
        {
            state.Chains.Add(new RequestChain { Name = "login then browse" });
            state.Chains.Add(new RequestChain { Name = "nightly" });
        });
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(new[] { "run", "--chain", "nope", "--workspace", ws },
                                  new StringWriter(), se, services: new CliServices { IsGuiRunning = () => false });
            Assert.Equal(3, code);
            Assert.Contains("No chain named 'nope'", se.ToString());
            Assert.Contains("login then browse", se.ToString());
            Assert.Contains("nightly", se.ToString());
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public void An_unknown_chain_with_no_chains_at_all_says_none()
    {
        var ws = Workspace(_ => { });
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(new[] { "run", "--chain", "nope", "--workspace", ws },
                                  new StringWriter(), se, services: new CliServices { IsGuiRunning = () => false });
            Assert.Equal(3, code);
            Assert.Contains("(none)", se.ToString());
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public void An_empty_chain_is_a_data_error()
    {
        var ws = Workspace(state => state.Chains.Add(new RequestChain { Name = "hollow" }));
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(new[] { "run", "--chain", "hollow", "--workspace", ws },
                                  new StringWriter(), se, services: new CliServices { IsGuiRunning = () => false });
            Assert.Equal(3, code);
            Assert.Contains("hollow", se.ToString());
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public async Task A_step_pointing_at_a_deleted_request_fails_before_anything_is_sent()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            // failures: 0 → every request answered 200, and RequestCount is the server's own record of
            // how many it actually answered. A chain that fails up front must leave it at zero.
            await using var upstream = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var ws = Workspace(state =>
            {
                state.Collections.Add(Request("first", "r1", upstream.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain
                {
                    Name = "broken",
                    Steps = { new ChainStep { RequestId = "r1" }, new ChainStep { RequestId = "gone-42" } }
                });
            });
            try
            {
                var se = new StringWriter();
                int code = CliApp.Run(new[] { "run", "--chain", "broken", "--workspace", ws },
                                      new StringWriter(), se, services: Services(client, ws));
                Assert.Equal(3, code);
                Assert.Contains("step 2", se.ToString());        // 1-based, names the step
                Assert.Contains("gone-42", se.ToString());
                Assert.Equal(0, upstream.RequestCount);          // step 1 never went out either
            }
            finally { File.Delete(ws); }
        }
    }

    [Theory]
    [InlineData("run", "--chain", "c", "some/request")]
    [InlineData("run", "--chain", "c", "--all")]
    public void A_chain_cannot_be_combined_with_a_positional_or_all(params string[] argv)
    {
        var se = new StringWriter();
        int code = CliApp.Run(argv, new StringWriter(), se, services: new CliServices());
        Assert.Equal(2, code);
        Assert.Contains("--chain names the chain to run", se.ToString());
        Assert.Contains("cannot be combined", se.ToString());
    }

    [Fact]
    public void A_chain_cannot_be_combined_with_data()
    {
        var csv = Path.Combine(Path.GetTempPath(), $"certapi-chain-{Guid.NewGuid():N}.csv");
        File.WriteAllText(csv, "id\n1\n");
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(new[] { "run", "--chain", "c", "--data", csv },
                                  new StringWriter(), se, services: new CliServices());
            Assert.Equal(2, code);
            Assert.Contains("--chain", se.ToString());
            Assert.Contains("--data", se.ToString());
        }
        finally { File.Delete(csv); }
    }

    // ---------------------------------------------------------------- execution

    [Fact]
    public async Task A_chain_carries_a_captured_token_to_the_next_step()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var login = await LoopbackMtlsServer.StartOAuthTokenAsync(server, client.Thumbprint!, "cid", "shh");
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);

            var step1 = LoginRequest("r-login", login.BaseUrl, client.Thumbprint!);
            var step2 = Request("call the API", "r-call", api.BaseUrl, client.Thumbprint!);
            step2.Request!.Headers.Add(new HeaderRow { Name = "Authorization", Value = "Bearer {{token}}" });

            var har = Path.Combine(Path.GetTempPath(), $"certapi-chain-{Guid.NewGuid():N}.har");
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
                var so = new StringWriter(); var se = new StringWriter();
                // --no-auto-token leaves the chain's own capture as the only way a token can reach the
                // second server, so a pass here cannot be the automatic session-token machinery.
                int code = CliApp.Run(
                    new[] { "run", "--chain", "login then call", "--workspace", ws, "--record", "--no-auto-token", "--har", har },
                    so, se, services: Services(client, ws));
                Assert.Equal(0, code);
                Assert.Contains("login then call/1. login", so.ToString());
                Assert.Contains("login then call/2. call the API", so.ToString());

                // The fact that matters: what the second server actually received. The echo server
                // answers with the request it was given, so the token text in that body is proof the
                // {{token}} resolved to the value step 1 captured — a header that resolved to an empty
                // string would have been answered 200 just the same.
                using var doc = JsonDocument.Parse(File.ReadAllText(har));
                var entries = doc.RootElement.GetProperty("log").GetProperty("entries").EnumerateArray().ToList();
                string echoed = entries[^1].GetProperty("response").GetProperty("content").GetProperty("text").GetString()!;
                Assert.Contains("Authorization: Bearer at-client_credentials", echoed);

                // Known-good recording applies per step exactly as in a normal run.
                var back = AppState.LoadFrom(ws);
                Assert.Equal(200, back.Collections[0].LastStatusCode);
                Assert.NotNull(back.Collections[1].KnownGood);
            }
            finally { File.Delete(ws); File.Delete(har); }
        }
    }

    [Fact]
    public async Task A_failing_step_stops_the_chain_and_the_rest_are_skipped()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            // failures: 99 → never succeeds; failures: 0 → would answer 200 if it were ever asked.
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
                var so = new StringWriter(); var se = new StringWriter();
                int code = CliApp.Run(new[] { "run", "--chain", "halts", "--workspace", ws },
                                      so, se, services: Services(client, ws));
                Assert.Equal(1, code);
                Assert.Contains("FAIL", so.ToString());
                Assert.Contains("SKIP", so.ToString());
                Assert.Contains("halts/2. browse", so.ToString());
                // The second server's own record: it was never asked, so the step really did not run.
                Assert.Equal(0, later.RequestCount);
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task A_step_that_does_not_stop_on_failure_lets_the_chain_carry_on()
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
                var so = new StringWriter(); var se = new StringWriter();
                int code = CliApp.Run(new[] { "run", "--chain", "carries on", "--workspace", ws },
                                      so, se, services: Services(client, ws));
                Assert.Equal(1, code);                       // a step still failed
                Assert.Equal(1, later.RequestCount);         // but step 2 was sent
                Assert.DoesNotContain("SKIP", so.ToString());
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task Skipped_steps_are_counted_in_the_json_summary_only_when_there_are_any()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var down = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 99, failStatus: 500);
            await using var up = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var halting = Workspace(state =>
            {
                state.Collections.Add(Request("login", "r1", down.BaseUrl, client.Thumbprint!));
                state.Collections.Add(Request("browse", "r2", up.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain
                {
                    Name = "halts",
                    Steps = { new ChainStep { RequestId = "r1" }, new ChainStep { RequestId = "r2" } }
                });
            });
            var clean = Workspace(state =>
            {
                state.Collections.Add(Request("browse", "r2", up.BaseUrl, client.Thumbprint!));
                state.Chains.Add(new RequestChain { Name = "clean", Steps = { new ChainStep { RequestId = "r2" } } });
            });
            try
            {
                var so = new StringWriter();
                CliApp.Run(new[] { "run", "--chain", "halts", "--workspace", halting, "--json" },
                           so, new StringWriter(), services: Services(client, halting));
                using var halted = JsonDocument.Parse(so.ToString());
                var summary = halted.RootElement.GetProperty("summary");
                Assert.Equal(1, summary.GetProperty("skipped").GetInt32());
                Assert.Equal(1, summary.GetProperty("total").GetInt32());     // skipped steps ran nothing
                Assert.Equal(0, summary.GetProperty("passed").GetInt32());    // and are never "passed"

                var so2 = new StringWriter();
                CliApp.Run(new[] { "run", "--chain", "clean", "--workspace", clean, "--json" },
                           so2, new StringWriter(), services: Services(client, clean));
                using var ok = JsonDocument.Parse(so2.ToString());
                // An ordinary run's envelope is unchanged — no stray key for a count of zero.
                Assert.False(ok.RootElement.GetProperty("summary").TryGetProperty("skipped", out _));
            }
            finally { File.Delete(halting); File.Delete(clean); }
        }
    }

    // ---------------------------------------------------------------- environment

    [Fact]
    public async Task A_chain_creates_the_environment_it_names_and_captures_land_there()
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
                    Name = "fresh", EnvironmentName = "Made By The Chain",
                    Steps = { new ChainStep { RequestId = "r1" } }
                });
            });
            try
            {
                int code = CliApp.Run(new[] { "run", "--chain", "fresh", "--workspace", ws },
                                      new StringWriter(), new StringWriter(), services: Services(client, ws));
                Assert.Equal(0, code);

                var back = AppState.LoadFrom(ws);
                var env = Assert.Single(back.Environments, e => e.Name == "Made By The Chain");
                Assert.Equal("at-client_credentials", env.Variables.Single(v => v.Key == "token").Value);
                Assert.Equal(env.Id, back.ActiveEnvironmentId);
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task An_explicit_env_flag_beats_the_chains_own_environment()
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
                // The stored default was not even created — the typed flag is the whole instruction.
                Assert.DoesNotContain(back.Environments, e => e.Name == "Stored Default");
            }
            finally { File.Delete(ws); }
        }
    }

    // ---------------------------------------------------------------- shared per-request path

    [Fact]
    public async Task An_ordinary_collection_run_still_captures_records_and_reports_assertions()
    {
        // The chain runner and the collection runner share one per-request path; this exercises that
        // path from the collection side, so an extraction that quietly changed a suite would show up.
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var login = await LoopbackMtlsServer.StartOAuthTokenAsync(server, client.Thumbprint!, "cid", "shh");
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);

            var second = Request("call", "r2", api.BaseUrl, client.Thumbprint!);
            second.Request!.Headers.Add(new HeaderRow { Name = "Authorization", Value = "Bearer {{token}}" });
            second.Request.Assertions.Add(new AssertionRule
            {
                Target = AssertTarget.Status, Op = AssertOp.Equals, Value = "418"   // cannot pass
            });

            var har = Path.Combine(Path.GetTempPath(), $"certapi-suite-{Guid.NewGuid():N}.har");
            var ws = Workspace(state =>
            {
                state.Environments.Add(new ApiEnvironment { Id = "e1", Name = "Shared" });
                var folder = new CollectionNode { Name = "suite", IsFolder = true };
                folder.Children.Add(LoginRequest("r1", login.BaseUrl, client.Thumbprint!));
                folder.Children.Add(second);
                state.Collections.Add(folder);
            });
            try
            {
                var so = new StringWriter(); var se = new StringWriter();
                int code = CliApp.Run(
                    new[] { "run", "suite", "--workspace", ws, "--record", "--env", "Shared", "--no-auto-token", "--har", har },
                    so, se, services: Services(client, ws));

                Assert.Equal(1, code);                                        // the assertion failed
                Assert.Contains("assertion failed", se.ToString());           // and was reported
                Assert.Contains("suite/call", so.ToString());                 // under its collection path

                using var doc = JsonDocument.Parse(File.ReadAllText(har));
                var entries = doc.RootElement.GetProperty("log").GetProperty("entries").EnumerateArray().ToList();
                string echoed = entries[^1].GetProperty("response").GetProperty("content").GetProperty("text").GetString()!;
                Assert.Contains("Authorization: Bearer at-client_credentials", echoed);   // token crossed

                var back = AppState.LoadFrom(ws);
                Assert.Equal(200, back.Collections[0].Children[0].LastStatusCode);
                Assert.NotNull(back.Collections[0].Children[0].KnownGood);    // known-good still recorded
            }
            finally { File.Delete(ws); File.Delete(har); }
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
        var path = Path.Combine(Path.GetTempPath(), $"certapi-chain-{Guid.NewGuid():N}.json");
        state.SaveTo(path);
        return path;
    }
}
