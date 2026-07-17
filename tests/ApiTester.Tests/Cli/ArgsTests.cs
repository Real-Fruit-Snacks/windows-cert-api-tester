using ApiTester.Cli;

namespace ApiTester.Tests.Cli;

public class ArgsTests
{
    [Fact]
    public void Flags_values_and_positionals_parse()
    {
        var a = new Args(new[] { "https://x", "-X", "POST", "-H", "A: 1", "-H", "B: 2", "--insecure" });
        Assert.Equal("POST", a.Value("-X", "--method"));
        Assert.Equal(new[] { "A: 1", "B: 2" }, a.Values("-H", "--header"));
        Assert.True(a.Flag("--insecure"));
        Assert.False(a.Flag("--json"));
        Assert.Equal(new[] { "https://x" }, a.Positionals());
    }

    [Fact]
    public void Missing_value_is_a_usage_error()
    {
        var a = new Args(new[] { "-X" });
        Assert.Throws<CliUsageException>(() => a.Value("-X"));
    }

    [Fact]
    public void Unknown_option_is_a_usage_error()
    {
        var a = new Args(new[] { "url", "--nope" });
        var ex = Assert.Throws<CliUsageException>(() => a.Positionals());
        Assert.Contains("--nope", ex.Message);
    }

    [Fact]
    public void Option_value_may_start_with_a_dash_when_adjacent()
    {
        var a = new Args(new[] { "-d", "-negative-looking-body" });
        Assert.Equal("-negative-looking-body", a.Value("-d", "--data"));
        Assert.Empty(a.Positionals());
    }

    [Fact]
    public void Names_match_case_insensitively()
    {
        var a = new Args(new[] { "--INSECURE", "-x", "post" });
        Assert.True(a.Flag("--insecure"));
        Assert.Equal("post", a.Value("-X", "--method"));
        Assert.Empty(a.Positionals());
    }

    [Fact]
    public void A_repeated_flag_is_rejected_as_an_unknown_option()
    {
        var a = new Args(new[] { "--insecure", "--insecure" });
        Assert.True(a.Flag("--insecure"));            // consumes the first occurrence only
        var ex = Assert.Throws<CliUsageException>(() => a.Positionals());
        Assert.Contains("--insecure", ex.Message);
    }
}
