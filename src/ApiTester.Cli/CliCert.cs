using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Cli;

/// <summary>Resolves the client certificate for a command from either the Windows store
/// (<c>--cert</c> / <c>--store</c>) or a file (<c>--cert-file</c> / <c>--cert-password</c> /
/// <c>--key-file</c>). Consumes those options from <paramref name="args"/>; returns null when none given.</summary>
public static class CliCert
{
    /// <summary>The shared help lines documenting the certificate options.</summary>
    public const string HelpLines = """
          --cert <thumb|subject>  Client certificate from the Windows store
          --store <location>      CurrentUser (default); LocalMachine searches both stores
          --cert-file <path>      Client certificate from a file (.pfx/.p12 or .pem/.crt) instead
          --cert-password <pw>    Password for a .pfx/.p12 certificate file
          --key-file <path>       Private-key file for a PEM certificate whose key is separate
        """;

    /// <summary>Resolve the certificate. The caller passes the already-parsed <paramref name="store"/>
    /// (from <c>--store</c>) since some commands also need it; this consumes only the cert options.</summary>
    public static X509Certificate2? Resolve(Args args, string store, CliServices services, TextWriter stderr)
    {
        // The precedence rule for the whole product, written as the null-coalescing chain it is:
        // an explicitly typed flag is non-null and wins; otherwise the configuration profile
        // supplies it; otherwise the built-in default stands. There is no separate precedence
        // engine, and adding one would be the thing that made this hard to reason about.
        var profile = services.Profile;
        string? certQueryFlag = args.Value("--cert");
        string? certFileFlag = args.Value("--cert-file");

        // Naming one source on the command line is a choice of source, so the profile's OTHER
        // source must not be applied on top of it — otherwise a profile with a store certificate
        // would make `--cert-file` fail as "mutually exclusive" against a value the user never
        // typed. Only when neither is typed does the profile decide which source is used.
        string? certQuery = certQueryFlag ?? (certFileFlag is null ? profile?.Cert : null);
        string? certFile = certFileFlag ?? (certQueryFlag is null ? profile?.CertFile : null);
        string? certPassword = args.Value("--cert-password") ?? profile?.CertPassword;
        string? keyFile = args.Value("--key-file") ?? profile?.KeyFile;

        // Validate --store unconditionally (a bad value is a usage error whether or not it's used).
        bool localMachine = store.Equals("LocalMachine", StringComparison.OrdinalIgnoreCase);
        if (!localMachine && !store.Equals("CurrentUser", StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException("--store must be CurrentUser or LocalMachine.");

        // Reachable two ways now: both typed on the line, or a profile that sets both with neither
        // typed. Both are the same mistake and get the same message.
        if (certFile is not null && certQuery is not null)
            throw new CliUsageException("--cert and --cert-file are mutually exclusive.");

        if (certFile is not null)
        {
            try { return CertificateFileLoader.Load(certFile, certPassword, keyFile); }
            catch (CertificateFileException ex) { throw new CliDataException(ex.Message); }
        }

        if (certQuery is null) return null;
        return CertPicker.Resolve(services.ListCertificates(localMachine), certQuery, stderr).Certificate;
    }
}
