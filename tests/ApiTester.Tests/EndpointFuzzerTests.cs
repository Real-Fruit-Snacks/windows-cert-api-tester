using System.Net.Http;
using ApiTester.Core;

namespace ApiTester.Tests;

public class EndpointFuzzerTests
{
    private static FuzzPlan Plan(string list, IReadOnlyList<string>? methods = null, int conc = 8) => new()
    {
        BaseUrl = "https://api.example.com",
        Entries = EndpointList.Parse(list),
        Methods = methods ?? new[] { "GET" },
        Concurrency = conc
    };

    private static ApiResponse Resp(int status) => new()
    {
        StatusCode = status,
        ReasonPhrase = "x",
        Body = new byte[10],
        Elapsed = TimeSpan.FromMilliseconds(1)
    };

    [Fact]
    public void Classify_maps_status_ranges()
    {
        Assert.Equal(FuzzOutcome.Found, FuzzClassifier.Classify(Resp(200)));
        Assert.Equal(FuzzOutcome.Redirect, FuzzClassifier.Classify(Resp(302)));
        Assert.Equal(FuzzOutcome.Unauthorized, FuzzClassifier.Classify(Resp(401)));
        Assert.Equal(FuzzOutcome.Unauthorized, FuzzClassifier.Classify(Resp(403)));
        Assert.Equal(FuzzOutcome.MethodNotAllowed, FuzzClassifier.Classify(Resp(405)));
        Assert.Equal(FuzzOutcome.NotFound, FuzzClassifier.Classify(Resp(404)));
        Assert.Equal(FuzzOutcome.ServerError, FuzzClassifier.Classify(Resp(500)));
        Assert.Equal(FuzzOutcome.OtherStatus, FuzzClassifier.Classify(Resp(418)));
        Assert.Equal(FuzzOutcome.Error, FuzzClassifier.Classify(new ApiResponse { Error = new ApiError(ApiErrorKind.Network, "boom") }));
    }

    [Fact]
    public void IsDiscovery_excludes_notfound_and_error()
    {
        Assert.True(FuzzClassifier.IsDiscovery(FuzzOutcome.Found));
        Assert.True(FuzzClassifier.IsDiscovery(FuzzOutcome.MethodNotAllowed));
        Assert.True(FuzzClassifier.IsDiscovery(FuzzOutcome.Unauthorized));
        Assert.False(FuzzClassifier.IsDiscovery(FuzzOutcome.NotFound));
        Assert.False(FuzzClassifier.IsDiscovery(FuzzOutcome.Error));
    }

    [Fact]
    public async Task Runs_every_probe_and_classifies_results()
    {
        var plan = Plan("/found\n/missing\n/secure");
        var report = await EndpointFuzzer.RunAsync(plan, (req, ct) =>
        {
            int status = req.Url.EndsWith("/found") ? 200
                : req.Url.EndsWith("/secure") ? 401 : 404;
            return Task.FromResult(Resp(status));
        }, progress: null, CancellationToken.None);

        Assert.Equal(3, report.Total);
        Assert.Equal(2, report.Discovered);   // found + secure
        Assert.Contains(report.Results, r => r.Path == "/found" && r.Outcome == FuzzOutcome.Found);
        Assert.Contains(report.Results, r => r.Path == "/secure" && r.Outcome == FuzzOutcome.Unauthorized);
        Assert.False(report.AllErrored);
    }

    [Fact]
    public async Task Expands_methods_and_honors_per_entry_method()
    {
        var plan = Plan("/a\nPOST /b", methods: new[] { "GET", "HEAD" });
        var seen = new List<string>();
        var report = await EndpointFuzzer.RunAsync(plan, (req, ct) =>
        {
            lock (seen) seen.Add($"{req.Method.Method} {req.Url}");
            return Task.FromResult(Resp(200));
        }, null, CancellationToken.None);

        // /a → GET,HEAD ; /b → POST only (per-entry override)
        Assert.Equal(3, report.Total);
        Assert.Contains(seen, s => s == "GET https://api.example.com/a");
        Assert.Contains(seen, s => s == "HEAD https://api.example.com/a");
        Assert.Contains(seen, s => s == "POST https://api.example.com/b");
        Assert.DoesNotContain(seen, s => s.StartsWith("GET") && s.EndsWith("/b"));
    }

    [Fact]
    public async Task Full_url_entry_overrides_base()
    {
        var plan = Plan("https://other.example.com/x");
        string? sent = null;
        await EndpointFuzzer.RunAsync(plan, (req, ct) => { sent = req.Url; return Task.FromResult(Resp(200)); },
            null, CancellationToken.None);
        Assert.Equal("https://other.example.com/x", sent);
    }

    [Fact]
    public async Task Preserves_a_query_string_on_a_path_entry()
    {
        var plan = Plan("/search?debug=true&limit=5");
        string? sent = null;
        await EndpointFuzzer.RunAsync(plan, (req, ct) => { sent = req.Url; return Task.FromResult(Resp(200)); },
            null, CancellationToken.None);
        Assert.Equal("https://api.example.com/search?debug=true&limit=5", sent);
    }

    [Fact]
    public async Task Preserves_a_query_string_on_a_full_url_entry()
    {
        var plan = Plan("https://other.example.com/x?y=1");
        string? sent = null;
        await EndpointFuzzer.RunAsync(plan, (req, ct) => { sent = req.Url; return Task.FromResult(Resp(200)); },
            null, CancellationToken.None);
        Assert.Equal("https://other.example.com/x?y=1", sent);
    }

    [Fact]
    public async Task Reports_progress_for_each_probe()
    {
        var plan = Plan("/a\n/b\n/c");
        var ticks = new List<FuzzProgress>();
        var progress = new Progress<FuzzProgress>(p => { lock (ticks) ticks.Add(p); });
        var report = await EndpointFuzzer.RunAsync(plan, (r, ct) => Task.FromResult(Resp(200)), progress, CancellationToken.None);
        // Progress<T> marshals async; wait until all three arrive.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (ticks.Count < 3 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(3, report.Total);
        Assert.All(ticks, t => Assert.Equal(3, t.Total));
    }

    [Fact]
    public async Task Concurrency_is_bounded()
    {
        int active = 0, peak = 0;
        var plan = Plan(string.Join("\n", Enumerable.Range(0, 30).Select(i => $"/p{i}")), conc: 4);
        await EndpointFuzzer.RunAsync(plan, async (req, ct) =>
        {
            int now = Interlocked.Increment(ref active);
            lock (plan) peak = Math.Max(peak, now);
            await Task.Delay(15, ct);
            Interlocked.Decrement(ref active);
            return Resp(200);
        }, null, CancellationToken.None);
        Assert.True(peak <= 4, $"peak concurrency {peak} exceeded 4");
    }

    [Fact]
    public async Task All_transport_errors_sets_AllErrored()
    {
        var plan = Plan("/a\n/b");
        var report = await EndpointFuzzer.RunAsync(plan,
            (r, ct) => Task.FromResult(new ApiResponse { Error = new ApiError(ApiErrorKind.Network, "down") }),
            null, CancellationToken.None);
        Assert.True(report.AllErrored);
        Assert.Equal(0, report.Discovered);
    }

    [Fact]
    public async Task A_send_that_throws_is_recorded_as_an_error_not_a_batch_failure()
    {
        var plan = Plan("/a\n/b\n/c");
        var report = await EndpointFuzzer.RunAsync(plan, (req, ct) =>
            req.Url.EndsWith("/b") ? throw new InvalidOperationException("boom") : Task.FromResult(Resp(200)),
            null, CancellationToken.None);
        Assert.Equal(3, report.Total);
        Assert.Contains(report.Results, r => r.Path == "/b" && r.Outcome == FuzzOutcome.Error && r.Error == "boom");
        Assert.Equal(2, report.Results.Count(r => r.Outcome == FuzzOutcome.Found));
    }

    [Fact]
    public async Task Cancellation_stops_probing()
    {
        var plan = Plan(string.Join("\n", Enumerable.Range(0, 200).Select(i => $"/p{i}")), conc: 2);
        using var cts = new CancellationTokenSource();
        int calls = 0;
        var task = EndpointFuzzer.RunAsync(plan, async (req, ct) =>
        {
            Interlocked.Increment(ref calls);
            if (calls == 3) cts.Cancel();
            await Task.Delay(5, ct);
            return Resp(200);
        }, null, cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.True(calls < 200);
    }
}
