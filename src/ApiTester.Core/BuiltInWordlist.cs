using System.Reflection;

namespace ApiTester.Core;

/// <summary>The starter endpoint wordlist embedded in the binary, so endpoint discovery works
/// with no external file. Users can still supply their own list for depth.</summary>
public static class BuiltInWordlist
{
    private const string ResourceName = "ApiTester.Core.common-api-endpoints.txt";

    private static readonly Lazy<string> _text = new(() =>
    {
        var asm = typeof(BuiltInWordlist).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded wordlist '{ResourceName}' is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>The raw wordlist text (comments and blank lines included).</summary>
    public static string Text => _text.Value;

    /// <summary>The parsed built-in entries — <c>Count</c> is handy for the "using N endpoints" note.</summary>
    public static IReadOnlyList<EndpointEntry> Entries => EndpointList.Parse(Text);
}
