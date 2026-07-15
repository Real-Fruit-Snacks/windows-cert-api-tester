using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>Builds a colored FlowDocument for JSON/XML using the Terminal Workbench palette.</summary>
internal static class SyntaxHighlighter
{
    private static SolidColorBrush B(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static readonly SolidColorBrush KeyBrush = B("#6bdcff");
    private static readonly SolidColorBrush StringBrush = B("#63f2ab");
    private static readonly SolidColorBrush NumberBrush = B("#f7a35c");
    private static readonly SolidColorBrush LiteralBrush = B("#b78cff");
    private static readonly SolidColorBrush PunctBrush = B("#63736f");
    private static readonly SolidColorBrush DefaultBrush = B("#b4c3bd");
    private static readonly SolidColorBrush TagBrush = B("#6bdcff");
    private static readonly SolidColorBrush AttrBrush = B("#f0c674");

    public static FlowDocument Build(string text, BodyKind kind)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };

        if (text.Length > 150_000 || kind is not (BodyKind.Json or BodyKind.Xml))
            paragraph.Inlines.Add(new Run(text) { Foreground = DefaultBrush });
        else if (kind == BodyKind.Json)
            HighlightJson(paragraph, text);
        else
            HighlightXml(paragraph, text);

        var doc = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5
        };
        return doc;
    }

    private static void Add(Paragraph p, string s, SolidColorBrush brush) =>
        p.Inlines.Add(new Run(s) { Foreground = brush });

    private static void HighlightJson(Paragraph p, string s)
    {
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (c == '"')
            {
                int start = i++;
                while (i < n)
                {
                    if (s[i] == '\\') { i += 2; continue; }
                    if (s[i] == '"') { i++; break; }
                    i++;
                }
                int j = i;
                while (j < n && char.IsWhiteSpace(s[j])) j++;
                bool isKey = j < n && s[j] == ':';
                Add(p, s[start..i], isKey ? KeyBrush : StringBrush);
            }
            else if (char.IsDigit(c) || (c == '-' && i + 1 < n && char.IsDigit(s[i + 1])))
            {
                int start = i++;
                while (i < n && (char.IsDigit(s[i]) || s[i] is '.' or 'e' or 'E' or '+' or '-')) i++;
                Add(p, s[start..i], NumberBrush);
            }
            else if (Matches(s, i, "true") || Matches(s, i, "false") || Matches(s, i, "null"))
            {
                string lit = s[i] == 't' ? "true" : s[i] == 'f' ? "false" : "null";
                Add(p, lit, LiteralBrush);
                i += lit.Length;
            }
            else if (c is '{' or '}' or '[' or ']' or ':' or ',')
            {
                Add(p, c.ToString(), PunctBrush);
                i++;
            }
            else
            {
                int start = i;
                while (i < n && !"\"{}[]:,".Contains(s[i]) && !char.IsDigit(s[i]) && s[i] != '-'
                       && !Matches(s, i, "true") && !Matches(s, i, "false") && !Matches(s, i, "null"))
                    i++;
                if (i == start) i++;
                Add(p, s[start..i], DefaultBrush);
            }
        }
    }

    private static bool Matches(string s, int i, string word) =>
        i + word.Length <= s.Length && s.AsSpan(i, word.Length).SequenceEqual(word);

    private static void HighlightXml(Paragraph p, string s)
    {
        int i = 0, n = s.Length;
        while (i < n)
        {
            if (s[i] == '<')
            {
                int start = i++;
                while (i < n && s[i] != '>') i++;
                if (i < n) i++;
                HighlightXmlTag(p, s[start..i]);
            }
            else
            {
                int start = i;
                while (i < n && s[i] != '<') i++;
                Add(p, s[start..i], DefaultBrush);
            }
        }
    }

    private static void HighlightXmlTag(Paragraph p, string tag)
    {
        int i = 0, n = tag.Length;
        while (i < n)
        {
            char c = tag[i];
            if (c is '"' or '\'')
            {
                char quote = c;
                int start = i++;
                while (i < n && tag[i] != quote) i++;
                if (i < n) i++;
                Add(p, tag[start..i], StringBrush);
            }
            else if (c is '<' or '>' or '/' or '?' or '!' or '=')
            {
                Add(p, c.ToString(), TagBrush);
                i++;
            }
            else
            {
                int start = i;
                while (i < n && tag[i] != '"' && tag[i] != '\'' && tag[i] != '>' && tag[i] != '<'
                       && tag[i] != '=' && tag[i] != '/') i++;
                if (i == start) i++;
                Add(p, tag[start..i], AttrBrush);
            }
        }
    }
}
