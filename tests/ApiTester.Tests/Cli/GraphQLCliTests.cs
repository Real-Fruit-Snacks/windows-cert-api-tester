using System.IO;
using System.Text;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class GraphQLCliTests
{
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    [Fact]
    public async Task Send_posts_a_graphql_query_with_variables()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartEchoAsync(serverCert, clientCert.Thumbprint!);

        var services = new CliServices
        {
            LiveStatePath = TempState(), IsGuiRunning = () => false,
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
        var so = new StringWriter();
        var se = new StringWriter();
        var bodyOut = new MemoryStream();
        int code = CliApp.Run(
            new[] { "send", server.BaseUrl, "--cert", "CliClient", "--insecure",
                    "--graphql", "query($id:ID!){ user(id:$id){ name } }", "--gql-variables", "{\"id\":7}" },
            new StringReader(""), so, se, bodyOut, services);

        string echoed = Encoding.UTF8.GetString(bodyOut.ToArray());
        Assert.Equal(0, code);
        Assert.Contains("POST /", echoed);
        Assert.Contains("application/json", echoed);
        Assert.Contains("\"query\"", echoed);
        Assert.Contains("user(id:$id)", echoed);
        Assert.Contains("\"variables\"", echoed);
        Assert.Contains("\"id\":7", echoed);
    }

    [Fact]
    public void Graphql_with_data_is_a_usage_error()
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(new[] { "send", "https://x.example", "--graphql", "{ x }", "-d", "body" },
            new StringReader(""), so, se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });
        Assert.Equal(2, code);
        Assert.Contains("cannot be combined", se.ToString());
    }

    [Fact]
    public void Bad_graphql_variables_is_a_data_error()
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(new[] { "send", "https://x.example", "--graphql", "{ x }", "--gql-variables", "not json" },
            new StringReader(""), so, se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });
        Assert.Equal(3, code);
    }
}
