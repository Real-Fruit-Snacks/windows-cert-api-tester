namespace ApiTester.Cli;

/// <summary>Deliberately minimal validation for an HTTP method value from the command line: an
/// empty or whitespace-only value is a usage error, and everything else is passed through
/// untouched, because arbitrary extension methods are legal HTTP — <c>PATCH</c>, a lowercase
/// <c>get</c>, and a non-standard verb all have to keep working.</summary>
public static class MethodOption
{
    /// <summary>Returns silently when <paramref name="value"/> is <c>null</c> (the option was not
    /// given at all) or is non-empty and not entirely whitespace. Throws <see cref="CliUsageException"/>
    /// otherwise.</summary>
    public static void Require(string? value, string flag)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
            throw new CliUsageException(Describe(flag, value));
    }

    /// <summary>The single-method usage message, shared by <see cref="Require"/> and by the MCP
    /// send_request tool (which reports the same problem through its own written-error shape
    /// instead of throwing) so the two can never diverge.</summary>
    public static string Describe(string flag, string? value) =>
        $"{flag} expects an HTTP method name, got '{value}' (for example GET, POST, or PATCH).";

    /// <summary>Returns silently when <paramref name="value"/> is <c>null</c> (the option was not
    /// given at all) or splitting it on commas yields at least one method. Throws
    /// <see cref="CliUsageException"/> when a value was given but nothing came out of the split —
    /// <c>""</c>, <c>"   "</c>, or <c>",,"</c> — instead of silently falling back to GET.</summary>
    public static void RequireList(string? value, string flag, int splitCount)
    {
        if (value is not null && splitCount == 0)
            throw new CliUsageException(DescribeList(flag, value));
    }

    /// <summary>The comma-separated-list usage message for <c>-X/--methods</c> (fuzz).</summary>
    public static string DescribeList(string flag, string? value) =>
        $"{flag} expects a comma-separated list of HTTP methods, got '{value}' (for example GET,POST).";
}
