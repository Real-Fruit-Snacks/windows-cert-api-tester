using System.Text.RegularExpressions;

namespace ApiTester.Core;

/// <summary>Expands <c>{{name}}</c> tokens in request fields from the active environment's
/// variables. Unknown tokens are left intact and reported so the user can see what's missing.</summary>
public static class VariableResolver
{
    private static readonly Regex Token = new(@"\{\{\s*([^{}]*?)\s*\}\}", RegexOptions.Compiled);

    public static (string Result, IReadOnlyList<string> Unresolved) Resolve(
        string template, IReadOnlyDictionary<string, string> vars)
    {
        if (string.IsNullOrEmpty(template)) return (template ?? "", Array.Empty<string>());

        var unresolved = new List<string>();
        var result = Token.Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (key.Length == 0) return m.Value;               // leave "{{}}" untouched
            if (vars.TryGetValue(key, out var v)) return v;
            if (!unresolved.Contains(key)) unresolved.Add(key);
            return m.Value;                                    // leave "{{unknown}}" intact
        });
        return (result, unresolved);
    }
}
