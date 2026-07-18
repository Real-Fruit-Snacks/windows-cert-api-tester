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
    public void Parses_to_a_useful_number_of_endpoints()
    {
        var entries = BuiltInWordlist.Entries;
        Assert.True(entries.Count >= 20, $"expected a substantial starter list, got {entries.Count}");
    }

    [Fact]
    public void Includes_common_probes()
    {
        var paths = BuiltInWordlist.Entries.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("/health", paths);
        Assert.Contains("/login", paths);
    }
}
