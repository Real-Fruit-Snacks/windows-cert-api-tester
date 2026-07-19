namespace ApiTester.Core;

/// <summary>Parses a compact command-line assertion expression into an <see cref="AssertionRule"/>,
/// for <c>certapi send --assert</c>. Grammar:
/// <code>&lt;target&gt; [path] &lt;op&gt; [value]</code>
/// <list type="bullet">
///   <item>targets: <c>status</c>, <c>time</c>, <c>header &lt;name&gt;</c>, <c>body &lt;jsonpath&gt;</c>, <c>body-text</c></item>
///   <item>ops: <c>==</c> <c>!=</c> <c>contains</c> <c>matches</c> <c>exists</c> <c>!exists</c> <c>&lt;</c> <c>&gt;</c>
///     (aliases: <c>equals</c>, <c>notequals</c>, <c>lt</c>, <c>gt</c>, <c>missing</c>)</item>
/// </list>
/// Examples: <c>status == 200</c>, <c>time &lt; 500</c>, <c>header Content-Type contains json</c>,
/// <c>body data.id exists</c>, <c>body-text matches "id":\s*\d+</c>. Throws
/// <see cref="FormatException"/> with a helpful message on malformed input.</summary>
public static class AssertionParser
{
    public static AssertionRule Parse(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            throw new FormatException("empty assertion");

        var parts = expr.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var target = ParseTarget(parts[0], out bool needsPath);

        int i = 1;
        string path = "";
        if (needsPath)
        {
            if (parts.Length < 2)
                throw new FormatException($"'{parts[0]}' needs a name/path, e.g. \"{parts[0].ToLowerInvariant()} <name> exists\"");
            path = parts[1];
            i = 2;
        }

        if (parts.Length <= i)
            throw new FormatException($"assertion '{expr}' is missing an operator (== != contains matches exists !exists < >)");

        var op = ParseOp(parts[i]);
        string value = i + 1 < parts.Length ? string.Join(' ', parts[(i + 1)..]) : "";

        bool needsValue = op is not (AssertOp.Exists or AssertOp.NotExists);
        if (needsValue && value.Length == 0)
            throw new FormatException($"assertion '{expr}' is missing a value after '{parts[i]}'");

        return new AssertionRule { Enabled = true, Target = target, Path = path, Op = op, Value = value };
    }

    private static AssertTarget ParseTarget(string t, out bool needsPath)
    {
        needsPath = false;
        switch (t.ToLowerInvariant())
        {
            case "status": return AssertTarget.Status;
            case "time": return AssertTarget.Time;
            case "header": needsPath = true; return AssertTarget.Header;
            case "body": needsPath = true; return AssertTarget.Body;
            case "body-text": case "bodytext": case "text": return AssertTarget.BodyText;
            default:
                throw new FormatException($"unknown assertion target '{t}' (expected status, time, header, body, or body-text)");
        }
    }

    private static AssertOp ParseOp(string o) => o.ToLowerInvariant() switch
    {
        "==" or "=" or "eq" or "equals" => AssertOp.Equals,
        "!=" or "ne" or "notequals" => AssertOp.NotEquals,
        "contains" or "has" => AssertOp.Contains,
        "matches" or "~" or "regex" => AssertOp.Matches,
        "exists" or "present" => AssertOp.Exists,
        "!exists" or "notexists" or "missing" or "absent" => AssertOp.NotExists,
        "<" or "lt" => AssertOp.LessThan,
        ">" or "gt" => AssertOp.GreaterThan,
        _ => throw new FormatException($"unknown assertion operator '{o}' (== != contains matches exists !exists < >)")
    };
}
