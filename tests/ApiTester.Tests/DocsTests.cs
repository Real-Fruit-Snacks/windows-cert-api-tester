using System.IO;
using System.Text.RegularExpressions;

namespace ApiTester.Tests;

/// <summary>Guards the documentation's internal consistency.
///
/// <para>These exist because both kinds of rot had actually happened. Three links in the CLI
/// reference pointed at anchors that did not exist — `#import` and `#export` because the two
/// commands shared one heading called "import / export", and `#trust` because a real command had
/// no section at all. And two whole programs' worth of flags reached the wiki without ever being
/// added to the reference page, which is the page people search first.</para>
///
/// <para>A missing page or a dead anchor is invisible in review and obvious to a reader, which is
/// exactly the kind of thing a test should hold.</para></summary>
public class DocsTests
{
    private static string? Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "wiki"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;   // not a source checkout; there is nothing to check
    }

    private static IEnumerable<string> DocFiles(string root)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(root, "wiki"), "*.md")) yield return file;
        string readme = Path.Combine(root, "README.md");
        if (File.Exists(readme)) yield return readme;
    }

    /// <summary>GitHub's heading-to-anchor rule, near enough for the headings this project uses:
    /// lower-case, non-alphanumerics collapsed to hyphens, trimmed.</summary>
    private static string Anchor(string heading) =>
        Regex.Replace(heading.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    [Fact]
    public void Every_link_to_a_wiki_page_points_at_a_page_that_exists()
    {
        if (Root() is not { } root) return;

        var pages = Directory.GetFiles(Path.Combine(root, "wiki"), "*.md")
                             .Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var broken = new List<string>();

        foreach (var file in DocFiles(root))
        {
            string text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, @"\]\((\d\d-[A-Za-z0-9-]+\.md)(?:#[a-z0-9-]+)?\)"))
                if (!pages.Contains(match.Groups[1].Value))
                    broken.Add($"{Path.GetFileName(file)} → {match.Groups[1].Value}");
        }

        Assert.Empty(broken);
    }

    [Fact]
    public void Every_intra_page_anchor_points_at_a_heading_that_exists()
    {
        if (Root() is not { } root) return;

        var broken = new List<string>();
        foreach (var file in DocFiles(root))
        {
            string text = File.ReadAllText(file);
            var headings = Regex.Matches(text, @"^#{1,4} (.+)$", RegexOptions.Multiline)
                                .Select(m => Anchor(m.Groups[1].Value)).ToHashSet();

            foreach (Match match in Regex.Matches(text, @"\]\(#([a-z0-9-]+)\)"))
                if (!headings.Contains(match.Groups[1].Value))
                    broken.Add($"{Path.GetFileName(file)} → #{match.Groups[1].Value}");
        }

        Assert.Empty(broken);
    }

    [Fact]
    public void Every_command_the_cli_dispatches_has_a_section_in_the_reference()
    {
        // The reference page is where people look first, and it had fallen behind by two whole
        // programs. Reading the dispatch table rather than a hand-kept list is what keeps this
        // honest: a new command cannot ship undocumented.
        if (Root() is not { } root) return;
        string cliApp = Path.Combine(root, "src", "ApiTester.Cli", "CliApp.cs");
        string reference = Path.Combine(root, "wiki", "21-CLI-Reference.md");
        if (!File.Exists(cliApp) || !File.Exists(reference)) return;

        var commands = Regex.Matches(File.ReadAllText(cliApp), @"^\s+""([a-z]+)"" => Commands\.",
                                     RegexOptions.Multiline)
                            .Select(m => m.Groups[1].Value)
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
        Assert.NotEmpty(commands);   // the pattern still matches the dispatch table

        string text = File.ReadAllText(reference);
        var headings = Regex.Matches(text, @"^## (.+)$", RegexOptions.Multiline)
                            .Select(m => m.Groups[1].Value.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var undocumented = commands.Where(c => !headings.Contains(c)).ToList();
        Assert.Empty(undocumented);
    }
}
