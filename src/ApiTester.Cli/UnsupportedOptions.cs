namespace ApiTester.Cli;

/// <summary>Options this tool deliberately does not have, paired with the reason and the thing to
/// use instead.
///
/// <para>The point is to answer the question the user actually asked. Someone who types
/// <c>--keylog</c> read somewhere that it exists and wants to decrypt traffic; "Unknown option
/// '--keylog'." is technically true and completely useless to them. A one-line explanation at the
/// exact moment they hit the wall is worth more than the same paragraph buried in a wiki page they
/// have no reason to open.</para>
///
/// <para>Only add entries here for options a reasonable person would expect to exist. This is not
/// a place to list typos.</para></summary>
public static class UnsupportedOptions
{
    private static readonly Dictionary<string, string> Explanations = new(StringComparer.OrdinalIgnoreCase)
    {
        // Verified on .NET 9 / Windows: the switch and SSLKEYLOGFILE were tried in code, in
        // runtimeconfig.json, and in the process environment, and no key log was ever written.
        // Key logging in .NET is implemented behind OpenSSL; Windows TLS uses SChannel, which does
        // not expose session secrets. A flag here would only ever produce an empty file.
        ["--keylog"] = KeyLog,
        ["--key-log"] = KeyLog,
        ["--sslkeylogfile"] = KeyLog,
        ["--ssl-key-log-file"] = KeyLog,
    };

    private const string KeyLog =
        "TLS key logging is not available on Windows: .NET writes key logs only through OpenSSL, " +
        "and Windows TLS uses SChannel, which does not expose session secrets. Use '--wire' " +
        "instead — it prints the decrypted request and response directly, which is what the keys " +
        "would have been for. See wiki page 23 (Troubleshooting).";

    /// <summary>The explanation for <paramref name="option"/>, or null if it is simply unknown.</summary>
    public static string? Explain(string option) =>
        Explanations.TryGetValue(option, out var why) ? why : null;
}
