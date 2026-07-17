namespace ApiTester.Cli.Mcp;

/// <summary>Decides whether a URL may be contacted. Empty allow-list means any http(s) host is
/// permitted; otherwise the host must equal an allowed entry or be a dotted subdomain of one.</summary>
public sealed class HostAllowlist
{
    private readonly List<string> _allowed;

    public HostAllowlist(IReadOnlyList<string> allowed) =>
        _allowed = allowed.Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Length > 0).ToList();

    public bool IsAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (_allowed.Count == 0) return true;

        string host = uri.Host.ToLowerInvariant();
        foreach (var entry in _allowed)
            if (host == entry || host.EndsWith("." + entry, StringComparison.Ordinal))
                return true;
        return false;
    }
}
