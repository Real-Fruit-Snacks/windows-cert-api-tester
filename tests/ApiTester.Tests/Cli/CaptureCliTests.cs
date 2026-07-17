using System.IO;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class CaptureCliTests
{
    private static (X509Certificate2 ca, X509Certificate2 server, X509Certificate2 client) Certs()
    {
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        var server = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        var client = SelfSignedCertificateFactory.CreateSignedCertificate("CapClient", ca, false, true);
        return (ca, server, client);
    }

    private static CliServices Services(X509Certificate2 client, string livePath) => new()
    {
        ListCertificates = _ => new[]
        {
            new CertificateInfo { Subject = "CN=CapClient", Issuer = "CN=CA", Thumbprint = client.Thumbprint!,
                NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                HasClientAuthEku = true, Certificate = client }
        },
        IsGuiRunning = () => false,
        LiveStatePath = livePath
    };

    [Fact]
    public async Task Send_capture_writes_a_variable_into_the_workspace()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartAsync(server, client.Thumbprint!, "{\"access_token\":\"tok-123\"}");
            var ws = Path.Combine(Path.GetTempPath(), $"cap-{Guid.NewGuid():N}.json");
            try
            {
                int code = CliApp.Run(new[]
                {
                    "send", upstream.BaseUrl, "--cert", "CapClient", "--insecure",
                    "--capture", "token=access_token", "--workspace", ws
                }, new StringWriter(), new StringWriter(), new MemoryStream(), Services(client, "unused"));
                Assert.Equal(0, code);

                var state = AppState.LoadFrom(ws);
                var env = state.Environments.Single(e => e.Id == state.ActiveEnvironmentId);
                Assert.Equal("tok-123", env.Variables.Single(v => v.Key == "token").Value);
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public void Malformed_capture_is_a_usage_error()
    {
        int code = CliApp.Run(new[] { "send", "https://x", "--capture", "novalue" },
            new StringWriter(), new StringWriter(), new MemoryStream(), new CliServices());
        Assert.Equal(2, code);
    }
}
