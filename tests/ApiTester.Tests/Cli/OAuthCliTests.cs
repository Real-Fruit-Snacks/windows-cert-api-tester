using System.IO;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class OAuthCliTests
{
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    private static CliServices ServicesWith(X509Certificate2 clientCert, string statePath) => new()
    {
        LiveStatePath = statePath,
        IsGuiRunning = () => false,
        ListCertificates = _ => new[]
        {
            new CertificateInfo
            {
                Subject = "CN=CliClient", Issuer = "CN=CA", Thumbprint = clientCert.Thumbprint!,
                NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                HasClientAuthEku = true, Certificate = clientCert
            }
        }
    };

    [Fact]
    public async Task Token_client_credentials_prints_the_access_token()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var srv = await LoopbackMtlsServer.StartOAuthTokenAsync(serverCert, clientCert.Thumbprint!, "app", "s3cret");

        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(
            new[] { "token", "--token-url", srv.BaseUrl, "--cert", "CliClient", "--insecure",
                    "--client-id", "app", "--client-secret", "s3cret", "--scope", "api.read" },
            new StringReader(""), so, se, new MemoryStream(), ServicesWith(clientCert, TempState()));

        Assert.Equal(0, code);
        Assert.Equal("at-client_credentials", so.ToString().Trim());
    }

    [Fact]
    public async Task Token_save_stores_it_for_the_api_origin()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var srv = await LoopbackMtlsServer.StartOAuthTokenAsync(serverCert, clientCert.Thumbprint!, "app", "s3cret");

        string statePath = TempState();
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(
            new[] { "token", "--token-url", srv.BaseUrl, "--cert", "CliClient", "--insecure",
                    "--client-id", "app", "--client-secret", "s3cret",
                    "--save", "--for", "https://api.example.com" },
            new StringReader(""), so, se, new MemoryStream(), ServicesWith(clientCert, statePath));

        Assert.Equal(0, code);
        var state = AppState.LoadFrom(statePath);
        var token = Assert.Single(state.SessionTokens);
        Assert.Equal("https://api.example.com:443", token.Origin);
        Assert.Equal("at-client_credentials", token.Token);
        Assert.Equal("oauth", token.Source);
        Assert.NotNull(token.ExpiresUtc);
    }

    [Fact]
    public async Task Token_bad_secret_exits_one_with_the_error()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var srv = await LoopbackMtlsServer.StartOAuthTokenAsync(serverCert, clientCert.Thumbprint!, "app", "s3cret");

        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(
            new[] { "token", "--token-url", srv.BaseUrl, "--cert", "CliClient", "--insecure",
                    "--client-id", "app", "--client-secret", "WRONG" },
            new StringReader(""), so, se, new MemoryStream(), ServicesWith(clientCert, TempState()));

        Assert.Equal(1, code);
        Assert.Contains("invalid_client", se.ToString());
    }

    [Fact]
    public async Task Token_save_creates_a_new_workspace_file()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var srv = await LoopbackMtlsServer.StartOAuthTokenAsync(serverCert, clientCert.Thumbprint!, "app", "s3cret");

        string wsPath = TempState();   // does not exist yet
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(
            new[] { "token", "--token-url", srv.BaseUrl, "--cert", "CliClient", "--insecure",
                    "--client-id", "app", "--client-secret", "s3cret",
                    "--save", "--for", "https://api.example.com", "--workspace", wsPath },
            new StringReader(""), so, se, new MemoryStream(), ServicesWith(clientCert, TempState()));

        Assert.Equal(0, code);
        Assert.True(File.Exists(wsPath), "the workspace file should have been created");
        var token = Assert.Single(AppState.LoadFrom(wsPath).SessionTokens);
        Assert.Equal("at-client_credentials", token.Token);
    }

    [Fact]
    public void Token_save_without_for_is_a_usage_error()
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(
            new[] { "token", "--token-url", "https://auth.example.com/token",
                    "--client-id", "app", "--client-secret", "x", "--save" },
            new StringReader(""), so, se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });

        Assert.Equal(2, code);
        Assert.Contains("--for", se.ToString());
    }
}
