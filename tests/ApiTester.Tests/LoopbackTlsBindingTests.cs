using ApiTester.Core;

namespace ApiTester.Tests;

public class LoopbackTlsBindingTests
{
    private const string RealisticShowOutput = """

        SSL Certificate bindings:
        -------------------------

            IP:port                      : 127.0.0.1:8443
            Certificate Hash             : a909502dd82ae41433e6f83886b00d4277a32a7b
            Application ID               : {00112233-4455-6677-8899-aabbccddeeff}
            Certificate Store Name       : MY
        """;

    [Fact]
    public void BuildAddCommand_contains_the_pieces_netsh_needs()
    {
        string command = LoopbackTlsBinding.BuildAddCommand(8443, "a9 09 50 2d d8 2a e4 14 33 e6 f8 38 86 b0 0d 42 77 a3 2a 7b");

        Assert.Contains("ipport=127.0.0.1:8443", command);
        Assert.Contains("certhash=A909502DD82AE41433E6F83886B00D4277A32A7B", command);
        Assert.Contains($"appid={LoopbackTlsBinding.AppId}", command);
        Assert.Contains("certstorename=MY", command);
    }

    [Fact]
    public void BuildShowCommand_and_BuildDeleteCommand_are_the_exact_expected_strings()
    {
        Assert.Equal("netsh http show sslcert ipport=127.0.0.1:8443", LoopbackTlsBinding.BuildShowCommand(8443));
        Assert.Equal("netsh http delete sslcert ipport=127.0.0.1:8443", LoopbackTlsBinding.BuildDeleteCommand(8443));
    }

    [Fact]
    public void NormalizeThumbprint_uppercases_and_strips_spaces()
    {
        Assert.Equal("A909502D", LoopbackTlsBinding.NormalizeThumbprint("a9 09 50 2d"));
    }

    [Fact]
    public void ParseShowOutput_extracts_thumbprint_and_app_id_from_realistic_output()
    {
        var bound = LoopbackTlsBinding.ParseShowOutput(RealisticShowOutput);

        Assert.NotNull(bound);
        Assert.Equal("A909502DD82AE41433E6F83886B00D4277A32A7B", bound!.Thumbprint);
        Assert.Equal("{00112233-4455-6677-8899-aabbccddeeff}", bound.AppId);
    }

    [Theory]
    [InlineData("The system cannot find the file specified.")]
    [InlineData("")]
    [InlineData("Some unrelated text with no certificate info.")]
    public void ParseShowOutput_returns_null_when_no_binding_is_reported(string output)
    {
        Assert.Null(LoopbackTlsBinding.ParseShowOutput(output));
    }

    [Fact]
    public void NotElevatedMessage_mentions_elevation_and_contains_add_and_delete_commands()
    {
        string message = LoopbackTlsBinding.NotElevatedMessage(8443, "A909502DD82AE41433E6F83886B00D4277A32A7B");

        Assert.Contains("elevat", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("127.0.0.1:8443", message);
        Assert.Contains(LoopbackTlsBinding.BuildAddCommand(8443, "A909502DD82AE41433E6F83886B00D4277A32A7B"), message);
        Assert.Contains(LoopbackTlsBinding.BuildDeleteCommand(8443), message);
    }

    [Fact]
    public void AlreadyBoundMessage_names_the_bound_thumbprint_and_offers_delete_command()
    {
        var bound = new BoundCertificate("A909502DD82AE41433E6F83886B00D4277A32A7B", "{00112233-4455-6677-8899-aabbccddeeff}");

        string message = LoopbackTlsBinding.AlreadyBoundMessage(8443, bound);

        Assert.Contains("127.0.0.1:8443", message);
        Assert.Contains(bound.Thumbprint, message);
        Assert.Contains(LoopbackTlsBinding.BuildDeleteCommand(8443), message);
    }

    [Trait("Category", "TlsBinding")]
    [Fact]
    public void Describe_reports_the_binding_that_netsh_shows()
    {
        // Opt-in only: this is the one test that runs netsh. It is off unless the operator sets
        // CERTAPI_TLS_BINDING_TESTS=1, because the suite must never touch HTTP Server API state.
        if (Environment.GetEnvironmentVariable("CERTAPI_TLS_BINDING_TESTS") != "1") return;

        // The operator is expected to have already bound a certificate to this port by hand
        // (e.g. via `netsh http add sslcert ipport=127.0.0.1:18443 ...`) before setting the
        // opt-in flag. This test only reads that state — it never binds or unbinds anything.
        const int port = 18443;
        var bound = LoopbackTlsBinding.Describe(port);

        Assert.NotNull(bound);
        Assert.False(string.IsNullOrWhiteSpace(bound!.Thumbprint));
    }
}
