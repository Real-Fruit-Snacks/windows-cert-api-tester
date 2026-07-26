using System.IO;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>The command-line surface of retry: the five flags on send/run/fuzz, their validation,
/// and the attempt count the user reads back. The engine's own behavior is covered by
/// ApiClientRetryTests; what these prove is that the flags reach it and the count reaches the user.</summary>
public class RetryCliTests
{
    // Never the developer's real %AppData%\CertApiTester\state.json: certapi send always reads the
    // live state (for auto-token reuse), so every command-level test gets its own temp path.
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    private static TransportOptions Apply(params string[] tokens) =>
        TransportFlags.Parse(new Args(tokens), out _).ApplyTo(new TransportOptions());

    /// <summary>certapi send against a host that never resolves, so a usage error is decided long
    /// before anything is sent. The exit code is the contract CI depends on, so these go through
    /// CliApp.Run rather than calling the parser directly.</summary>
    private static (int Code, string Err) RunSend(params string[] extra)
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[] { "send", "https://example.invalid/" }.Concat(extra).ToArray(),
            new StringWriter(), stderr, new MemoryStream(), new CliServices { LiveStatePath = TempState() });
        return (code, stderr.ToString());
    }

    [Fact]
    public void No_retry_flags_leaves_every_retry_setting_at_its_default()
    {
        var overrides = TransportFlags.Parse(new Args(new[] { "https://x" }), out _);

        Assert.Null(overrides.Retries);
        Assert.Null(overrides.RetryOn);
        Assert.Null(overrides.RetryDelayMs);
        Assert.Null(overrides.RetryUnsafeMethods);
        Assert.Null(overrides.RetryOnTransportError);

        var options = overrides.ApplyTo(new TransportOptions());
        Assert.Equal(0, options.Retries);
        Assert.Equal(new[] { 429, 502, 503, 504 }, options.RetryOn);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.RetryDelay);
        Assert.True(options.RetryOnTransportError);
        Assert.True(options.HonorRetryAfter);
        Assert.False(options.RetryUnsafeMethods);
    }

    [Fact]
    public void Retry_sets_the_retry_count()
    {
        Assert.Equal(3, Apply("https://x", "--retry", "3").Retries);
        Assert.Equal(0, Apply("https://x", "--retry", "0").Retries);
    }

    [Fact]
    public void Retry_on_replaces_the_default_status_list()
    {
        Assert.Equal(new[] { 500, 502 }, Apply("https://x", "--retry-on", "500,502").RetryOn);
    }

    [Fact]
    public void Retry_on_tolerates_spaces_around_the_codes()
    {
        Assert.Equal(new[] { 429, 503 }, Apply("https://x", "--retry-on", "429, 503").RetryOn);
    }

    [Fact]
    public void Retry_delay_is_read_as_milliseconds()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), Apply("https://x", "--retry-delay", "250").RetryDelay);
    }

    [Fact]
    public void Retry_unsafe_opts_post_and_patch_in_and_its_absence_leaves_the_baseline_alone()
    {
        Assert.True(Apply("https://x", "--retry-unsafe").RetryUnsafeMethods);

        var saved = new TransportOptions { RetryUnsafeMethods = true };
        var options = TransportFlags.Parse(new Args(new[] { "https://x" }), out _).ApplyTo(saved);
        Assert.True(options.RetryUnsafeMethods);
    }

    [Fact]
    public void No_retry_transport_turns_transport_retries_off_and_its_absence_leaves_the_baseline_alone()
    {
        Assert.False(Apply("https://x", "--no-retry-transport").RetryOnTransportError);

        var saved = new TransportOptions { RetryOnTransportError = false };
        var options = TransportFlags.Parse(new Args(new[] { "https://x" }), out _).ApplyTo(saved);
        Assert.False(options.RetryOnTransportError);
    }

    [Fact]
    public void A_saved_requests_own_retry_settings_survive_when_no_flag_names_them()
    {
        // The property `run` depends on: a saved request that already retries keeps doing so unless
        // the command line says otherwise, and one flag does not reset the rest.
        var saved = new TransportOptions
        {
            Retries = 5,
            RetryOn = new[] { 418 },
            RetryDelay = TimeSpan.FromMilliseconds(1234),
            RetryUnsafeMethods = true,
            RetryOnTransportError = false
        };

        var options = TransportFlags.Parse(new Args(new[] { "--retry-delay", "7" }), out _).ApplyTo(saved);

        Assert.Equal(TimeSpan.FromMilliseconds(7), options.RetryDelay);
        Assert.Equal(5, options.Retries);
        Assert.Equal(new[] { 418 }, options.RetryOn);
        Assert.True(options.RetryUnsafeMethods);
        Assert.False(options.RetryOnTransportError);
    }

    [Theory]
    [InlineData("--retry", "-1")]              // a negative count is not a smaller retry
    [InlineData("--retry", "abc")]
    [InlineData("--retry-on", "429,abc")]      // a typo'd code must not be silently dropped
    [InlineData("--retry-on", "999")]          // not a status code at all
    [InlineData("--retry-on", "42")]
    [InlineData("--retry-on", "")]             // "retry on nothing" is a mistake, not an instruction
    [InlineData("--retry-delay", "-5")]
    [InlineData("--retry-delay", "x")]
    public void A_malformed_retry_flag_is_a_usage_error(string flag, string value)
    {
        var (code, err) = RunSend(flag, value);

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains(flag, err);
    }

    [Fact]
    public void Retry_on_names_the_token_it_could_not_read()
    {
        var (code, err) = RunSend("--retry-on", "429,abc,503");

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("'abc'", err);
    }

    [Theory]
    [InlineData("send")]
    [InlineData("run")]
    [InlineData("fuzz")]
    public void The_retry_flags_are_documented_on_every_command_that_shares_them(string command)
    {
        // The whole design of this surface: one description of the flags, spliced into all three
        // commands, so the help of one cannot drift from the behavior of another.
        var stdout = new StringWriter();
        int code = CliApp.Run(new[] { "help", command }, stdout, new StringWriter(), new MemoryStream(),
            new CliServices { LiveStatePath = TempState() });

        Assert.Equal(ExitCodes.Ok, code);
        string help = stdout.ToString();
        Assert.Contains("--retry ", help);
        Assert.Contains("--retry-on ", help);
        Assert.Contains("--retry-delay ", help);
        Assert.Contains("--retry-unsafe", help);
        Assert.Contains("--no-retry-transport", help);
    }

    [Fact]
    public void The_meta_line_reports_the_attempt_count_only_when_a_retry_happened()
    {
        var retried = new ApiResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Elapsed = TimeSpan.FromMilliseconds(42),
            Attempts = 3,
            Connection = new ConnectionInfo { TlsProtocol = "Tls13", ClientCertificateSent = true }
        };
        Assert.Equal("200 OK · 0 B · 42 ms · 3 attempts · Tls13 · client cert presented",
            OutputText.MetaLine(retried));

        // One attempt is every response this client produced before retry existed; saying so would
        // churn the output of every user who never asked for a retry.
        Assert.DoesNotContain("attempts", OutputText.MetaLine(retried with { Attempts = 1 }));
    }

    /// <summary>certapi send against a loopback server that answers <paramref name="failures"/>
    /// requests with a failure status before succeeding — the whole path from the command line into
    /// ApiClient, over a real mTLS handshake. --fail is always passed so a retried-away failure and a
    /// surrendered one are told apart by the exit code; --retry-delay 1 keeps the backoff free.</summary>
    private static async Task<(int Code, string Err, int Requests)> SendToFlakyServerAsync(
        int failures, params string[] extra)
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("RetryCliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartFlakyAsync(serverCert, clientCert.Thumbprint!, failures);

        var services = new CliServices
        {
            LiveStatePath = TempState(),
            ListCertificates = _ => new[]
            {
                new CertificateInfo
                {
                    Subject = "CN=RetryCliClient", Issuer = "CN=CA", Thumbprint = clientCert.Thumbprint!,
                    NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                    HasClientAuthEku = true, Certificate = clientCert
                }
            }
        };

        var stderr = new StringWriter();
        var args = new[] { "send", server.BaseUrl, "--cert", "RetryCliClient", "--insecure", "--fail" }
            .Concat(extra).ToArray();
        int code = CliApp.Run(args, new StringWriter(), stderr, new MemoryStream(), services);
        return (code, stderr.ToString(), server.RequestCount);
    }

    [Fact]
    public async Task Retry_flag_retries_a_flaky_endpoint_and_reports_the_attempt_count()
    {
        var (code, err, requests) = await SendToFlakyServerAsync(
            failures: 2, "--retry", "3", "--retry-delay", "1");

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("3 attempts", err);
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task Without_the_retry_flag_the_first_failure_is_the_answer()
    {
        var (code, err, requests) = await SendToFlakyServerAsync(failures: 2);

        Assert.Equal(ExitCodes.Failure, code);
        Assert.DoesNotContain("attempts", err);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task A_status_that_retry_on_does_not_list_is_not_retried()
    {
        // The server fails with 503; only 502 was asked for, so the 503 is a final answer.
        var (code, err, requests) = await SendToFlakyServerAsync(
            failures: 2, "--retry", "3", "--retry-delay", "1", "--retry-on", "502");

        Assert.Equal(ExitCodes.Failure, code);
        Assert.DoesNotContain("attempts", err);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task A_post_is_retried_only_when_retry_unsafe_opts_it_in()
    {
        var guarded = await SendToFlakyServerAsync(
            failures: 2, "-X", "POST", "-d", "{}", "--retry", "3", "--retry-delay", "1");
        Assert.Equal(ExitCodes.Failure, guarded.Code);
        Assert.Equal(1, guarded.Requests);

        var optedIn = await SendToFlakyServerAsync(
            failures: 2, "-X", "POST", "-d", "{}", "--retry", "3", "--retry-delay", "1", "--retry-unsafe");
        Assert.Equal(ExitCodes.Ok, optedIn.Code);
        Assert.Contains("3 attempts", optedIn.Err);
        Assert.Equal(3, optedIn.Requests);
    }

    [Fact]
    public void A_failure_that_exhausted_its_retries_says_how_many_times_it_failed()
    {
        var failed = new ApiResponse
        {
            Elapsed = TimeSpan.FromMilliseconds(9),
            Attempts = 3,
            Error = new ApiError(ApiErrorKind.ConnectionRefused, "connection refused")
        };
        Assert.Equal("error [ConnectionRefused]: connection refused (3 attempts)", OutputText.MetaLine(failed));

        // A single failure keeps the wording it has always had.
        Assert.Equal("error [ConnectionRefused]: connection refused",
            OutputText.MetaLine(failed with { Attempts = 1 }));
    }
}
