using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class ExportCommand
{
    public const string Help = """
        Usage: certapi export openapi [<folder>] -o <file> [--workspace <file>]
               certapi export workspace -o <file> [--workspace <file>]

        openapi: writes collections (optionally one root folder) as an OpenAPI 3.0
        document — auth is exported as a security scheme only, never the secrets.
        workspace: writes the whole workspace as a portable JSON file (window geometry
        stripped). Note: workspace files include request auth values and history.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string? workspace = args.Value("--workspace");
        string? outFile = args.Value("-o", "--output");
        var positionals = args.Positionals();
        if (positionals.Count is < 1 or > 2 || outFile is null) throw new CliUsageException(Help);

        var state = CliWorkspace.Load(workspace, services.LiveStatePath);

        switch (positionals[0].ToLowerInvariant())
        {
            case "openapi":
            {
                CollectionNode wrapper;
                if (positionals.Count == 2)
                {
                    wrapper = state.Collections.FirstOrDefault(n =>
                            n.IsFolder && n.Name.Equals(positionals[1], StringComparison.OrdinalIgnoreCase))
                        ?? throw new CliDataException($"No folder named '{positionals[1]}' at the collections root.");
                }
                else
                {
                    wrapper = new CollectionNode { Name = "API collection", IsFolder = true };
                    foreach (var n in state.Collections) wrapper.Children.Add(n);
                }
                var pc = wrapper.ToParsed();
                if (pc.Requests.Count == 0 && pc.Folders.Count == 0)
                    throw new CliDataException("Nothing to export — no saved requests found.");
                File.WriteAllText(outFile, OpenApiExporter.ToJson(pc));
                stderr.WriteLine($"Exported OpenAPI to {outFile}.");
                return ExitCodes.Ok;
            }
            case "workspace":
            {
                if (positionals.Count != 1) throw new CliUsageException(Help);
                var clone = JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(state))!;
                clone.WindowWidth = clone.WindowHeight = clone.WindowLeft = clone.WindowTop = null;
                clone.WindowMaximized = false;
                clone.SaveTo(outFile);
                stderr.WriteLine($"Workspace exported to {outFile} — it includes auth values and history, so treat it as private.");
                return ExitCodes.Ok;
            }
            default: throw new CliUsageException(Help);
        }
    }
}
