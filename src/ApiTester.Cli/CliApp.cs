using System.Reflection;
using ApiTester.Core;

namespace ApiTester.Cli;

/// <summary>Injectable seams so tests can run every command in-process.</summary>
public sealed class CliServices
{
    public Func<bool, IReadOnlyList<CertificateInfo>> ListCertificates { get; init; } =
        includeLocalMachine => new CertificateStoreService().ListClientCertificates(includeLocalMachine);

    public Func<bool> IsGuiRunning { get; init; } =
        () => System.Diagnostics.Process.GetProcessesByName("ApiTester.App").Length > 0;

    public string LiveStatePath { get; init; } = AppState.DefaultPath;

    public ApiClient Client { get; init; } = new();

    /// <summary>Wired to Ctrl+C by Program.cs so in-flight requests cancel cleanly.</summary>
    public CancellationToken Cancel { get; init; } = CancellationToken.None;
}

public static class CliApp
{
    public const string Usage = """
        Usage: certapi <command> [options]

        Commands:
          send <url>        Send a one-off request (client cert from the Windows store)
          run <path>        Run saved requests from your collections (or --all)
          certs             List client certificates
          selftest          Prove the mTLS path end-to-end against a loopback server
          import            Import a cURL command or an OpenAPI file into collections
          export            Export collections as OpenAPI, or the whole workspace
          help [command]    Show help (for one command, or this overview)

        Run 'certapi help <command>' for options. 'certapi --version' prints the version.
        """;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr,
                          Stream? bodyOut = null, CliServices? services = null)
    {
        services ??= new CliServices();
        try
        {
            if (args.Length == 0) { stderr.WriteLine(Usage); return ExitCodes.Usage; }

            string command = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();
            return command switch
            {
                "--version" or "-v" => Version(stdout),
                "help" or "--help" or "-h" => Help(rest, stdout),
                _ => throw new CliUsageException($"Unknown command '{args[0]}'.\n{Usage}")
            };
        }
        catch (CliUsageException ex) { stderr.WriteLine(ex.Message); return ExitCodes.Usage; }
        catch (CliDataException ex) { stderr.WriteLine(ex.Message); return ExitCodes.Data; }
    }

    private static int Version(TextWriter stdout)
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = version.IndexOf('+');                      // strip build metadata
        stdout.WriteLine($"certapi {(plus > 0 ? version[..plus] : version)}");
        return ExitCodes.Ok;
    }

    private static int Help(string[] rest, TextWriter stdout)
    {
        stdout.WriteLine(Usage);   // per-command help arrives with each command's task
        return ExitCodes.Ok;
    }
}
