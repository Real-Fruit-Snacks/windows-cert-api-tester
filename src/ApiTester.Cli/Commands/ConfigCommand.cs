using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class ConfigCommand
{
    public const string Help = """
        Usage: certapi config path
               certapi config show [--profile <name>]
               certapi config profiles

        Says what configuration is actually in effect, in the same spirit as `doctor` and `proxy`:
        a default that surprises you should be traceable to the file that set it.

          path       Which configuration file was found, and by which rule
          show       The resolved profile, exactly as a command would see it
          profiles   The profile names the file defines

        Configuration is looked for in this order, first match winning:
          1. --config <path>            named explicitly
          2. the CERTAPI_CONFIG environment variable
          3. certapi.config.json        found by walking up from the working directory
          4. the per-user file          %APPDATA%\certapi\config.json
        --no-config ignores all four, which is how a run is made reproducible regardless of what
        happens to sit in the working directory.

        A profile supplies defaults for the options a command already understands — certificate,
        proxy, revocation, retries, timeout, workspace, and standing headers. An explicitly typed
        flag always wins over the profile; the profile wins over the built-in default.

        A value may contain ${env:NAME}, read from the environment when the file is loaded, so a
        password or a token lives in the environment and the file stays safe to commit. `show`
        prints such a value as resolved-or-missing and never prints the secret itself.

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi config path
          certapi config show --profile corp
          certapi send https://api.internal/orders --profile corp

        Exit 0 when the configuration could be read, 2 usage, 3 when a named file is unreadable.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        // --config/--profile/--no-config are global: CliApp consumed them and recorded both the
        // file it found and the profile it resolved. This command reports THAT, rather than
        // re-running discovery — which could disagree with what the invocation actually used, and
        // would ignore an explicit --config into the bargain.
        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);

        var source = services.ProfileSource;

        switch (positionals[0].ToLowerInvariant())
        {
            case "path":
                if (source is null)
                {
                    stdout.WriteLine("no configuration file found.");
                    stdout.WriteLine($"  looked for {ConfigLoader.FileName} from {services.WorkingDirectory} upwards,");
                    stdout.WriteLine($"  the {ConfigLoader.EnvironmentVariable} variable, and {services.UserConfigPath ?? "(no per-user path)"}.");
                }
                else
                {
                    stdout.WriteLine(source.Path);
                    stdout.WriteLine($"  found by: {source.Rule}");
                }
                return ExitCodes.Ok;

            case "profiles":
            {
                var config = Load(source);
                if (config.Profiles.Count == 0) { stdout.WriteLine("no profiles are defined."); return ExitCodes.Ok; }
                foreach (var name in config.Profiles.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                    stdout.WriteLine(name + (string.Equals(name, config.DefaultProfile, StringComparison.OrdinalIgnoreCase) ? "   (default)" : ""));
                return ExitCodes.Ok;
            }

            case "show":
            {
                if (source is null) { stdout.WriteLine("no configuration file found."); return ExitCodes.Ok; }
                var profile = services.Profile;
                if (profile is null)
                {
                    stdout.WriteLine($"{source.Path} has no default profile — name one with --profile.");
                    return ExitCodes.Ok;
                }
                Render(profile, stdout);
                return ExitCodes.Ok;
            }

            default:
                throw new CliUsageException(Help);
        }
    }

    private static CertapiConfig Load(ConfigSource? source)
    {
        if (source is null) return CertapiConfig.Empty;
        try { return ConfigLoader.Parse(File.ReadAllText(source.Path), source); }
        catch (ConfigException ex) { throw new CliDataException(ex.Message); }
        catch (IOException ex) { throw new CliDataException($"could not read {source.Path}: {ex.Message}"); }
    }

    private static void Render(ConfigProfile p, TextWriter stdout)
    {
        void Line(string name, string? value)
        {
            if (value is not null) stdout.WriteLine($"  {name,-18} {value}");
        }

        // A secret is never printed. The loader has already resolved ${env:…} by the time a
        // profile exists, so what is shown is the field name and the fact that it is set —
        // printing the value would defeat the reason for keeping it in the environment.
        Line("cert", p.Cert);
        Line("store", p.Store);
        Line("certFile", p.CertFile);
        Line("certPassword", p.CertPassword is null ? null : "(set)");
        Line("keyFile", p.KeyFile);
        Line("proxy", p.Proxy);
        Line("proxyUser", p.ProxyUser is null ? null : "(set)");
        Line("noProxy", p.NoProxy?.ToString().ToLowerInvariant());
        Line("noProxyList", p.NoProxyList);
        Line("revocation", p.Revocation);
        Line("revocationStrict", p.RevocationStrict?.ToString().ToLowerInvariant());
        Line("retry", p.Retry?.ToString());
        Line("timeout", p.Timeout?.ToString());
        Line("insecure", p.Insecure?.ToString().ToLowerInvariant());
        Line("workspace", p.Workspace);
        foreach (var header in p.Headers)
            stdout.WriteLine($"  {"header",-18} {header.Key}: {header.Value}");
    }
}
