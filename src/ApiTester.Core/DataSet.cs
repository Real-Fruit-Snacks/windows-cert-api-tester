using System.Text;
using System.Text.Json;

namespace ApiTester.Core;

/// <summary>Loads a data-driven test dataset — a CSV (header row + rows) or a JSON array of objects —
/// into a list of string rows whose columns are available as <c>{{variables}}</c> per iteration.</summary>
public static class DataSet
{
    /// <summary>Load a dataset by file extension (.json → JSON array, otherwise CSV).</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Data file not found: {path}");
        var text = File.ReadAllText(path);
        return Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? ParseJson(text) : ParseCsv(text);
    }

    /// <summary>Parse CSV with a header row; quoted fields may contain commas and "" escapes.</summary>
    public static List<IReadOnlyDictionary<string, string>> ParseCsv(string text)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        var lines = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToList();
        if (lines.Count < 2) return rows;

        var headers = SplitLine(lines[0]).Select(h => h.Trim()).ToList();
        for (int i = 1; i < lines.Count; i++)
        {
            var fields = SplitLine(lines[i]);
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int c = 0; c < headers.Count; c++)
                if (headers[c].Length > 0)
                    row[headers[c]] = c < fields.Count ? fields[c] : "";
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Parse a JSON array of objects; each value is stringified (a string as-is, else its JSON).</summary>
    public static List<IReadOnlyDictionary<string, string>> ParseJson(string text)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return rows;
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in element.EnumerateObject())
                row[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.Null => "",
                    _ => prop.Value.GetRawText()
                };
            rows.Add(row);
        }
        return rows;
    }

    private static List<string> SplitLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
