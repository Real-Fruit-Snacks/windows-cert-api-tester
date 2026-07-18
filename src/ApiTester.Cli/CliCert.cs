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
        string? certQuery = args.Value("--cert");
        string? certFile = args.Value("--cert-file");
        string? certPassword = args.Value("--cert-password");
        string? keyFile = args.Value("--key-file");

        // Validate --store unconditionally (a bad value is a usage error whether or not it's used).
        bool localMachine = store.Equals("LocalMachine", StringComparison.OrdinalIgnoreCase);
        if (!localMachine && !store.Equals("CurrentUser", StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException("--store must be CurrentUser or LocalMachine.");

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
