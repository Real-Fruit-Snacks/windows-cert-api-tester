using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

/// <summary>Manages per-site trusted (pinned) server certificates: <c>send</c>/<c>run</c> already
/// consult <see cref="TrustService"/> automatically (a pinned host lets a request through without
/// the blanket <c>--insecure</c> bypass) — this command is how the pins themselves get listed,
/// added, and removed.</summary>
public static class TrustCommand
{
    public const string Help = """
        Usage: certapi trust list   [--workspace <file>] [--json]
               certapi trust add    <host> (--thumbprint <t> | --from-url <https-url>) [--workspace <file>]
               certapi trust remove <host> [--thumbprint <t>] [--workspace <file>]

        Lists, adds, or removes pinned server-certificate thumbprints for a host. A pinned host
        lets certapi send/run reach it without --insecure. Reads/writes the live GUI state by
        default, or a --workspace file.

        add:
          --thumbprint <t>         Pin this exact thumbprint for <host>
          --from-url <https-url>  Connect once, capture the presented server certificate, and
                                   pin its thumbprint (needs exactly one of these two options)

        remove:
          --thumbprint <t>        Remove only this pin; omit to remove every pin for <host>

        The certificate options below only matter for --from-url, in case <https-url> itself
        requires a client certificate (mTLS):
        """ + "\n" + CliCert.HelpLines + """

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          certapi trust list
          certapi trust list --json
          certapi trust add internal.corp --thumbprint 0123456789ABCDEF0123456789ABCDEF01234567
          certapi trust add internal.corp --from-url https://internal.corp
          certapi trust remove internal.corp
          certapi trust remove internal.corp --thumbprint 0123456789ABCDEF0123456789ABCDEF01234567
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string? workspace = args.Value("--workspace");
        bool json = args.Flag("--json");
        string? thumbprint = args.Value("--thumbprint");
        string? fromUrl = args.Value("--from-url");
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        // Only --from-url ever needs a client certificate, but options are consumed here (before
        // Positionals()) for every subcommand, the same way CertsCommand/SendCommand always do.
        var cert = CliCert.Resolve(args, store, services, stderr);

        var positionals = args.Positionals();
        if (positionals.Count == 0) throw new CliUsageException(Help);
        string sub = positionals[0].ToLowerInvariant();

        var state = CliWorkspace.Load(workspace, services.LiveStatePath, stderr);
        string targetPath = workspace ?? services.LiveStatePath;

        return sub switch
        {
            "list" when positionals.Count == 1 => List(state, json, stdout, stderr),
            "add" when positionals.Count == 2 =>
                Add(state, positionals[1], thumbprint, fromUrl, cert, workspace, targetPath, services, stderr),
            "remove" when positionals.Count == 2 =>
                Remove(state, positionals[1], thumbprint, workspace, targetPath, services, stderr),
            _ => throw new CliUsageException(Help)
        };
    }

    private static int List(AppState state, bool json, TextWriter stdout, TextWriter stderr)
    {
        if (state.TrustedServerCerts.Count == 0) stderr.WriteLine("No trusted server certificates.");

        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(state.TrustedServerCerts.Select(t => new
            {
                host = t.Host,
                thumbprint = t.Thumbprint,
                subject = t.Subject,
                addedUtc = t.AddedUtc
            }), new JsonSerializerOptions { WriteIndented = true }));
            return ExitCodes.Ok;
        }

        foreach (var t in state.TrustedServerCerts)
            stdout.WriteLine($"{t.Host}\n  {t.Thumbprint}  ·  {t.Subject ?? "—"}  ·  added {t.AddedUtc:yyyy-MM-dd}");
        return ExitCodes.Ok;
    }

    private static int Add(
        AppState state, string host, string? thumbprint, string? fromUrl, X509Certificate2? cert,
        string? workspace, string targetPath, CliServices services, TextWriter stderr)
    {
        if (thumbprint is null && fromUrl is null)
            throw new CliUsageException("trust add needs --thumbprint <t> or --from-url <https-url>.");

        if (workspace is null && services.IsGuiRunning())
            throw new CliDataException(
                "The GUI is running and would overwrite this change when it closes — close the app, or write into a --workspace file.");

        string finalThumbprint;
        string? subject = null;
        if (thumbprint is not null)
        {
            finalThumbprint = thumbprint;
        }
        else
        {
            // --from-url: connect once purely to observe the presented server certificate. The
            // blanket IgnoreServerCertificateErrors is deliberate here — it only forces the
            // handshake to complete so the cert is observable, and is never itself stored as trust.
            var request = new ApiRequest { Method = HttpMethod.Get, Url = fromUrl! };
            var transport = new TransportOptions { IgnoreServerCertificateErrors = true };
            var response = services.Client.SendAsync(
                request, cert, transport: transport, cancellationToken: services.Cancel).GetAwaiter().GetResult();

            if (response.Connection?.ServerCertificateThumbprint is not { } thumb)
            {
                stderr.WriteLine(response.Error?.Message ?? $"Could not connect to {fromUrl}.");
                foreach (var d in response.Error?.Detail ?? Array.Empty<ErrorDetail>())
                    stderr.WriteLine($"  caused by: {d.ExceptionType}: {d.Message}"
                        + (d.NativeErrorCode is { } native ? $" (native error {native})" : ""));
                return ExitCodes.Failure;
            }

            finalThumbprint = thumb;
            subject = response.Connection.ServerCertificateSubject;
            stderr.WriteLine($"Captured server certificate for {host}: {finalThumbprint}  ·  {subject ?? "—"}");
        }

        TrustService.Trust(state, host, finalThumbprint, subject);
        CliWorkspace.ReportSaveResult(state.SaveTo(targetPath), targetPath, stderr);
        stderr.WriteLine($"Trusted {finalThumbprint} for {host}.");
        return ExitCodes.Ok;
    }

    private static int Remove(
        AppState state, string host, string? thumbprint, string? workspace, string targetPath,
        CliServices services, TextWriter stderr)
    {
        if (workspace is null && services.IsGuiRunning())
            throw new CliDataException(
                "The GUI is running and would overwrite this change when it closes — close the app, or write into a --workspace file.");

        int before = state.TrustedServerCerts.Count;
        TrustService.Untrust(state, host, thumbprint);
        int removed = before - state.TrustedServerCerts.Count;
        CliWorkspace.ReportSaveResult(state.SaveTo(targetPath), targetPath, stderr);
        stderr.WriteLine($"Removed {removed} pin(s) for {host}.");
        return ExitCodes.Ok;
    }
}
