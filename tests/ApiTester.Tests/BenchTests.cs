using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The bench engine driven against a real loopback mTLS server, over the same send path a
/// user's request takes. Every assertion here is an invariant — how many were sent, what the counts
/// sum to, that the percentiles are ordered — never a latency value: the numbers themselves depend
/// on the machine, and a test that pinned them would only ever report the CI runner's mood.</summary>
public class BenchTests
{
    private sealed class Loopback : IAsyncDisposable
    {
        public LoopbackMtlsServer Server = null!;
        public X509Certificate2 Client = null!;
        private X509Certificate2 _ca = null!, _server = null!;

        /// <summary>A loopback server that counts the requests it answered — the ground truth behind
        /// every "how many actually went out" assertion below. <c>failures: 0</c> makes it an ordinary
        /// always-200 endpoint; a large count makes it answer <paramref name="failStatus"/> forever.</summary>
        public static async Task<Loopback> StartAsync(int failures = 0, int failStatus = 503)
        {
            var l = new Loopback();
            l._ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
            l._server = SelfSignedCertificateFactory.CreateSignedCertificate(
                "localhost", l._ca, true, false, new[] { "localhost" });
            l.Client = SelfSignedCertificateFactory.CreateSignedCertificate("BenchClient", l._ca, false, true);
            l.Server = await LoopbackMtlsServer.StartFlakyAsync(l._server, l.Client.Thumbprint!, failures, failStatus);
            return l;
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            _ca.Dispose(); _server.Dispose(); Client.Dispose();
        }
    }

    private static ApiRequest Get(Loopback l) => new()
    {
        Method = HttpMethod.Get,
        Url = l.Server.BaseUrl,
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>The loopback certificate chains to a throwaway CA, so the bench has to be told to
    /// accept it — the same switch a user passes as --insecure.</summary>
    private static TransportOptions Insecure => new() { IgnoreServerCertificateErrors = true };

    [Fact]
    public async Task A_bench_of_twenty_requests_sends_exactly_twenty()
    {
        await using var l = await Loopback.StartAsync();

        var result = await Bench.RunAsync(
            Get(l), l.Client, Insecure, new BenchOptions(Count: 20, Concurrency: 4), null, default);

        Assert.Equal(20, result.Sent);
        // The server's own count corroborates it: nothing was retried, and nothing was skipped. Its
        // counter is written by whichever handler finished last, so under concurrency the published
        // value can trail the true total by one — the point here is 20 rather than 80.
        Assert.InRange(l.Server.RequestCount, 19, 20);
    }

    [Fact]
    public async Task The_percentiles_are_ordered_from_min_to_max()
    {
        await using var l = await Loopback.StartAsync();

        var result = await Bench.RunAsync(
            Get(l), l.Client, Insecure, new BenchOptions(Count: 20, Concurrency: 4), null, default);

        Assert.True(result.MinMs <= result.P50Ms, $"min {result.MinMs} > p50 {result.P50Ms}");
        Assert.True(result.P50Ms <= result.P90Ms, $"p50 {result.P50Ms} > p90 {result.P90Ms}");
        Assert.True(result.P90Ms <= result.P99Ms, $"p90 {result.P90Ms} > p99 {result.P99Ms}");
        Assert.True(result.P99Ms <= result.MaxMs, $"p99 {result.P99Ms} > max {result.MaxMs}");
        Assert.True(result.MinMs > 0, "a real request over TLS cannot take zero time");
    }

    [Fact]
    public async Task Every_result_lands_in_the_status_counts()
    {
        await using var l = await Loopback.StartAsync();

        var result = await Bench.RunAsync(
            Get(l), l.Client, Insecure, new BenchOptions(Count: 20, Concurrency: 4), null, default);

        Assert.Equal(result.Sent, result.StatusCounts.Values.Sum());
        Assert.Equal(20, result.StatusCounts[200]);
        Assert.Empty(result.ErrorCounts);
    }

    [Fact]
    public async Task A_healthy_endpoint_fails_nothing()
    {
        await using var l = await Loopback.StartAsync();

        var result = await Bench.RunAsync(
            Get(l), l.Client, Insecure, new BenchOptions(Count: 20, Concurrency: 4), null, default);

        Assert.Equal(20, result.Succeeded);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task The_request_rate_is_a_finite_number_above_zero()
    {
        await using var l = await Loopback.StartAsync();

        var result = await Bench.RunAsync(
            Get(l), l.Client, Insecure, new BenchOptions(Count: 8, Concurrency: 2), null, default);

        // A report that prints NaN or ∞ is worthless, so finiteness is part of the contract.
        Assert.False(double.IsNaN(result.RequestsPerSecond));
        Assert.False(double.IsInfinity(result.RequestsPerSecond));
        Assert.True(result.RequestsPerSecond > 0, $"rate was {result.RequestsPerSecond}");
        Assert.True(result.Elapsed > TimeSpan.Zero);
    }

    [Fact]
    public async Task A_duration_run_stops_when_the_period_is_over()
    {
        await using var l = await Loopback.StartAsync();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await Bench.RunAsync(
            Get(l), l.Client, Insecure,
            new BenchOptions(Count: 0, Concurrency: 2, Duration: TimeSpan.FromSeconds(1)), null, default);
        clock.Stop();

        // Deliberately loose: this suite runs on shared CI, and the claim being tested is "it stops",
        // not "it stops to the millisecond".
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"a 1s bench took {clock.Elapsed}");
        Assert.True(result.Sent > 0, "a second of a loopback endpoint is more than no requests");
        Assert.Equal(result.Sent, result.StatusCounts.Values.Sum());
    }

    [Fact]
    public async Task Warm_up_requests_are_extra_and_discarded()
    {
        await using var l = await Loopback.StartAsync();

        var result = await Bench.RunAsync(
            Get(l), l.Client, Insecure,
            // One worker, so the server's counter is exact rather than last-write-wins.
            new BenchOptions(Count: 5, Concurrency: 1, WarmUp: TimeSpan.FromMilliseconds(300)), null, default);

        // Five measured requests, and the server saw more than five: the warm-up was extra (it did
        // not eat into -n) and its results are in none of the statistics.
        Assert.Equal(5, result.Sent);
        Assert.Equal(5, result.StatusCounts[200]);
        Assert.True(l.Server.RequestCount > 5, $"the server only saw {l.Server.RequestCount} requests");
    }

    [Fact]
    public async Task Cancelling_a_bench_returns_what_it_measured_instead_of_throwing()
    {
        await using var l = await Loopback.StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await Bench.RunAsync(
            Get(l), l.Client, Insecure, new BenchOptions(Count: 5000, Concurrency: 2), null, cts.Token);

        // A bench interrupted with Ctrl+C still has something to say, so the partial result comes back
        // rather than an exception.
        Assert.True(result.Sent < 5000, $"a cancelled bench sent all {result.Sent} requests");
        Assert.Equal(result.Sent, result.StatusCounts.Values.Sum() + result.ErrorCounts.Values.Sum());
    }

    [Fact]
    public async Task Degenerate_input_neither_hangs_nor_spins()
    {
        await using var l = await Loopback.StartAsync();

        // Nothing to send and no duration: an empty report, not a wait on a counter nobody increments.
        var nothing = Bench.RunAsync(
            Get(l), l.Client, Insecure, new BenchOptions(Count: 0, Concurrency: 0), null, default);
        Assert.Same(nothing, await Task.WhenAny(nothing, Task.Delay(TimeSpan.FromSeconds(10))));
        var empty = await nothing;
        Assert.Equal(0, empty.Sent);
        Assert.Equal(0, empty.Succeeded);
        Assert.Equal(0, empty.Failed);
        Assert.Equal(0, empty.RequestsPerSecond);
        Assert.Equal(new[] { 0d, 0d, 0d, 0d, 0d },
            new[] { empty.MinMs, empty.P50Ms, empty.P90Ms, empty.P99Ms, empty.MaxMs });
        Assert.Empty(empty.StatusCounts);
        Assert.Empty(empty.ErrorCounts);
        Assert.Equal(0, l.Server.RequestCount);

        // A concurrency of zero is a caller's bug, not an instruction to run no workers: it is clamped
        // to one so the requested count still gets sent.
        var clamped = Bench.RunAsync(
            Get(l), l.Client, Insecure, new BenchOptions(Count: 3, Concurrency: 0), null, default);
        Assert.Same(clamped, await Task.WhenAny(clamped, Task.Delay(TimeSpan.FromSeconds(20))));
        Assert.Equal(3, (await clamped).Sent);
    }

    [Fact]
    public async Task An_endpoint_that_answers_500_fails_every_request()
    {
        await using var l = await Loopback.StartAsync(failures: int.MaxValue, failStatus: 500);

        var result = await Bench.RunAsync(
            Get(l), l.Client, Insecure, new BenchOptions(Count: 10, Concurrency: 2), null, default);

        Assert.Equal(10, result.Sent);
        Assert.Equal(0, result.Succeeded);
        Assert.Equal(result.Sent, result.Failed);
        Assert.Equal(result.Sent, result.StatusCounts[500]);
        // A 500 is the endpoint answering, so it is a status rather than a transport error.
        Assert.Empty(result.ErrorCounts);
    }
}
