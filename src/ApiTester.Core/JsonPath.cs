using System.Text.Json;

namespace ApiTester.Core;

/// <summary>A minimal dotted JSON path: segments split on '.', each `name`, `name[index]`, or
/// `[index]`. Returns null on any missing segment, wrong kind, or out-of-range index.</summary>
public static class JsonPath
{
    public static JsonElement? Evaluate(JsonElement root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        JsonElement current = root;

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            string s = segment.Trim();

            // Split "name[1][2]" into a leading name (optional) and any trailing [index] parts.
            int bracket = s.IndexOf('[');
            string name = bracket < 0 ? s : s[..bracket];
            if (name.Length > 0)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                    return null;
            }

            if (bracket >= 0)
            {
                string rest = s[bracket..];
                var idxTokens = rest.Split('[', StringSplitOptions.RemoveEmptyEntries);
                if (idxTokens.Length == 0) return null;   // "name[" with nothing after
                foreach (var idxToken in idxTokens)
                {
                    if (!idxToken.EndsWith(']')) return null;   // unclosed like "name[0"
                    var t = idxToken.TrimEnd(']');
                    if (!int.TryParse(t, out int index) || current.ValueKind != JsonValueKind.Array) return null;
                    if (index < 0 || index >= current.GetArrayLength()) return null;
                    current = current[index];
                }
            }
        }
        return current;
    }
}
