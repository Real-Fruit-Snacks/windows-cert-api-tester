using System.IO;
using ApiTester.Cli;

namespace ApiTester.Tests.Cli;

/// <summary>Argument-validation coverage for `serve --tls*`. Both tests exit during parsing, before
/// any certificate, store, or binding code runs — no other test belongs in this file (see the B-6
/// brief's safety rule: this suite must never touch machine TLS state).</summary>
public class ServeTlsCliTests
{
    [Fact]
    public void Tls_trust_without_tls_is_a_usage_error()
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[] { "serve", "https://upstream.example", "--port", "18443", "--tls-trust" },
            TextWriter.Null, stderr, services: new CliServices());

        Assert.Equal(2, code);
        Assert.Contains("--tls-trust only applies together with --tls.", stderr.ToString());
    }

    [Fact]
    public void Tls_untrust_combined_with_tls_is_a_usage_error()
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(
            new[] { "serve", "https://upstream.example", "--port", "18443", "--tls", "--tls-untrust" },
            TextWriter.Null, stderr, services: new CliServices());

        Assert.Equal(2, code);
        Assert.Contains("--tls-untrust", stderr.ToString());
    }
}
