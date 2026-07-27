using System.IO;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>The command-line surface of certificate revocation checking: --revocation and
/// --revocation-strict as TransportFlags parses and applies them, the shared strict-without-a-mode
/// usage error reaching the command line end to end, and the --json envelope reporting the outcome.
/// The revocation decision table itself (RevocationCheck.Decide) and its wiring into ApiClient are
/// covered by their own tests; what these prove is that the flags and the reporting reach it
/// correctly.</summary>
public class RevocationCliTests
{
    // Never the developer's real %AppData%\CertApiTester\state.json: certapi send always reads the
    // live state (for auto-token reuse), so every command-level test gets its own temp path.
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    [Theory]
    [InlineData("online", RevocationMode.Online)]
    [InlineData("offline", RevocationMode.Offline)]
    [InlineData("none", RevocationMode.None)]
    [InlineData("ONLINE", RevocationMode.Online)]
    public void Revocation_flag_parses_each_mode_case_insensitively(string raw, RevocationMode expected)
    {
        var overrides = TransportFlags.Parse(new Args(new[] { "https://x", "--revocation", raw }), out _);

        Assert.Equal(expected, overrides.Revocation);
    }

    [Fact]
    public void An_unrecognized_revocation_mode_is_a_usage_error_naming_the_offending_value()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            TransportFlags.Parse(new Args(new[] { "https://x", "--revocation", "bogus" }), out _));

        Assert.Contains("bogus", ex.Message);
    }

    [Fact]
    public void No_revocation_flag_leaves_the_override_null_and_a_saved_requests_own_mode_survives()
    {
        var overrides = TransportFlags.Parse(new Args(new[] { "https://x" }), out _);
        Assert.Null(overrides.Revocation);

        // The certapi run guarantee: a saved request's own revocation mode is untouched when the
        // command line names nothing about revocation at all.
        var saved = new TransportOptions { Revocation = RevocationMode.Online };
        var options = overrides.ApplyTo(saved);
        Assert.Equal(RevocationMode.Online, options.Revocation);
    }

    [Fact]
    public void Revocation_flag_overrides_a_saved_requests_own_mode()
    {
        var saved = new TransportOptions { Revocation = RevocationMode.Online };

        var options = TransportFlags.Parse(new Args(new[] { "https://x", "--revocation", "offline" }), out _)
            .ApplyTo(saved);

        Assert.Equal(RevocationMode.Offline, options.Revocation);
    }

    [Fact]
    public void Revocation_strict_flag_sets_the_override_true_and_reaches_transport_options()
    {
        var overrides = TransportFlags.Parse(new Args(new[] { "https://x", "--revocation-strict" }), out _);
        Assert.True(overrides.RevocationStrict);

        var options = overrides.ApplyTo(new TransportOptions());
        Assert.True(options.RevocationStrict);
    }

    [Fact]
    public void Absent_revocation_strict_flag_leaves_the_override_null_and_a_saved_requests_own_value_survives()
    {
        var overrides = TransportFlags.Parse(new Args(new[] { "https://x" }), out _);
        Assert.Null(overrides.RevocationStrict);

        var saved = new TransportOptions { RevocationStrict = true };
        var options = overrides.ApplyTo(saved);
        Assert.True(options.RevocationStrict);
    }

    [Fact]
    public void Revocation_strict_without_a_mode_exits_with_the_usage_code_and_the_shared_message_end_to_end()
    {
        // Proves ApiClient.ValidateTransport -- not a duplicated check inside TransportFlags.Parse --
        // is what produces this: the message is the repo's own shared constant, reached after
        // TransportOverrides.ApplyTo, exactly as ruling 3's contract describes.
        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[] { "send", "https://example.invalid/", "--revocation-strict" },
            new StringWriter(), stderr, new MemoryStream(), new CliServices { LiveStatePath = TempState() });

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains(RevocationCheck.StrictWithoutCheckingMessage, stderr.ToString());
    }

    [Fact]
    public void The_json_envelope_reports_the_revocation_mode_and_status_by_enum_name()
    {
        var response = new ApiResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Body = System.Text.Encoding.UTF8.GetBytes("hi"),
            Connection = new ConnectionInfo
            {
                RevocationMode = RevocationMode.Online,
                RevocationStatus = RevocationStatus.Checked
            }
        };

        using var doc = JsonDocument.Parse(SendCommand.BuildEnvelope(response, includeBody: true));

        Assert.Equal("Online", doc.RootElement.GetProperty("revocationMode").GetString());
        Assert.Equal("Checked", doc.RootElement.GetProperty("revocationStatus").GetString());
    }

    [Fact]
    public void The_json_envelope_reports_not_checked_when_there_is_no_connection_info()
    {
        var response = new ApiResponse
        {
            Elapsed = System.TimeSpan.FromMilliseconds(1),
            Error = new ApiError(ApiErrorKind.Network, "boom")
        };

        using var doc = JsonDocument.Parse(SendCommand.BuildEnvelope(response, includeBody: false));

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("revocationMode").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("revocationStatus").ValueKind);
    }

    [Fact]
    public void The_shared_transport_help_documents_both_revocation_flags()
    {
        Assert.Contains("--revocation <mode>", TransportFlags.Help);
        Assert.Contains("--revocation-strict", TransportFlags.Help);
    }

    /// <summary>certapi send against a real loopback server, over a real mTLS handshake: --insecure
    /// overrides revocation exactly as it overrides every other chain problem (ruling 5), but must
    /// say so rather than leave a reader to assume a clean check. This also exercises the --debug
    /// transport summary's own revocation term and the connection line's plain-words status.</summary>
    [Fact]
    public async System.Threading.Tasks.Task Insecure_send_reports_that_revocation_was_requested_but_not_enforced()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("RevocationCliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);

        var services = new CliServices
        {
            LiveStatePath = TempState(),
            ListCertificates = _ => new[]
            {
                new CertificateInfo
                {
                    Subject = "CN=RevocationCliClient", Issuer = "CN=CA", Thumbprint = clientCert.Thumbprint!,
                    NotBefore = System.DateTime.Now.AddDays(-1), NotAfter = System.DateTime.Now.AddDays(30),
                    HasClientAuthEku = true, Certificate = clientCert
                }
            }
        };

        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[]
            {
                "send", server.BaseUrl, "--cert", "RevocationCliClient", "--insecure",
                "--revocation", "online", "--debug"
            },
            new StringWriter(), stderr, new MemoryStream(), services);

        Assert.Equal(ExitCodes.Ok, code);
        string err = stderr.ToString();
        Assert.Contains("revocation online", err);
        Assert.Contains("NOT ENFORCED (--insecure)", err);
        Assert.Contains("--insecure", err);
        Assert.Contains("not enforced", err);
    }

    /// <summary>The default mode is none, so --insecure alone -- the behavior every existing user
    /// already relies on -- must never print the not-enforced note: nothing about revocation was
    /// ever asked for.</summary>
    [Fact]
    public async System.Threading.Tasks.Task Insecure_send_without_a_revocation_mode_prints_no_not_enforced_note()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("RevocationCliClient2", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);

        var services = new CliServices
        {
            LiveStatePath = TempState(),
            ListCertificates = _ => new[]
            {
                new CertificateInfo
                {
                    Subject = "CN=RevocationCliClient2", Issuer = "CN=CA", Thumbprint = clientCert.Thumbprint!,
                    NotBefore = System.DateTime.Now.AddDays(-1), NotAfter = System.DateTime.Now.AddDays(30),
                    HasClientAuthEku = true, Certificate = clientCert
                }
            }
        };

        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[] { "send", server.BaseUrl, "--cert", "RevocationCliClient2", "--insecure" },
            new StringWriter(), stderr, new MemoryStream(), services);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.DoesNotContain("not enforced", stderr.ToString());
    }
}
