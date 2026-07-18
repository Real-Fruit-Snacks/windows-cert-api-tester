using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApiTester.Core;

/// <summary>What part of the response an assertion checks.</summary>
public enum AssertTarget { Status, Time, Header, Body, BodyText }

/// <summary>How an assertion compares the actual value against its expected value.</summary>
public enum AssertOp { Equals, NotEquals, Contains, Matches, Exists, NotExists, LessThan, GreaterThan }

/// <summary>One assertion on a response: e.g. Status == 200, Body <c>data.id</c> exists,
/// Time &lt; 500, Header Content-Type contains json. Edited in the GUI and checked by <c>run</c>.</summary>
public sealed class AssertionRule : INotifyPropertyChanged
{
    private bool _enabled = true;
    private AssertTarget _target = AssertTarget.Status;
    private string _path = "";
    private AssertOp _op = AssertOp.Equals;
    private string _value = "";

    public bool Enabled { get => _enabled; set { _enabled = value; Raise(nameof(Enabled)); } }
    public AssertTarget Target { get => _target; set { _target = value; Raise(nameof(Target)); } }

    /// <summary>Header name (for <see cref="AssertTarget.Header"/>) or JSON path (for
    /// <see cref="AssertTarget.Body"/>); ignored for Status / Time / BodyText.</summary>
    public string Path { get => _path; set { _path = value; Raise(nameof(Path)); } }
    public AssertOp Op { get => _op; set { _op = value; Raise(nameof(Op)); } }
    public string Value { get => _value; set { _value = value; Raise(nameof(Value)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}

/// <summary>The outcome of evaluating one assertion.</summary>
public sealed record AssertionResult(bool Passed, string Description, string? Actual);

/// <summary>Checks a response against a request's assertion rules.</summary>
public static class AssertionEvaluator
{
    /// <summary>Evaluate every enabled rule against the response, in order.</summary>
    public static IReadOnlyList<AssertionResult> Evaluate(IEnumerable<AssertionRule> rules, ApiResponse response)
    {
        var results = new List<AssertionResult>();
        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            results.Add(EvaluateOne(rule, response));
        }
        return results;
    }

    /// <summary>True when every enabled rule passes (vacuously true when there are none).</summary>
    public static bool AllPass(IEnumerable<AssertionRule> rules, ApiResponse response) =>
        Evaluate(rules, response).All(r => r.Passed);

    private static AssertionResult EvaluateOne(AssertionRule rule, ApiResponse response)
    {
        string? actual = ActualValue(rule, response);
        bool passed = Apply(rule.Op, actual, rule.Value.Trim());
        return new AssertionResult(passed, Describe(rule), actual);
    }

    private static string? ActualValue(AssertionRule rule, ApiResponse response) => rule.Target switch
    {
        AssertTarget.Status => response.StatusCode?.ToString(),
        AssertTarget.Time => ((long)response.Elapsed.TotalMilliseconds).ToString(),
        AssertTarget.Header => response.Headers
            .FirstOrDefault(h => h.Key.Equals(rule.Path.Trim(), StringComparison.OrdinalIgnoreCase)).Value,
        AssertTarget.Body => JsonValue(response.Body, rule.Path.Trim()),
        AssertTarget.BodyText => Encoding.UTF8.GetString(response.Body),
        _ => null
    };

    /// <summary>Extract a JSON value at <paramref name="path"/> as a string; null when the body is
    /// not JSON, the path is missing, or the value is a container.</summary>
    private static string? JsonValue(byte[] body, string path)
    {
        if (body.Length == 0) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return null; }
        using (doc)
        {
            if (JsonPath.Evaluate(doc.RootElement, path) is not { } el) return null;
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => el.GetRawText(),
                JsonValueKind.Null => null,
                _ => el.GetRawText()   // object/array: its JSON, so Exists/Contains still work
            };
        }
    }

    private static bool Apply(AssertOp op, string? actual, string value) => op switch
    {
        AssertOp.Exists => actual is not null,
        AssertOp.NotExists => actual is null,
        AssertOp.Equals => string.Equals(actual, value, StringComparison.Ordinal),
        AssertOp.NotEquals => !string.Equals(actual, value, StringComparison.Ordinal),
        AssertOp.Contains => actual is not null && actual.Contains(value, StringComparison.Ordinal),
        AssertOp.Matches => actual is not null && TryRegex(value, actual),
        AssertOp.LessThan => TryNum(actual, out var a) && TryNum(value, out var v) && a < v,
        AssertOp.GreaterThan => TryNum(actual, out var a) && TryNum(value, out var v) && a > v,
        _ => false
    };

    private static bool TryRegex(string pattern, string input)
    {
        try { return Regex.IsMatch(input, pattern); }
        catch (ArgumentException) { return false; }   // an invalid pattern never matches
    }

    private static bool TryNum(string? s, out double n) =>
        double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out n);

    private static string Describe(AssertionRule rule)
    {
        string target = rule.Target switch
        {
            AssertTarget.Status => "Status",
            AssertTarget.Time => "Time (ms)",
            AssertTarget.Header => $"Header {rule.Path.Trim()}",
            AssertTarget.Body => $"Body {rule.Path.Trim()}",
            AssertTarget.BodyText => "Body text",
            _ => rule.Target.ToString()
        };
        string op = rule.Op switch
        {
            AssertOp.Equals => "==", AssertOp.NotEquals => "!=",
            AssertOp.Contains => "contains", AssertOp.Matches => "matches",
            AssertOp.Exists => "exists", AssertOp.NotExists => "is absent",
            AssertOp.LessThan => "<", AssertOp.GreaterThan => ">",
            _ => rule.Op.ToString()
        };
        return rule.Op is AssertOp.Exists or AssertOp.NotExists
            ? $"{target} {op}"
            : $"{target} {op} {rule.Value.Trim()}";
    }
}
