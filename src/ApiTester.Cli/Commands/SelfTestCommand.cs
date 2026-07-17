using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class SelfTestCommand
{
    public const string Help = """
        Usage: certapi selftest [--json]

        Stands up a local mutual-TLS server with generated certificates and proves the
        whole client-certificate path end to end — no real endpoint needed.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr)
    {
        bool json = args.Flag("--json");
        if (args.Positionals().Count > 0) throw new CliUsageException(Help);

        var result = new SelfTestRunner().RunAsync().GetAwaiter().GetResult();
        if (json)
            stdout.WriteLine(JsonSerializer.Serialize(
                new { passed = result.Passed, detail = result.Detail },
                new JsonSerializerOptions { WriteIndented = true }));
        else
            stdout.WriteLine(result.Detail);
        return result.Passed ? ExitCodes.Ok : ExitCodes.Failure;
    }
}
