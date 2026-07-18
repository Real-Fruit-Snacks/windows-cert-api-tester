using System.IO;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class CertFileCliTests
{
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    [Fact]
    public async Task Send_uses_a_client_certificate_from_a_pfx_file()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("FileClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        var pfx = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pfx");
        File.WriteAllBytes(pfx, clientCert.Export(X509ContentType.Pkcs12, "pw"));
        var state = TempState();
        try
        {
            var services = new CliServices { LiveStatePath = state, IsGuiRunning = () => false };
            var so = new StringWriter();
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "send", server.BaseUrl, "--cert-file", pfx, "--cert-password", "pw", "--insecure", "--json" },
                new StringReader(""), so, se, new MemoryStream(), services);

            Assert.Equal(0, code);
            Assert.Contains("\"status\": 200", so.ToString());
            Assert.Contains("\"clientCertPresented\": true", so.ToString());   // the file cert was actually used
        }
        finally { File.Delete(pfx); if (File.Exists(state)) File.Delete(state); }
    }

    [Fact]
    public void A_missing_cert_file_is_a_data_error()
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(
            new[] { "send", "https://x.example", "--cert-file", "C:\\no\\such.pfx" },
            new StringReader(""), so, se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });
        Assert.Equal(3, code);   // data error
        Assert.Contains("not found", se.ToString());
    }

    [Fact]
    public void Cert_and_cert_file_together_is_a_usage_error()
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(
            new[] { "send", "https://x.example", "--cert", "CN=x", "--cert-file", "c.pfx" },
            new StringReader(""), so, se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });
        Assert.Equal(2, code);   // usage error
        Assert.Contains("mutually exclusive", se.ToString());
    }
}
