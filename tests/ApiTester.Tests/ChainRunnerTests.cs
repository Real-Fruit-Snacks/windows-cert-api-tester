using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Drives <see cref="ChainRunner"/> directly — no <c>ApiTester.Cli</c> types at all — since
/// this is the exact contract the desktop application's chain window will depend on next slice.
/// <c>certapi run --chain</c>'s observable behavior is already pinned by
/// <c>Cli/ChainCharacterizationTests.cs</c>; these tests cover only the new public Core surface.</summary>
public class ChainRunnerTests
{
    // ---------------------------------------------------------------- PrepareCaptureEnvironment

    [Fact]
    public void PrepareCaptureEnvironment_creates_the_named_environment_makes_it_active_and_returns_its_name()
    {
        var state = new AppState();
        var chain = new RequestChain { Name = "fresh", EnvironmentName = "Fresh Env" };

        string? name = ChainRunner.PrepareCaptureEnvironment(state, chain);

        Assert.Equal("Fresh Env", name);
        var env = Assert.Single(state.Environments);
        Assert.Equal("Fresh Env", env.Name);
        Assert.Equal(env.Id, state.ActiveEnvironmentId);
    }

    [Fact]
    public void PrepareCaptureEnvironment_returns_null_and_creates_nothing_when_the_chain_names_none()
    {
        var state = new AppState();
        var chain = new RequestChain { Name = "no-env" };

        string? name = ChainRunner.PrepareCaptureEnvironment(state, chain);

        Assert.Null(name);
        Assert.Empty(state.Environments);
        Assert.Null(state.ActiveEnvironmentId);
    }

    [Fact]
    public void PrepareCaptureEnvironment_reuses_an_existing_environment_matched_case_insensitively()
    {
        var state = new AppState();
        var existing = new ApiEnvironment { Id = "e-staging", Name = "Staging" };
        state.Environments.Add(existing);
        var chain = new RequestChain { Name = "reuse", EnvironmentName = "STAGING" };

        string? name = ChainRunner.PrepareCaptureEnvironment(state, chain);

        Assert.Equal("STAGING", name);
        Assert.Single(state.Environments);           // no second environment was created
        Assert.Equal("e-staging", state.ActiveEnvironmentId);
    }

    // ---------------------------------------------------------------- Find

    [Fact]
    public void Find_throws_naming_the_chains_that_exist()
    {
        var state = new AppState();
        state.Chains.Add(new RequestChain { Name = "alpha" });
        state.Chains.Add(new RequestChain { Name = "beta" });

        var ex = Assert.Throws<ChainRunException>(() => ChainRunner.Find(state, "missing"));

        Assert.Contains("No chain named 'missing'", ex.Message);
        Assert.Contains("alpha", ex.Message);
        Assert.Contains("beta", ex.Message);
    }

    [Fact]
    public void Find_says_none_when_there_are_no_chains()
    {
        var state = new AppState();

        var ex = Assert.Throws<ChainRunException>(() => ChainRunner.Find(state, "missing"));

        Assert.Contains("(none)", ex.Message);
    }

    // ---------------------------------------------------------------- Resolve

    [Fact]
    public void Resolve_throws_for_a_chain_with_no_steps()
    {
        var state = new AppState();
        var chain = new RequestChain { Name = "empty" };

        var ex = Assert.Throws<ChainRunException>(() => ChainRunner.Resolve(state, chain));

        Assert.Contains("empty", ex.Message);
        Assert.Contains("no steps", ex.Message);
    }

    [Fact]
    public void Resolve_throws_for_a_step_whose_request_id_no_longer_resolves()
    {
        var state = new AppState();
        state.Collections.Add(Request("first", "r1", "https://example.test/", "thumb"));
        var chain = new RequestChain
        {
            Name = "broken",
            Steps = { new ChainStep { RequestId = "r1" }, new ChainStep { RequestId = "missing-id" } }
        };

        var ex = Assert.Throws<ChainRunException>(() => ChainRunner.Resolve(state, chain));

        Assert.Contains("step 2", ex.Message);       // 1-based
        Assert.Contains("missing-id", ex.Message);
    }

    [Fact]
    public void Resolve_matches_a_request_id_case_insensitively()
    {
        var state = new AppState();
        // Ids are Guid hex, written "N" (upper-cased here to make the point) by the collections tree
        // and "n" by a chain — the two spellings of the same id must still find each other.
        var node = Request("dup", "ABCD1234ABCD1234ABCD1234ABCD1234", "https://example.test/", "thumb");
        state.Collections.Add(node);
        var chain = new RequestChain
        {
            Name = "casing",
            Steps = { new ChainStep { RequestId = "abcd1234abcd1234abcd1234abcd1234" } }
        };

        var steps = ChainRunner.Resolve(state, chain);

        Assert.Same(node, Assert.Single(steps).Node);
    }

    [Fact]
    public void Resolve_labels_steps_with_the_chain_name_and_1_based_number_even_for_a_duplicate_request()
    {
        var state = new AppState();
        state.Collections.Add(Request("dup", "r1", "https://example.test/", "thumb"));
        var chain = new RequestChain
        {
            Name = "twice",
            Steps = { new ChainStep { RequestId = "r1" }, new ChainStep { RequestId = "r1" } }
        };

        var steps = ChainRunner.Resolve(state, chain);

        Assert.Equal(2, steps.Count);
        Assert.Equal(1, steps[0].Number);
        Assert.Equal("twice/1. dup", steps[0].Label);
        Assert.Equal(2, steps[1].Number);
        Assert.Equal("twice/2. dup", steps[1].Label);
    }

    // ---------------------------------------------------------------- RunAsync

    [Fact]
    public async Task RunAsync_reports_progress_once_per_step_in_order()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var a = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            await using var b = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var state = new AppState();
            state.Collections.Add(Request("one", "r1", a.BaseUrl, client.Thumbprint!));
            state.Collections.Add(Request("two", "r2", b.BaseUrl, client.Thumbprint!));
            var chain = new RequestChain
            {
                Name = "pair",
                Steps = { new ChainStep { RequestId = "r1" }, new ChainStep { RequestId = "r2" } }
            };
            var steps = ChainRunner.Resolve(state, chain);
            var ctx = NewContext(state, client);
            var vars = NewVars(state);
            var progress = new RecordingProgress<ChainRunProgress>();

            var result = await ChainRunner.RunAsync(steps, ctx, vars, progress, CancellationToken.None);

            Assert.Equal(2, progress.Reports.Count);
            Assert.Equal(1, progress.Reports[0].Number);
            Assert.Equal(2, progress.Reports[0].Total);
            Assert.Same(result.Steps[0], progress.Reports[0].Outcome);
            Assert.Equal(2, progress.Reports[1].Number);
            Assert.Equal(2, progress.Reports[1].Total);
            Assert.Same(result.Steps[1], progress.Reports[1].Outcome);
        }
    }

    [Fact]
    public async Task RunAsync_with_an_already_cancelled_token_throws_and_sends_nothing()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var state = new AppState();
            state.Collections.Add(Request("ping", "r1", upstream.BaseUrl, client.Thumbprint!));
            var chain = new RequestChain { Name = "solo", Steps = { new ChainStep { RequestId = "r1" } } };
            var steps = ChainRunner.Resolve(state, chain);
            var ctx = NewContext(state, client);
            var vars = NewVars(state);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => ChainRunner.RunAsync(steps, ctx, vars, null, cts.Token));
            Assert.Equal(0, upstream.RequestCount);
        }
    }

    [Fact]
    public async Task RunAsync_carries_a_captured_value_into_the_next_step_through_what_the_second_server_received()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var login = await LoopbackMtlsServer.StartOAuthTokenAsync(server, client.Thumbprint!, "cid", "shh");
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);

            var state = new AppState();
            state.Collections.Add(LoginRequest("r-login", login.BaseUrl, client.Thumbprint!));
            var step2 = Request("call the api", "r-call", api.BaseUrl, client.Thumbprint!);
            step2.Request!.Headers.Add(new HeaderRow { Name = "Authorization", Value = "Bearer {{token}}" });
            state.Collections.Add(step2);
            var chain = new RequestChain
            {
                Name = "login then call", EnvironmentName = "ChainEnv",
                Steps = { new ChainStep { RequestId = "r-login" }, new ChainStep { RequestId = "r-call" } }
            };
            ChainRunner.PrepareCaptureEnvironment(state, chain);
            var steps = ChainRunner.Resolve(state, chain);
            // NoAutoToken = true: the chain's own capture rule is the only way a token can reach the
            // second server, so a pass here cannot be the automatic session-token machinery.
            var ctx = NewContext(state, client, noAutoToken: true);
            var vars = NewVars(state);

            var result = await ChainRunner.RunAsync(steps, ctx, vars, null, CancellationToken.None);

            Assert.Equal(2, result.Steps.Count);
            string echoed = System.Text.Encoding.UTF8.GetString(result.Steps[1].Response.Body);
            Assert.Contains("Authorization: Bearer at-client_credentials", echoed);
        }
    }

    [Fact]
    public async Task RunAsync_halts_on_a_failing_step_marked_StopOnFailure_and_skips_the_rest()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var down = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 99, failStatus: 500);
            await using var later = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var state = new AppState();
            state.Collections.Add(Request("login", "r1", down.BaseUrl, client.Thumbprint!));
            state.Collections.Add(Request("browse", "r2", later.BaseUrl, client.Thumbprint!));
            var chain = new RequestChain
            {
                Name = "halts",
                // StopOnFailure defaults to true — not set explicitly, on purpose.
                Steps = { new ChainStep { RequestId = "r1" }, new ChainStep { RequestId = "r2" } }
            };
            var steps = ChainRunner.Resolve(state, chain);
            var ctx = NewContext(state, client);
            var vars = NewVars(state);

            var result = await ChainRunner.RunAsync(steps, ctx, vars, null, CancellationToken.None);

            Assert.Single(result.Steps);
            Assert.False(result.Steps[0].Passed);
            Assert.Equal(new[] { "halts/2. browse" }, result.SkippedLabels);
            Assert.Equal(0, later.RequestCount);
        }
    }

    [Fact]
    public async Task RunAsync_carries_on_after_a_failing_step_marked_not_StopOnFailure()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var down = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 99, failStatus: 500);
            await using var later = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);
            var state = new AppState();
            state.Collections.Add(Request("optional", "r1", down.BaseUrl, client.Thumbprint!));
            state.Collections.Add(Request("browse", "r2", later.BaseUrl, client.Thumbprint!));
            var chain = new RequestChain
            {
                Name = "carries on",
                Steps =
                {
                    new ChainStep { RequestId = "r1", StopOnFailure = false },
                    new ChainStep { RequestId = "r2" }
                }
            };
            var steps = ChainRunner.Resolve(state, chain);
            var ctx = NewContext(state, client);
            var vars = NewVars(state);

            var result = await ChainRunner.RunAsync(steps, ctx, vars, null, CancellationToken.None);

            Assert.Equal(2, result.Steps.Count);
            Assert.Empty(result.SkippedLabels);
            Assert.Equal(1, later.RequestCount);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static (X509Certificate2 ca, X509Certificate2 server, X509Certificate2 client) Certs()
    {
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        var server = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        var client = SelfSignedCertificateFactory.CreateSignedCertificate("ChainRunnerClient", ca, false, true);
        return (ca, server, client);
    }

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
    /// and captures the issued access_token into {{token}} — the first half of a two-step chain.</summary>
    private static CollectionNode LoginRequest(string id, string url, string certThumbprint)
    {
        var node = Request("login", id, url, certThumbprint);
        node.Request!.Method = "POST";
        node.Request.ContentType = "application/x-www-form-urlencoded";
        node.Request.Body = "grant_type=client_credentials&client_id=cid&client_secret=shh";
        node.Request.Captures.Add(new CaptureRule { Variable = "token", Source = CaptureSource.Body, Path = "access_token" });
        return node;
    }

    private static RequestRunContext NewContext(AppState state, X509Certificate2 clientCert, bool noAutoToken = false) => new()
    {
        State = state,
        Client = new ApiClient(),
        Predicates = new TrustPredicates(state),
        FindCertificate = _ => clientCert,
        Record = true,
        NoAutoToken = noAutoToken
    };

    /// <summary>Rebuilds from whatever environment is active, exactly the way the desktop
    /// application's chain window will: no command line involved anywhere.</summary>
    private static RunVariables NewVars(AppState state) => new(() =>
    {
        var vars = new Dictionary<string, string>();
        if (state.ActiveEnvironmentId is { } id &&
            state.Environments.FirstOrDefault(e => e.Id == id) is { } env)
            foreach (var v in env.Variables) vars[v.Key] = v.Value;
        return vars;
    });

    /// <summary>A synchronous <see cref="IProgress{T}"/> stub — never <see cref="Progress{T}"/>,
    /// which defers each report to the thread pool and would make an ordering assertion racy.</summary>
    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = new();
        public void Report(T value) => Reports.Add(value);
    }
}
