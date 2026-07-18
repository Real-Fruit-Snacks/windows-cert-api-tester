using System.IO;
using System.Text;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class WindowsAuthCliTests
{
    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    [Fact]
    public async Task Send_windows_auth_flag_answers_the_challenge()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var so = new StringWriter();
        var se = new StringWriter();
        var body = new MemoryStream();

        int code = CliApp.Run(
            new[] { "send", srv.BaseUrl + "windows-auth", "--windows-auth" },
            new StringReader(""), so, se, body, new CliServices { LiveStatePath = TempState() });

        Assert.Equal(0, code);
        Assert.Contains("authenticated", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task Send_explicit_windows_credentials_also_answer_the_challenge()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);
        var so = new StringWriter();
        var se = new StringWriter();
        var body = new MemoryStream();

        // Explicit (even bogus) credentials still produce the initial NTLM message the mock accepts.
        int code = CliApp.Run(
            new[] { "send", srv.BaseUrl + "windows-auth", "--windows-user", @"CORP\tester", "--windows-password", "pw" },
            new StringReader(""), so, se, body, new CliServices { LiveStatePath = TempState() });

        Assert.Equal(0, code);
        Assert.Contains("NTLM", Encoding.UTF8.GetString(body.ToArray()));
    }
}
