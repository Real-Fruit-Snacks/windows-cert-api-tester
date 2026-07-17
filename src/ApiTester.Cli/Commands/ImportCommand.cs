using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class ImportCommand
{
    public const string Help = """
        Usage: certapi import curl "<curl command>" [--into <folder>] [--workspace <file>]
               certapi import openapi <file>        [--into <folder>] [--workspace <file>]

        Adds requests to your collections — the live GUI state by default, or a workspace
        file. --into names a root-level folder (created if needed).
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string? into = args.Value("--into");
        string? workspace = args.Value("--workspace");
        var positionals = args.Positionals();
        if (positionals.Count != 2) throw new CliUsageException(Help);

        string targetPath = workspace ?? services.LiveStatePath;
        if (workspace is null && services.IsGuiRunning())
            throw new CliDataException(
                "The GUI is running and would overwrite this change when it closes — close the app, or import into a --workspace file.");

        // Importing may target a brand-new workspace file: start empty and create it on save.
        var state = workspace is not null && !File.Exists(workspace)
            ? new AppState()
            : CliWorkspace.Load(workspace, services.LiveStatePath);

        int added;
        string what;
        var add = Target(state, into);
        switch (positionals[0].ToLowerInvariant())
        {
            case "curl":
            {
                ParsedRequest parsed;
                try { parsed = CurlParser.Parse(positionals[1]); }
                catch (Exception ex) { throw new CliDataException($"Could not parse the curl command: {ex.Message}"); }
                var model = RequestModel.FromParsed(parsed);
                var name = string.IsNullOrWhiteSpace(parsed.Name) ? $"{model.Method} {model.Path}" : parsed.Name!;
                add(new CollectionNode { Name = name, IsFolder = false, Request = model });
                added = 1; what = name;
                break;
            }
            case "openapi":
            {
                if (!File.Exists(positionals[1])) throw new CliDataException($"File not found: {positionals[1]}");
                ParsedCollection pc;
                try { pc = OpenApiImporter.Parse(File.ReadAllText(positionals[1])); }
                catch (Exception ex) { throw new CliDataException($"Could not parse '{positionals[1]}': {ex.Message}"); }
                var node = CollectionNode.FromParsed(pc);
                add(node);
                added = CountRequests(node); what = node.Name;
                break;
            }
            default: throw new CliUsageException(Help);
        }

        state.SaveTo(targetPath);
        stderr.WriteLine($"Imported {added} request{(added == 1 ? "" : "s")} ({what}) into {(workspace is null ? "the live workspace" : workspace)}.");
        return ExitCodes.Ok;
    }

    /// <summary>Where new nodes go: the collections root, or a root folder found/created by name.</summary>
    private static Action<CollectionNode> Target(AppState state, string? into)
    {
        if (into is null) return n => state.Collections.Add(n);
        var folder = state.Collections.FirstOrDefault(
            n => n.IsFolder && n.Name.Equals(into, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
        {
            folder = new CollectionNode { Name = into, IsFolder = true };
            state.Collections.Add(folder);
        }
        return n => folder.Children.Add(n);
    }

    private static int CountRequests(CollectionNode n) =>
        n.IsFolder ? n.Children.Sum(CountRequests) : 1;
}
