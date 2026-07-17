using ApiTester.Cli;
using System.IO;

namespace ApiTester.Tests.Cli;

public class CliAppTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(args, so, se);
        return (code, so.ToString(), se.ToString());
    }

    [Fact]
    public void No_arguments_prints_usage_and_exits_2()
    {
        var r = Run();
        Assert.Equal(2, r.Code);
        Assert.Contains("Usage:", r.Err);
        Assert.Contains("certapi", r.Err);
    }

    [Fact]
    public void Unknown_command_exits_2_and_names_it()
    {
        var r = Run("frobnicate");
        Assert.Equal(2, r.Code);
        Assert.Contains("frobnicate", r.Err);
    }

    [Fact]
    public void Version_prints_and_exits_0()
    {
        var r = Run("--version");
        Assert.Equal(0, r.Code);
        Assert.Matches(@"certapi \d+\.\d+\.\d+", r.Out.Trim());
    }

    [Fact]
    public void Help_prints_usage_to_stdout_and_exits_0()
    {
        var r = Run("help");
        Assert.Equal(0, r.Code);
        Assert.Contains("Usage:", r.Out);
    }
}
