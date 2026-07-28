using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class DoctorCommand
{
    public const string Help = """
        Usage: certapi doctor <url> [options]

        Diagnoses a connection one stage at a time — URL, DNS, proxy decision, TCP, the proxy
        tunnel, the TLS handshake, and finally an HTTP GET — and reports the stage that broke
        rather than a single error line. Every stage is timed.

        What it can tell you that a normal request cannot:
          * the certificate authorities the server accepts client certificates from, matched
            against the certificates you actually have ("none of yours are issued by those")
          * whether this network is decrypting TLS in the middle, which is why a client
            certificate cannot reach the server through it
          * which proxy the machine picks for this URL, including one chosen by a PAC script,
            and what the proxy said if it refused
          * whether the internet is reachable at all, or a captive portal is in the way

        Options:
          --cert <thumb|subject>  Client certificate from the Windows store
          --store <location>      CurrentUser (default); LocalMachine searches both stores
          --cert-file <path>      Client certificate from a file (.pfx/.p12 or .pem/.crt) instead
          --cert-password <pw>    Password for a .pfx/.p12 certificate file
          --key-file <path>       Private-key file for a PEM certificate whose key is separate
          --json                  Print the whole report as JSON instead of text
          -q, --quiet             Only print stages that failed or carry advice

        """ + "\n" + TransportFlags.StreamHelp + "\n" + """

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi doctor https://api.example.com/health
          certapi doctor https://api.example.com --cert "CN=My Client"
          certapi doctor https://api.example.com --proxy http://proxy.corp:8080 --json

        Exit 0 when every stage passed, 1 when one failed, 2 usage, 3 data errors.
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string store = args.Value("--store") ?? "CurrentUser";
        bool json = args.Flag("--json");
        bool quiet = args.Flag("-q", "--quiet");
        bool insecure = args.Flag("--insecure");
        var transport = TransportFlags.ParseStreamSubset(args, insecure);
        var cert = CliCert.Resolve(args, store, services, stderr);

        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);
        string url = positionals[0];

        bool includeLocalMachine = store.Equals("LocalMachine", StringComparison.OrdinalIgnoreCase);
        // The whole store, not just the chosen certificate: the point is to answer "do I have
        // anything the server would accept?", which needs the candidates it did not pick.
        var storeCerts = new List<System.Security.Cryptography.X509Certificates.X509Certificate2>();
        try
        {
            foreach (var info in services.ListCertificates(includeLocalMachine))
                if (services.FindCertificate(info.Thumbprint) is { } found) storeCerts.Add(found);
        }
        catch (Exception ex) { services.Log.Debug("could not enumerate the certificate store: " + ex.Message); }

        var report = ConnectionDoctor
            .RunAsync(url, cert, storeCerts, transport, probe: null, services.Cancel)
            .GetAwaiter().GetResult();

        if (json) stdout.WriteLine(ToJson(report));
        else Render(report, stdout, quiet);

        return report.Ok ? ExitCodes.Ok : ExitCodes.Failure;
    }

    private static void Render(DoctorReport report, TextWriter stdout, bool quiet)
    {
        stdout.WriteLine($"certapi doctor · {report.Url}");
        stdout.WriteLine();
        foreach (var stage in report.Stages)
        {
            bool interesting = !stage.Ok || stage.Advice is not null;
            if (quiet && !interesting) continue;

            string mark = stage.Ok ? "ok  " : "FAIL";
            stdout.WriteLine($"  [{mark}] {stage.Name,-8} {stage.Summary}  ({stage.Elapsed.TotalMilliseconds:F0} ms)");
            if (!quiet)
                foreach (var line in stage.Detail) stdout.WriteLine($"           {line}");
            if (stage.Advice is not null) stdout.WriteLine($"           → {stage.Advice}");
        }
        stdout.WriteLine();
        stdout.WriteLine(report.Ok
            ? $"All stages passed in {report.Stages.Sum(s => s.Elapsed.TotalMilliseconds):F0} ms."
            : $"Stopped at '{report.FirstFailure!.Name}': {report.FirstFailure.Summary}");
    }

    private static string ToJson(DoctorReport report) =>
        JsonSerializer.Serialize(new
        {
            url = report.Url,
            ok = report.Ok,
            failedStage = report.FirstFailure?.Name,
            stages = report.Stages.Select(s => new
            {
                name = s.Name,
                ok = s.Ok,
                summary = s.Summary,
                detail = s.Detail,
                advice = s.Advice,
                elapsedMs = (int)s.Elapsed.TotalMilliseconds
            })
        });
}
