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

    public Func<string, System.Security.Cryptography.X509Certificates.X509Certificate2?> FindCertificate { get; init; } =
        thumbprint => new CertificateStoreService().FindByThumbprint(thumbprint, includeLocalMachine: true);

    public Func<Uri, System.Security.Cryptography.X509Certificates.X509Certificate2?, bool, TimeSpan, ApiTester.Core.MtlsGateway> GatewayFactory
    { get; init; } = (upstream, cert, insecure, timeout) => new ApiTester.Core.MtlsGateway(upstream, cert, insecure, timeout);

    /// <summary>Diagnostic sink for --debug / --log-file; set per invocation by CliApp.</summary>
    public CliLog Log { get; set; } = CliLog.None;
}

public static class CliApp
{
    public const string Usage = """
        Usage: certapi <command> [options]

        Commands:
          send <url>        Send a one-off request (client cert from the Windows store)
          token             Fetch an OAuth 2.0 access token (and optionally save it)
          run <path>        Run saved requests from your collections (or --all)
          fuzz <base-url>   Discover endpoints from a wordlist (which ones exist?)
          sse <url>         Stream Server-Sent Events (text/event-stream)
          ws <url>          Open a WebSocket, send messages, print what arrives
          certs             List client certificates
          selftest          Prove the mTLS path end-to-end against a loopback server
          mock              Run a local test server to fire requests at (http/tls/mtls)
          import            Import a cURL command or an OpenAPI file into collections
          export            Export collections as OpenAPI, or the whole workspace
          serve <upstream>  Run a local mTLS gateway that forwards to <upstream>
          mcp               Run an MCP server so AI agents can make mTLS calls
          help [command]    Show help (for one command, or this overview)

        Global options (work on every command, anywhere on the line):
          --debug           Rich diagnostics on stderr: resolved URLs, headers (Authorization
                            masked), certificate lookup, TLS details, timings, full stack traces
          --log-file <path> Append everything (diagnostics + all stderr output) to a log file

        Examples:
          certapi certs
          certapi send https://api.example.com/health --cert "CN=My Client"
          certapi send https://api.example.com/login -X POST -d '{"user":"me"}'
              # a token in the response (access_token / id_token / …) is captured
              # automatically and reused for later requests to the same host
          certapi run smoke-suite --env Staging
          certapi selftest
          certapi send https://api.example.com/x --debug --log-file certapi.log

        Run 'certapi help <command>' for options. 'certapi --version' prints the version.
        """;

    public static int Run(string[] args, TextReader input, TextWriter stdout, TextWriter stderr,
                          Stream? bodyOut = null, CliServices? services = null)
    {
        services ??= new CliServices();
        // Commands that read stdin (mcp/fuzz/ws) or stream to stdout (sse) run through here so they
        // get the reader; everything else falls through to the reader-less overload below.
        if (args.Length > 0 && IsStreamingCommand(args[0]))
        {
            string cmd = args[0].ToLowerInvariant();
            (string[] Remaining, bool Debug, string? LogFile) g;
            try { g = GlobalOptions.Extract(args.Skip(1).ToArray()); }
            catch (CliUsageException ex) { stderr.WriteLine(ex.Message); return ExitCodes.Usage; }

            using var log = CliLog.Create(g.Debug, g.LogFile, stderr);
            services.Log = log;
            var err = log.WrapStderr(stderr);
            try
            {
                return cmd switch
                {
                    "mcp"  => Commands.McpCommand.Run(new Args(g.Remaining), input, stdout, err, services),
                    "fuzz" => Commands.FuzzCommand.Run(new Args(g.Remaining), input, stdout, err, services),
                    "ws"   => Commands.WsCommand.Run(new Args(g.Remaining), input, stdout, err, services),
                    "sse"  => Commands.SseCommand.Run(new Args(g.Remaining), stdout, err, services),
                    _      => throw new CliUsageException($"Unknown command '{args[0]}'.\n{Usage}")
                };
            }
            catch (CliUsageException ex) { err.WriteLine(ex.Message); return ExitCodes.Usage; }
            catch (CliDataException ex) { err.WriteLine(ex.Message); return ExitCodes.Data; }
            catch (Exception ex) { err.WriteLine("error: " + log.Describe(ex)); return ExitCodes.Failure; }
        }
        return Run(args, stdout, stderr, bodyOut, services);
    }

    private static bool IsStreamingCommand(string arg) =>
        arg.Equals("mcp", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("fuzz", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("sse", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr,
                          Stream? bodyOut = null, CliServices? services = null)
    {
        services ??= new CliServices();
        if (args.Length == 0) { stderr.WriteLine(Usage); return ExitCodes.Usage; }

        (string[] Remaining, bool Debug, string? LogFile) g;
        try { g = GlobalOptions.Extract(args); }
        catch (CliUsageException ex) { stderr.WriteLine(ex.Message); return ExitCodes.Usage; }

        using var log = CliLog.Create(g.Debug, g.LogFile, stderr);
        services.Log = log;
        var err = log.WrapStderr(stderr);
        try
        {
            if (g.Remaining.Length == 0) { err.WriteLine(Usage); return ExitCodes.Usage; }
            string command = g.Remaining[0].ToLowerInvariant();
            var rest = g.Remaining.Skip(1).ToArray();
            return command switch
            {
                "--version" or "-v" => Version(stdout),
                "help" or "--help" or "-h" => Help(rest, stdout),
                "certs" => Commands.CertsCommand.Run(new Args(rest), stdout, err, services),
                "send" => Commands.SendCommand.Run(new Args(rest), stdout, err, bodyOut ?? new MemoryStream(), services),
                "run" => Commands.RunCommand.Run(new Args(rest), stdout, err, services),
                "token" => Commands.TokenCommand.Run(new Args(rest), stdout, err, services),
                "fuzz" => Commands.FuzzCommand.Run(new Args(rest), TextReader.Null, stdout, err, services),
                "selftest" => Commands.SelfTestCommand.Run(new Args(rest), stdout, err),
                "mock" => Commands.MockCommand.Run(new Args(rest), stdout, err, services),
                "import" => Commands.ImportCommand.Run(new Args(rest), stdout, err, services),
                "export" => Commands.ExportCommand.Run(new Args(rest), stdout, err, services),
                "serve" => Commands.ServeCommand.Run(new Args(rest), stdout, err, services),
                _ => throw new CliUsageException($"Unknown command '{g.Remaining[0]}'.\n{Usage}")
            };
        }
        catch (CliUsageException ex) { err.WriteLine(ex.Message); return ExitCodes.Usage; }
        catch (CliDataException ex) { err.WriteLine(ex.Message); return ExitCodes.Data; }
        catch (Exception ex) { err.WriteLine("error: " + log.Describe(ex)); return ExitCodes.Failure; }
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
        stdout.WriteLine(rest.FirstOrDefault()?.ToLowerInvariant() switch
        {
            "send" => Commands.SendCommand.Help,
            "certs" => Commands.CertsCommand.Help,
            "run" => Commands.RunCommand.Help,
            "token" => Commands.TokenCommand.Help,
            "fuzz" => Commands.FuzzCommand.Help,
            "sse" => Commands.SseCommand.Help,
            "ws" => Commands.WsCommand.Help,
            "selftest" => Commands.SelfTestCommand.Help,
            "mock" => Commands.MockCommand.Help,
            "import" => Commands.ImportCommand.Help,
            "export" => Commands.ExportCommand.Help,
            "serve" => Commands.ServeCommand.Help,
            "mcp" => Commands.McpCommand.Help,
            _ => Usage
        });
        return ExitCodes.Ok;
    }
}
