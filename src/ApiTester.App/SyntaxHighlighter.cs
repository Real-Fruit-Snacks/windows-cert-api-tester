using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>Builds a colored FlowDocument for JSON/XML using the Terminal Workbench palette. Two
/// token palettes are kept — bright hues for the dark theme, deeper hues that read on white for the
/// light theme — and the active one is chosen per render from <see cref="App.CurrentTheme"/>.</summary>
internal static class SyntaxHighlighter
{
    private static SolidColorBrush B(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private sealed record Palette(
        SolidColorBrush Key, SolidColorBrush String, SolidColorBrush Number, SolidColorBrush Literal,
        SolidColorBrush Punct, SolidColorBrush Default, SolidColorBrush Tag, SolidColorBrush Attr);

    private static readonly Palette Dark = new(
        Key: B("#6bdcff"), String: B("#63f2ab"), Number: B("#f7a35c"), Literal: B("#b78cff"),
        Punct: B("#63736f"), Default: B("#b4c3bd"), Tag: B("#6bdcff"), Attr: B("#f0c674"));

    private static readonly Palette Light = new(
        Key: B("#0b6b8a"), String: B("#0f7a45"), Number: B("#b5560f"), Literal: B("#6d3bd1"),
        Punct: B("#8a968f"), Default: B("#33413b"), Tag: B("#0b6b8a"), Attr: B("#9a6a12"));

    private static Palette Current =>
        string.Equals((Application.Current as App)?.CurrentTheme, "Light", StringComparison.OrdinalIgnoreCase)
            ? Light : Dark;

    public static FlowDocument Build(string text, BodyKind kind)
    {
        var pal = Current;
        var paragraph = new Paragraph { Margin = new Thickness(0) };

        if (text.Length > 150_000 || kind is not (BodyKind.Json or BodyKind.Xml))
            paragraph.Inlines.Add(new Run(text) { Foreground = pal.Default });
        else if (kind == BodyKind.Json)
            HighlightJson(paragraph, text, pal);
        else
            HighlightXml(paragraph, text, pal);

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

    private static void HighlightJson(Paragraph p, string s, Palette pal)
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
                Add(p, s[start..i], isKey ? pal.Key : pal.String);
            }
            else if (char.IsDigit(c) || (c == '-' && i + 1 < n && char.IsDigit(s[i + 1])))
            {
                int start = i++;
                while (i < n && (char.IsDigit(s[i]) || s[i] is '.' or 'e' or 'E' or '+' or '-')) i++;
                Add(p, s[start..i], pal.Number);
            }
            else if (Matches(s, i, "true") || Matches(s, i, "false") || Matches(s, i, "null"))
            {
                string lit = s[i] == 't' ? "true" : s[i] == 'f' ? "false" : "null";
                Add(p, lit, pal.Literal);
                i += lit.Length;
            }
            else if (c is '{' or '}' or '[' or ']' or ':' or ',')
            {
                Add(p, c.ToString(), pal.Punct);
                i++;
            }
            else
            {
                int start = i;
                while (i < n && !"\"{}[]:,".Contains(s[i]) && !char.IsDigit(s[i]) && s[i] != '-'
                       && !Matches(s, i, "true") && !Matches(s, i, "false") && !Matches(s, i, "null"))
                    i++;
                if (i == start) i++;
                Add(p, s[start..i], pal.Default);
            }
        }
    }

    private static bool Matches(string s, int i, string word) =>
        i + word.Length <= s.Length && s.AsSpan(i, word.Length).SequenceEqual(word);

    private static void HighlightXml(Paragraph p, string s, Palette pal)
    {
        int i = 0, n = s.Length;
        while (i < n)
        {
            if (s[i] == '<')
            {
                int start = i++;
                while (i < n && s[i] != '>') i++;
                if (i < n) i++;
                HighlightXmlTag(p, s[start..i], pal);
            }
            else
            {
                int start = i;
                while (i < n && s[i] != '<') i++;
                Add(p, s[start..i], pal.Default);
            }
        }
    }

    private static void HighlightXmlTag(Paragraph p, string tag, Palette pal)
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
                Add(p, tag[start..i], pal.String);
            }
            else if (c is '<' or '>' or '/' or '?' or '!' or '=')
            {
                Add(p, c.ToString(), pal.Tag);
                i++;
            }
            else
            {
                int start = i;
                while (i < n && tag[i] != '"' && tag[i] != '\'' && tag[i] != '>' && tag[i] != '<'
                       && tag[i] != '=' && tag[i] != '/') i++;
                if (i == start) i++;
                Add(p, tag[start..i], pal.Attr);
            }
        }
    }
}
