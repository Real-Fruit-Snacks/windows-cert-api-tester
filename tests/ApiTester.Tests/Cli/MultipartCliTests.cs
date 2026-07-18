using System.IO;
using System.Text;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class MultipartCliTests
{
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    private static CliServices Services(System.Security.Cryptography.X509Certificates.X509Certificate2 client, string state) => new()
    {
        LiveStatePath = state,
        IsGuiRunning = () => false,
        ListCertificates = _ => new[]
        {
            new CertificateInfo
            {
                Subject = "CN=CliClient", Issuer = "CN=CA", Thumbprint = client.Thumbprint!,
                NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                HasClientAuthEku = true, Certificate = client
            }
        }
    };

    [Fact]
    public async Task Send_posts_multipart_form_with_a_text_field_and_a_file()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartEchoAsync(serverCert, clientCert.Thumbprint!);

        var file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        File.WriteAllText(file, "FILE-CONTENT-123");
        var state = TempState();
        try
        {
            var so = new StringWriter();
            var se = new StringWriter();
            var bodyOut = new MemoryStream();
            int code = CliApp.Run(
                new[] { "send", server.BaseUrl, "--cert", "CliClient", "--insecure",
                        "-F", "field=hello", "-F", "upload=@" + file },
                new StringReader(""), so, se, bodyOut, Services(clientCert, state));

            // The echo server returns the exact request it received; the multipart body is in it.
            string echoed = Encoding.UTF8.GetString(bodyOut.ToArray());
            Assert.Equal(0, code);
            Assert.Contains("multipart/form-data", echoed);
            Assert.Contains("name=field", echoed);
            Assert.Contains("hello", echoed);
            Assert.Contains("name=upload", echoed);
            Assert.Contains("filename=", echoed);
            Assert.Contains("FILE-CONTENT-123", echoed);       // the file's bytes were uploaded
        }
        finally { File.Delete(file); if (File.Exists(state)) File.Delete(state); }
    }

    [Fact]
    public void Form_and_data_together_is_a_usage_error()
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(
            new[] { "send", "https://x.example", "-F", "a=b", "-d", "body" },
            new StringReader(""), so, se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });
        Assert.Equal(2, code);
        Assert.Contains("mutually exclusive", se.ToString());
    }

    [Fact]
    public void A_missing_form_file_is_a_data_error()
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(
            new[] { "send", "https://x.example", "-F", "upload=@C:\\no\\such\\file.bin" },
            new StringReader(""), so, se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });
        Assert.Equal(3, code);
        Assert.Contains("not found", se.ToString());
    }
}
