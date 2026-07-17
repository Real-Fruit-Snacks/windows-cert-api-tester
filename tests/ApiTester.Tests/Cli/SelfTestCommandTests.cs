using ApiTester.Cli;
using System.IO;

namespace ApiTester.Tests.Cli;

public class SelfTestCommandTests
{
    [Fact]
    public void Selftest_passes_and_prints_the_report()
    {
        var so = new StringWriter();
        int code = CliApp.Run(new[] { "selftest" }, so, TextWriter.Null);
        Assert.Equal(0, code);
        Assert.False(string.IsNullOrWhiteSpace(so.ToString()));
    }

    [Fact]
    public void Selftest_json_reports_passed_true()
    {
        var so = new StringWriter();
        int code = CliApp.Run(new[] { "selftest", "--json" }, so, TextWriter.Null);
        Assert.Equal(0, code);
        using var doc = System.Text.Json.JsonDocument.Parse(so.ToString());
        Assert.True(doc.RootElement.GetProperty("passed").GetBoolean());
    }
}
