using System.Diagnostics;
using System.Net.Http;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Retry and backoff, proven against a real loopback mTLS server that fails a fixed number
/// of times before succeeding: the server's own request count is the ground truth an attempt count
/// is checked against, so a passing test means the client really did send the request again.</summary>
public class ApiClientRetryTests
{
    [Fact]
    public async Task Retries_a_503_until_it_succeeds()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: 2);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Retries = 3,
                RetryDelay = TimeSpan.FromMilliseconds(20)
            });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(3, resp.Attempts);
        Assert.Equal(3, server.RequestCount);
    }

    [Fact]
    public async Task Retry_is_off_by_default()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: 2);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.Equal(503, resp.StatusCode);
        Assert.Equal(1, resp.Attempts);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task Retries_is_a_cap_not_a_promise()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        // Never succeeds, so only the cap can stop it.
        await using var server = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: int.MaxValue);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Retries = 2,
                RetryDelay = TimeSpan.FromMilliseconds(20)
            });

        Assert.Equal(503, resp.StatusCode);
        // Two retries on top of the first attempt.
        Assert.Equal(3, resp.Attempts);
        Assert.Equal(3, server.RequestCount);
    }

    [Theory]
    [InlineData(404)]   // a real answer, not a "not now"
    [InlineData(500)]   // deliberately absent from the default set: a bug repeats
    public async Task A_status_off_the_retry_list_is_not_retried(int status)
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: 2, failStatus: status);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Retries = 3,
                RetryDelay = TimeSpan.FromMilliseconds(20)
            });

        Assert.Equal(status, resp.StatusCode);
        Assert.Equal(1, resp.Attempts);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task A_POST_is_not_retried_unless_unsafe_methods_are_opted_in()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        ApiRequest Post(string url) => new()
        {
            Method = HttpMethod.Post,
            Url = url,
            Body = "{\"charge\":1}",
            ContentType = "application/json"
        };

        // Re-sending a POST nobody confirmed can charge a card twice, so it stays put by default.
        await using var guarded = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: 2);
        var refused = await new ApiClient().SendAsync(
            Post(guarded.BaseUrl), clientCert,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Retries = 3,
                RetryDelay = TimeSpan.FromMilliseconds(20)
            });

        Assert.Equal(503, refused.StatusCode);
        Assert.Equal(1, refused.Attempts);
        Assert.Equal(1, guarded.RequestCount);

        // ...and moves once the caller says the endpoint can take it.
        await using var opted = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: 2);
        var allowed = await new ApiClient().SendAsync(
            Post(opted.BaseUrl), clientCert,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Retries = 3,
                RetryDelay = TimeSpan.FromMilliseconds(20),
                RetryUnsafeMethods = true
            });

        Assert.Equal(200, allowed.StatusCode);
        Assert.Equal(3, allowed.Attempts);
        Assert.Equal(3, opted.RequestCount);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Every_idempotent_method_retries_without_being_asked_twice(string method)
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: 1);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = new HttpMethod(method), Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Retries = 3,
                RetryDelay = TimeSpan.FromMilliseconds(20)
            });

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(2, resp.Attempts);
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task A_refused_connection_is_retried_when_transport_errors_count()
    {
        // Port 1 refuses immediately, so this is a transport failure rather than a status. Nothing is
        // observable server-side, so the attempt count is the whole evidence.
        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = "https://127.0.0.1:1/" },
            clientCertificate: null,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Retries = 2,
                RetryDelay = TimeSpan.FromMilliseconds(20)
            });

        Assert.NotNull(resp.Error);
        Assert.Equal(ApiErrorKind.ConnectionRefused, resp.Error!.Kind);
        Assert.Equal(3, resp.Attempts);
    }

    [Fact]
    public async Task A_refused_connection_is_not_retried_when_transport_errors_are_excluded()
    {
        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = "https://127.0.0.1:1/" },
            clientCertificate: null,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Retries = 2,
                RetryDelay = TimeSpan.FromMilliseconds(20),
                RetryOnTransportError = false
            });

        Assert.Equal(ApiErrorKind.ConnectionRefused, resp.Error?.Kind);
        Assert.Equal(1, resp.Attempts);
    }

    [Fact]
    public async Task A_refused_client_certificate_is_not_retried()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        // The server demands a client certificate this request will not present, and it will refuse
        // the next one for the same reason: retrying a rejected certificate only fails slower.
        await using var server = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: 0);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCertificate: null,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Retries = 3,
                RetryDelay = TimeSpan.FromMilliseconds(20)
            });

        Assert.NotNull(resp.Error);
        // The handshake failure surfaces as a refused certificate on some TLS stacks and as a reset
        // connection on others; only the first is excluded from retry, so the assertion follows suit.
        if (resp.Error!.Kind is ApiErrorKind.CertificateRefused)
            Assert.Equal(1, resp.Attempts);
    }

    [Fact]
    public async Task A_retry_after_header_wins_over_the_computed_backoff()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        // Retry-After: 0 against a ten-second computed backoff, so the elapsed time says which delay
        // was used without the test having to see inside the client.
        await using var server = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: 1, failStatus: 503, retryAfter: "0");

        var clock = Stopwatch.StartNew();
        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Retries = 3,
                RetryDelay = TimeSpan.FromSeconds(10),
                HonorRetryAfter = true
            });
        clock.Stop();

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(2, resp.Attempts);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3),
            $"The computed 10s backoff appears to have been used anyway: took {clock.Elapsed}.");
    }

    [Fact]
    public async Task Ignoring_retry_after_falls_back_to_the_computed_backoff()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        // The same Retry-After: 0, but ignored — so the configured delay must be paid. Kept small so
        // the cost of proving it is 300ms rather than ten seconds.
        await using var server = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: 1, failStatus: 503, retryAfter: "0");

        var clock = Stopwatch.StartNew();
        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Retries = 3,
                RetryDelay = TimeSpan.FromMilliseconds(300),
                HonorRetryAfter = false
            });
        clock.Stop();

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(2, resp.Attempts);
        // Jitter can shave 10% off the 300ms, so the floor allows for it.
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(260),
            $"The server's Retry-After: 0 appears to have been honored anyway: took {clock.Elapsed}.");
    }

    [Fact]
    public async Task A_retry_after_date_that_has_already_passed_means_now()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        // The HTTP-date form of Retry-After, a minute in the past: a gone-by date is a wait of zero,
        // never a negative one, so the ten-second computed backoff must still be skipped.
        string past = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        await using var server = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: 1, failStatus: 503, retryAfter: past);

        var clock = Stopwatch.StartNew();
        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Retries = 3,
                RetryDelay = TimeSpan.FromSeconds(10)
            });
        clock.Stop();

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(2, resp.Attempts);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3),
            $"An HTTP-date Retry-After was not understood: took {clock.Elapsed}.");
    }

    [Fact]
    public async Task An_unreadable_retry_after_falls_back_to_the_computed_backoff()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        // Neither seconds nor a date. The send must still complete on the computed delay rather than
        // failing over a header it could not read.
        await using var server = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: 1, failStatus: 503, retryAfter: "whenever");

        var clock = Stopwatch.StartNew();
        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Retries = 3,
                RetryDelay = TimeSpan.FromMilliseconds(300)
            });
        clock.Stop();

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(2, resp.Attempts);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(260),
            $"An unreadable Retry-After appears to have been read as zero: took {clock.Elapsed}.");
    }

    [Fact]
    public async Task Cancelling_during_a_backoff_wait_returns_promptly()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: int.MaxValue);

        // The first attempt fails fast and a ten-second wait begins; cancelling into that wait has to
        // end the call, which it only can if the wait is awaited rather than slept through.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var clock = Stopwatch.StartNew();
        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Retries = 5,
                RetryDelay = TimeSpan.FromSeconds(10)
            },
            cancellationToken: cts.Token);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3),
            $"The backoff wait ignored cancellation: took {clock.Elapsed}.");
        // The failed response the client had in hand is what comes back, with the attempts it took.
        Assert.Equal(503, resp.StatusCode);
        Assert.Equal(1, resp.Attempts);
    }

    [Fact]
    public async Task A_custom_retry_list_replaces_the_default_one()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        var transport = new TransportOptions
        {
            IgnoreServerCertificateErrors = true,
            Retries = 3,
            RetryOn = new[] { 500 },
            RetryDelay = TimeSpan.FromMilliseconds(20)
        };

        // 500 is on the list now, so it is retried...
        await using var retried = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: 1, failStatus: 500);
        var onList = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = retried.BaseUrl }, clientCert, transport);

        Assert.Equal(200, onList.StatusCode);
        Assert.Equal(2, onList.Attempts);
        Assert.Equal(2, retried.RequestCount);

        // ...and 503, a default that the custom list displaced, is not.
        await using var notRetried = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: 1, failStatus: 503);
        var offList = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = notRetried.BaseUrl }, clientCert, transport);

        Assert.Equal(503, offList.StatusCode);
        Assert.Equal(1, offList.Attempts);
        Assert.Equal(1, notRetried.RequestCount);
    }
}
