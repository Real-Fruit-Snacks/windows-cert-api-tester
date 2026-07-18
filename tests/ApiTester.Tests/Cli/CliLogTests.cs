using System.IO;
using ApiTester.Cli;

namespace ApiTester.Tests.Cli;

public class CliLogTests
{
    [Fact]
    public void Extract_pulls_global_flags_from_anywhere()
    {
        var (rest, debug, logFile) = GlobalOptions.Extract(
            new[] { "send", "--debug", "https://x.example", "--log-file", "run.log", "--pretty" });
        Assert.True(debug);
        Assert.Equal("run.log", logFile);
        Assert.Equal(new[] { "send", "https://x.example", "--pretty" }, rest);

        var none = GlobalOptions.Extract(new[] { "certs" });
        Assert.False(none.Debug);
        Assert.Null(none.LogFile);
    }

    [Fact]
    public void Extract_rejects_a_dangling_log_file() =>
        Assert.Throws<CliUsageException>(() => GlobalOptions.Extract(new[] { "certs", "--log-file" }));

    [Fact]
    public void Debug_lines_reach_stderr_only_when_enabled()
    {
        var se = new StringWriter();
        using var quiet = CliLog.Create(debug: false, logFilePath: null, se);
        quiet.Debug("hidden");
        Assert.Equal("", se.ToString());

        using var loud = CliLog.Create(debug: true, logFilePath: null, se);
        loud.Debug("visible");
        Assert.Contains("debug: visible", se.ToString());
    }

    [Fact]
    public void Log_file_receives_debug_and_teed_stderr_lines()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".log");
        try
        {
            var se = new StringWriter();
            using (var log = CliLog.Create(debug: false, path, se))
            {
                log.Debug("a diagnostic");
                var tee = log.WrapStderr(se);
                tee.WriteLine("note: something happened");
            }
            var text = File.ReadAllText(path);
            Assert.Contains("[debug] a diagnostic", text);
            Assert.Contains("[stderr] note: something happened", text);
            Assert.Contains("something happened", se.ToString());   // still reaches real stderr

            using (var again = CliLog.Create(debug: false, path, new StringWriter()))
                again.Debug("appended");
            Assert.Contains("appended", File.ReadAllText(path));    // append, not truncate
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Unopenable_log_file_warns_but_does_not_fail()
    {
        var se = new StringWriter();
        var bad = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "nested", "x.log");
        // Make the *file path itself* invalid by pointing at an existing file as a directory.
        var blocker = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(blocker, "");
        try
        {
            using var log = CliLog.Create(debug: true, Path.Combine(blocker, "x.log"), se);
            log.Debug("still works");
            Assert.Contains("warning: could not open log file", se.ToString());
            Assert.Contains("debug: still works", se.ToString());
        }
        finally { File.Delete(blocker); }
    }

    [Fact]
    public void Describe_shows_the_stack_only_under_debug()
    {
        Exception ex;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception caught) { ex = caught; }

        using var quiet = CliLog.Create(false, null, TextWriter.Null);
        Assert.Equal("boom", quiet.Describe(ex));

        using var loud = CliLog.Create(true, null, TextWriter.Null);
        Assert.Contains("InvalidOperationException", loud.Describe(ex));
        Assert.Contains("CliLogTests", loud.Describe(ex));   // stack frame present
    }

    [Fact]
    public void CliApp_understands_the_global_flags_end_to_end()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".log");
        try
        {
            var so = new StringWriter();
            var se = new StringWriter();
            int code = CliApp.Run(new[] { "--debug", "--log-file", path, "help" }, so, se);
            Assert.Equal(0, code);
            Assert.Contains("Usage: certapi", so.ToString());
            Assert.True(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
