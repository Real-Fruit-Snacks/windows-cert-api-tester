using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class ProxyCommand
{
    public const string Help = """
        Usage: certapi proxy [<url>] [--json]

        Shows how this machine is configured to reach the internet, and — with a URL — which
        proxy it would actually use for that address.

        Proxy auto-config (PAC) is a JavaScript program, so the only honest answer to "which
        proxy will I get" is the one Windows itself computes. This asks WinHTTP's own engine
        (WinHttpGetProxyForUrl) with your configured script or WPAD discovery, then asks .NET
        the same question and prints both. They normally agree; when they do not, that
        disagreement is the finding — and it explains a request that works in a browser but
        not here, or the other way round.

        With no URL it prints the configuration only: automatic detection (WPAD), the
        configuration-script address, the static proxy and its bypass list.

        Options:
          --json                  Print the report as JSON instead of text

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi proxy
          certapi proxy https://api.example.com/orders
          certapi proxy https://api.example.com/orders --json

        Exit 0 when the settings could be read (with or without a proxy configured), 2 usage.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        bool json = args.Flag("--json");
        var positionals = args.Positionals();
        if (positionals.Count > 1) throw new CliUsageException(Help);
        string? url = positionals.Count == 1 ? positionals[0] : null;

        if (url is not null &&
            (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
             (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)))
            throw new CliUsageException($"'{url}' is not an absolute http(s) URL.");

        var settings = ProxyIntrospection.ReadSettings();
        var decision = url is null ? null : ProxyIntrospection.Decide(url, settings);

        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                settings = new
                {
                    autoDetect = settings.AutoDetect,
                    autoConfigUrl = settings.AutoConfigUrl,
                    proxyEnabled = settings.ProxyEnabled,
                    proxyServer = settings.ProxyServer,
                    proxyOverride = settings.ProxyOverride
                },
                decision = decision is null ? null : new
                {
                    url = decision.Url,
                    winHttp = decision.WinHttpProxy ?? "DIRECT",
                    winHttpError = decision.WinHttpError,
                    dotNet = decision.DotNetProxy ?? "DIRECT",
                    disagrees = decision.Disagrees
                }
            }));
            return ExitCodes.Ok;
        }

        Render(settings, decision, stdout);
        return ExitCodes.Ok;
    }

    private static void Render(ProxySettings settings, ProxyDecision? decision, TextWriter stdout)
    {
        stdout.WriteLine("Internet Options (this user):");
        if (settings.IsEmpty)
            stdout.WriteLine("  no proxy configured — everything goes direct");
        else
        {
            stdout.WriteLine($"  automatically detect settings (WPAD): {(settings.AutoDetect ? "on" : "off")}");
            stdout.WriteLine($"  configuration script: {settings.AutoConfigUrl ?? "(none)"}");
            stdout.WriteLine($"  static proxy: {(settings.ProxyEnabled ? settings.ProxyServer ?? "(enabled, none set)" : "off")}");
            if (settings.ProxyOverride is { Length: > 0 })
                stdout.WriteLine($"  bypass list: {settings.ProxyOverride}");
        }

        if (decision is null)
        {
            stdout.WriteLine();
            stdout.WriteLine("Pass a URL to see which proxy applies to it: certapi proxy https://api.example.com");
            return;
        }

        stdout.WriteLine();
        stdout.WriteLine($"For {decision.Url}:");
        stdout.WriteLine(decision.WinHttpError is { } error
            ? $"  Windows (WinHTTP/PAC): could not decide — {error}"
            : $"  Windows (WinHTTP/PAC): {decision.WinHttpProxy ?? "DIRECT"}");
        stdout.WriteLine($"  .NET (what certapi will use): {decision.DotNetProxy ?? "DIRECT"}");

        if (decision.Disagrees)
        {
            stdout.WriteLine();
            stdout.WriteLine("  ! The two engines disagree. certapi follows the .NET answer, so a browser " +
                             "and certapi may be taking different routes to this host.");
        }
    }
}
