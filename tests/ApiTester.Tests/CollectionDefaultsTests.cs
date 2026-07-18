using System.IO;
using ApiTester.Core;

namespace ApiTester.Tests;

public class CollectionDefaultsTests
{
    private static (List<CollectionNode> Roots, CollectionNode Leaf, CollectionNode Mid, CollectionNode Root) Tree()
    {
        var leaf = new CollectionNode { Name = "req", IsFolder = false, Request = new RequestModel() };
        var mid = new CollectionNode { Name = "mid", IsFolder = true };
        var root = new CollectionNode { Name = "root", IsFolder = true };
        mid.Children.Add(leaf);
        root.Children.Add(mid);
        return (new List<CollectionNode> { root }, leaf, mid, root);
    }

    [Fact]
    public void Nearest_ancestor_default_wins()
    {
        var (roots, leaf, mid, root) = Tree();
        root.DefaultBaseUrl = "https://root.example";
        root.DefaultCertThumbprint = "ROOTCERT";
        mid.DefaultBaseUrl = "https://mid.example";

        var (baseUrl, cert) = CollectionDefaults.For(roots, leaf);
        Assert.Equal("https://mid.example", baseUrl);   // nearest folder wins per value
        Assert.Equal("ROOTCERT", cert);                 // falls through where the nearer folder is silent
    }

    [Fact]
    public void No_defaults_yields_nulls_and_unknown_target_is_safe()
    {
        var (roots, leaf, _, _) = Tree();
        Assert.Equal((null, null), CollectionDefaults.For(roots, leaf));

        var stranger = new CollectionNode { Name = "x", IsFolder = false };
        Assert.Equal((null, null), CollectionDefaults.For(roots, stranger));
        Assert.Null(CollectionDefaults.RootOf(roots, stranger));
    }

    [Fact]
    public void RootOf_finds_the_top_level_ancestor()
    {
        var (roots, leaf, _, root) = Tree();
        Assert.Same(root, CollectionDefaults.RootOf(roots, leaf));

        var topLevel = new CollectionNode { Name = "solo", IsFolder = false, Request = new RequestModel() };
        roots.Add(topLevel);
        Assert.Null(CollectionDefaults.RootOf(roots, topLevel));   // no ancestor folder
    }

    [Fact]
    public void Defaults_round_trip_through_serialization()
    {
        var state = new AppState();
        var folder = new CollectionNode { Name = "api", IsFolder = true, DefaultBaseUrl = "https://x", DefaultCertThumbprint = "ABC" };
        state.Collections.Add(folder);
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            state.SaveTo(path);
            var loaded = AppState.LoadFrom(path);
            Assert.Equal("https://x", loaded.Collections[0].DefaultBaseUrl);
            Assert.Equal("ABC", loaded.Collections[0].DefaultCertThumbprint);
        }
        finally { File.Delete(path); }
    }
}
