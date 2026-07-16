using System.Text;

namespace ApiTester.Core;

/// <summary>Parses a <c>curl</c> command line into a <see cref="ParsedRequest"/>. Handles the
/// common flags (method, headers, data, user, insecure), quoting, and line continuations.</summary>
public static class CurlParser
{
    public static ParsedRequest Parse(string curl)
    {
        var req = new ParsedRequest();
        var tokens = Tokenize(curl ?? "");
        int i = 0;
        if (tokens.Count > 0 && tokens[0].Equals("curl", StringComparison.OrdinalIgnoreCase)) i = 1;

        string? explicitMethod = null;
        bool hasBody = false;

        for (; i < tokens.Count; i++)
        {
            var token = tokens[i];
            string opt = token;
            string? inlineVal = null;
            if (token.StartsWith("--") && token.Contains('='))
            {
                int eq = token.IndexOf('=');
                opt = token[..eq];
                inlineVal = token[(eq + 1)..];
            }

            string Next()
            {
                if (inlineVal is not null) return inlineVal;
                return ++i < tokens.Count ? tokens[i] : "";
            }

            switch (opt)
            {
                case "-X": case "--request": explicitMethod = Next().ToUpperInvariant(); break;
                case "-H": case "--header": AddHeader(req, Next()); break;
                case "-A": case "--user-agent": req.Headers.Add(new("User-Agent", Next())); break;
                case "-e": case "--referer": req.Headers.Add(new("Referer", Next())); break;
                case "-b": case "--cookie": req.Headers.Add(new("Cookie", Next())); break;
                case "-d": case "--data": case "--data-raw": case "--data-ascii":
                case "--data-binary": case "--data-urlencode":
                    req.Body = req.Body is null ? Next() : req.Body + "&" + Next();
                    hasBody = true;
                    break;
                case "-u": case "--user":
                    var up = Next();
                    int c = up.IndexOf(':');
                    if (c >= 0) { req.BasicUser = up[..c]; req.BasicPassword = up[(c + 1)..]; }
                    else req.BasicUser = up;
                    break;
                case "-k": case "--insecure": req.InsecureSkipVerify = true; break;
                case "--url": req.Url = Next(); break;
                default:
                    if (!opt.StartsWith("-") && opt.Length > 0)
                        req.Url = token; // a bare argument is the URL
                    // other flags (‑s, ‑L, ‑i, ‑v, --compressed, …) carry no value we need
                    break;
            }
        }

        req.Method = explicitMethod ?? (hasBody ? "POST" : "GET");
        ExtractContentTypeAndAuth(req);
        return req;
    }

    private static void AddHeader(ParsedRequest req, string raw)
    {
        int c = raw.IndexOf(':');
        if (c < 0) { if (raw.Length > 0) req.Headers.Add(new(raw.Trim(), "")); return; }
        req.Headers.Add(new(raw[..c].Trim(), raw[(c + 1)..].Trim()));
    }

    private static void ExtractContentTypeAndAuth(ParsedRequest req)
    {
        for (int i = req.Headers.Count - 1; i >= 0; i--)
        {
            var (name, value) = (req.Headers[i].Key, req.Headers[i].Value);
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                req.ContentType = value;
                req.Headers.RemoveAt(i);
            }
            else if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
                     value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                req.BearerToken = value["Bearer ".Length..].Trim();
                req.Headers.RemoveAt(i);
            }
        }
    }

    /// <summary>Split a command line into arguments, honoring single/double quotes,
    /// backslash escapes, and <c>\</c> / <c>^</c> line continuations.</summary>
    private static List<string> Tokenize(string input)
    {
        input = input
            .Replace("\\\r\n", " ").Replace("\\\n", " ")
            .Replace("^\r\n", " ").Replace("^\n", " ");

        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool has = false;
        char quote = '\0';

        for (int i = 0; i < input.Length; i++)
        {
            char ch = input[i];
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else if (quote == '"' && ch == '\\' && i + 1 < input.Length) sb.Append(input[++i]);
                else sb.Append(ch);
            }
            else if (ch is ' ' or '\t' or '\r' or '\n')
            {
                if (has) { tokens.Add(sb.ToString()); sb.Clear(); has = false; }
            }
            else if (ch is '\'' or '"') { quote = ch; has = true; }
            else if (ch == '\\' && i + 1 < input.Length) { sb.Append(input[++i]); has = true; }
            else { sb.Append(ch); has = true; }
        }
        if (has) tokens.Add(sb.ToString());
        return tokens;
    }
}
