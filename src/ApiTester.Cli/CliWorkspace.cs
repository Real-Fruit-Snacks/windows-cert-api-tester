using ApiTester.Core;

namespace ApiTester.Cli;

public static class CliWorkspace
{
    public static AppState Load(string? workspacePath, string liveStatePath)
    {
        if (workspacePath is null)
            return File.Exists(liveStatePath) ? SafeLoad(liveStatePath) : new AppState();
        if (!File.Exists(workspacePath))
            throw new CliDataException($"Workspace file not found: {workspacePath}");
        return SafeLoad(workspacePath);
    }

    private static AppState SafeLoad(string path)
    {
        try { return AppState.LoadFrom(path); }
        catch (Exception ex) { throw new CliDataException($"Could not read '{path}': {ex.Message}"); }
    }

    public static Dictionary<string, string> BuildVars(
        AppState state, string? envName, IReadOnlyList<string> varOverrides)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        if (envName is not null)
        {
            var env = state.Environments.FirstOrDefault(
                e => e.Name.Equals(envName, StringComparison.OrdinalIgnoreCase))
                ?? throw new CliDataException(
                    $"No environment named '{envName}'. Available: " +
                    (state.Environments.Count == 0 ? "(none)" : string.Join(", ", state.Environments.Select(e => e.Name))));
            foreach (var v in env.Variables)
                if (!string.IsNullOrWhiteSpace(v.Key)) vars[v.Key.Trim()] = v.Value ?? "";
        }
        foreach (var kv in varOverrides)
        {
            int eq = kv.IndexOf('=');
            if (eq <= 0) throw new CliUsageException($"--var expects key=value, got '{kv}'.");
            vars[kv[..eq].Trim()] = kv[(eq + 1)..];
        }
        return vars;
    }

    public static List<(string Path, CollectionNode Node)> ResolveTargets(AppState state, string? path, bool all)
    {
        if (!all && string.IsNullOrWhiteSpace(path))
            throw new CliUsageException("Give a collection path (e.g. \"api/Get todo\") or --all.");

        IEnumerable<CollectionNode> scope = state.Collections;
        var prefix = "";
        if (!all)
        {
            foreach (var segment in path!.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var matches = scope.Where(n => n.Name.Equals(segment, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0)
                    throw new CliDataException($"Nothing named '{segment}' under '{(prefix.Length == 0 ? "collections" : prefix.TrimEnd('/'))}'.");
                if (matches.Count > 1)
                    throw new CliDataException($"'{segment}' is ambiguous under '{prefix}' ({matches.Count} matches).");
                var node = matches[0];
                prefix += node.Name + "/";
                if (!node.IsFolder)
                    return Leaves(node, prefix.TrimEnd('/')) ;
                scope = node.Children;
            }
        }

        var result = new List<(string, CollectionNode)>();
        foreach (var n in scope) result.AddRange(Leaves(n, prefix + n.Name));
        if (result.Count == 0) throw new CliDataException("No saved requests found to run.");
        return result;
    }

    private static List<(string, CollectionNode)> Leaves(CollectionNode node, string path)
    {
        if (!node.IsFolder)
            return node.Request is null ? new() : new() { (path, node) };
        var list = new List<(string, CollectionNode)>();
        foreach (var c in node.Children) list.AddRange(Leaves(c, path + "/" + c.Name));
        return list;
    }
}
