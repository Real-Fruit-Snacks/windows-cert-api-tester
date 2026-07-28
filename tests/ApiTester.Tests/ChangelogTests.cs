using System.IO;
using System.Text.RegularExpressions;

namespace ApiTester.Tests;

/// <summary>Guards the changelog's internal consistency.
///
/// <para>These exist because the file silently lost nine releases. Each release was written by
/// editing the heading that was already at the top rather than inserting a new one above it, so
/// v1.76.0 through v1.84.0 ended up merged under a single heading that kept being renamed to
/// whatever shipped last. The compare-link footer was updated correctly every time, which is what
/// eventually gave the problem away — and is what the first test below checks. Nothing about the
/// released software was wrong; the record of it was.</para></summary>
public class ChangelogTests
{
    private static string? FindRepoFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;   // not a source checkout; there is nothing to check
    }

    private static string? Changelog()
    {
        string? path = FindRepoFile("CHANGELOG.md");
        return path is null ? null : File.ReadAllText(path);
    }

    [Fact]
    public void Every_compare_link_has_a_section_and_every_section_has_a_compare_link()
    {
        if (Changelog() is not { } text) return;

        var sections = Regex.Matches(text, @"^## \[(\d+\.\d+\.\d+)\]", RegexOptions.Multiline)
                            .Select(m => m.Groups[1].Value).ToList();
        var links = Regex.Matches(text, @"^\[(\d+\.\d+\.\d+)\]:", RegexOptions.Multiline)
                         .Select(m => m.Groups[1].Value).ToList();

        // Named individually so a failure says which version is missing, not merely that a count
        // was off — that is the difference between a five-second fix and an afternoon.
        Assert.Empty(links.Except(sections));      // a link with no section: the merge-under-one-heading bug
        Assert.Empty(sections.Except(links));      // a section nobody can diff
    }

    [Fact]
    public void No_version_is_documented_twice()
    {
        if (Changelog() is not { } text) return;

        var sections = Regex.Matches(text, @"^## \[(\d+\.\d+\.\d+)\]", RegexOptions.Multiline)
                            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(sections.Distinct().Count(), sections.Count);
    }

    [Fact]
    public void The_version_being_built_is_documented()
    {
        if (Changelog() is not { } text) return;
        if (FindRepoFile(Path.Combine("src", "ApiTester.Cli", "ApiTester.Cli.csproj")) is not { } proj) return;

        var version = Regex.Match(File.ReadAllText(proj), @"<Version>([^<]+)</Version>").Groups[1].Value.Trim();
        Assert.NotEmpty(version);
        Assert.Contains($"## [{version}]", text);
    }
}
