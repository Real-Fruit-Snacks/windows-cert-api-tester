using ApiTester.Cli;
using ApiTester.Core;
using System;
using System.IO;

namespace ApiTester.Tests.Cli;

public class CertsCommandTests
{
    private static CliServices Services(params CertificateInfo[] certs) => new()
    {
        ListCertificates = _ => certs
    };

    private static CertificateInfo Fake(string subject)
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        var cert = SelfSignedCertificateFactory.CreateSignedCertificate(subject.Replace("CN=", ""), ca, false, true);
        return new CertificateInfo
        {
            Subject = subject, Issuer = "CN=CA", Thumbprint = cert.Thumbprint!,
            NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
            HasClientAuthEku = true, Certificate = cert
        };
    }

    [Fact]
    public void Lists_certificates_as_a_table()
    {
        var so = new StringWriter();
        int code = CliApp.Run(new[] { "certs" }, so, TextWriter.Null, services: Services(Fake("CN=Alice")));
        Assert.Equal(0, code);
        Assert.Contains("CN=Alice", so.ToString());
    }

    [Fact]
    public void Filter_narrows_and_json_is_parseable()
    {
        var so = new StringWriter();
        int code = CliApp.Run(new[] { "certs", "--filter", "bob", "--json" }, so, TextWriter.Null,
                              services: Services(Fake("CN=Alice"), Fake("CN=Bob")));
        Assert.Equal(0, code);
        using var doc = System.Text.Json.JsonDocument.Parse(so.ToString());
        var one = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("CN=Bob", one.GetProperty("subject").GetString());
    }

    [Fact]
    public void Invalid_store_is_a_usage_error()
    {
        var se = new StringWriter();
        int code = CliApp.Run(new[] { "certs", "--store", "Nope" }, new StringWriter(), se, services: Services());
        Assert.Equal(2, code);
        Assert.Contains("CurrentUser or LocalMachine", se.ToString());
    }

    [Fact]
    public void Extra_positionals_are_a_usage_error()
    {
        int code = CliApp.Run(new[] { "certs", "stray" }, new StringWriter(), new StringWriter(), services: Services());
        Assert.Equal(2, code);
    }

    [Fact]
    public void Empty_results_exit_0_with_a_notice_in_both_modes()
    {
        var seTable = new StringWriter();
        Assert.Equal(0, CliApp.Run(new[] { "certs" }, new StringWriter(), seTable, services: Services()));
        Assert.Contains("No client certificates", seTable.ToString());

        var so = new StringWriter();
        var seJson = new StringWriter();
        Assert.Equal(0, CliApp.Run(new[] { "certs", "--json" }, so, seJson, services: Services()));
        Assert.Contains("No client certificates", seJson.ToString());
        Assert.Empty(System.Text.Json.JsonDocument.Parse(so.ToString()).RootElement.EnumerateArray());
    }
}
