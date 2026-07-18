namespace ApiTester.Core;

/// <summary>Resolves the website/certificate a collection request inherits from its ancestor
/// folders — the nearest ancestor with a value wins, per value.</summary>
public static class CollectionDefaults
{
    public static (string? BaseUrl, string? CertThumbprint) For(IEnumerable<CollectionNode> roots, CollectionNode target)
    {
        var chain = AncestorsOf(roots, target);
        string? baseUrl = null, cert = null;
        for (int i = chain.Count - 1; i >= 0; i--)   // nearest first
        {
            baseUrl ??= string.IsNullOrWhiteSpace(chain[i].DefaultBaseUrl) ? null : chain[i].DefaultBaseUrl!.Trim();
            cert ??= string.IsNullOrEmpty(chain[i].DefaultCertThumbprint) ? null : chain[i].DefaultCertThumbprint;
        }
        return (baseUrl, cert);
    }

    /// <summary>The target's top-level ancestor folder, or null when it sits at the root.</summary>
    public static CollectionNode? RootOf(IEnumerable<CollectionNode> roots, CollectionNode target)
    {
        var chain = AncestorsOf(roots, target);
        return chain.Count > 0 ? chain[0] : null;
    }

    /// <summary>The folders from the tree root down to (excluding) the target; empty when the
    /// target is top-level or absent.</summary>
    private static List<CollectionNode> AncestorsOf(IEnumerable<CollectionNode> roots, CollectionNode target)
    {
        var chain = new List<CollectionNode>();
        return Walk(roots) ? chain : new List<CollectionNode>();

        bool Walk(IEnumerable<CollectionNode> scope)
        {
            foreach (var n in scope)
            {
                if (ReferenceEquals(n, target)) return true;
                if (!n.IsFolder) continue;
                chain.Add(n);
                if (Walk(n.Children)) return true;
                chain.RemoveAt(chain.Count - 1);
            }
            return false;
        }
    }
}
