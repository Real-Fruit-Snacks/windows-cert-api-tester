using ApiTester.Core;

namespace ApiTester.Tests;

public class BuiltInWordlistTests
{
    [Fact]
    public void Embedded_resource_loads_and_is_non_empty()
    {
        Assert.False(string.IsNullOrWhiteSpace(BuiltInWordlist.Text));
    }

    [Fact]
    public void Parses_to_a_professional_sized_list()
    {
        var entries = BuiltInWordlist.Entries;
        Assert.True(entries.Count >= 100, $"expected a substantial curated starter list, got {entries.Count}");
    }

    [Fact]
    public void Includes_common_probes_across_categories()
    {
        var paths = BuiltInWordlist.Entries.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // one representative from several categories
        Assert.Contains("/health", paths);        // operational
        Assert.Contains("/login", paths);          // auth
        Assert.Contains("/api", paths);            // api root
        Assert.Contains("/openapi.json", paths);   // docs/specs
        Assert.Contains("/admin", paths);          // admin
        Assert.Contains("/.env", paths);           // sensitive exposure
    }

    [Fact]
    public void Pins_methods_where_appropriate()
    {
        // POST-only endpoints are method-pinned so a probe hits them properly.
        Assert.Contains(BuiltInWordlist.Entries, e => e.Method == "POST" && e.Path == "/login");
    }

    [Fact]
    public void Has_no_duplicate_method_path_pairs()
    {
        var entries = BuiltInWordlist.Entries;
        var distinct = entries.Select(e => (e.Method ?? "", e.Path)).Distinct().Count();
        Assert.Equal(entries.Count, distinct);
    }
}
