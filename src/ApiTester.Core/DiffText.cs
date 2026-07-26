namespace ApiTester.Core;

/// <summary>Renders a <see cref="DiffResult"/> as plain text. It lives in Core, and not in the
/// command-line tool that prints it first, so the app's Diff view and a CI log show a user the
/// same wording for the same difference.</summary>
public static class DiffText
{
    /// <summary>A compact, human-readable rendering of a diff — one line per difference, with no
    /// trailing newline so a caller decides how it is terminated.</summary>
    public static string Format(DiffResult diff)
    {
        if (diff is null || diff.Identical) return "no differences";

        var lines = new List<string>();
        if (diff.StatusBefore != diff.StatusAfter)
            lines.Add($"status: {diff.StatusBefore} -> {diff.StatusAfter}");

        foreach (var header in diff.Headers)
            lines.Add($"header {header.Name}: {Side(header.Before)} -> {Side(header.After)}");

        foreach (var body in diff.Body)
            lines.Add($"body {Where(body.Path)} {Verb(body.Kind)}: {Side(body.Before)} -> {Side(body.After)}");

        return string.Join("\n", lines);
    }

    /// <summary>A missing value has to read as missing: an empty gap after the arrow looks like an
    /// empty string, which is a different fact.</summary>
    private static string Side(string? value) => value ?? "(absent)";

    /// <summary>An empty path means the whole body — a text or binary comparison has nowhere
    /// finer to point.</summary>
    private static string Where(string path) => path.Length == 0 ? "(body)" : path;

    private static string Verb(DiffKind kind) => kind switch
    {
        DiffKind.Added => "added",
        DiffKind.Removed => "removed",
        DiffKind.Changed => "changed",
        DiffKind.TypeChanged => "type changed",
        _ => kind.ToString()
    };
}
