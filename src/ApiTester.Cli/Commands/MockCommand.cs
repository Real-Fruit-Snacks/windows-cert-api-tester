using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class MockCommand
{
    public const string Help = """
        Usage: certapi mock [options]

        Run a local test server you can fire requests at — the standing counterpart to selftest.
        It echoes each request back as JSON (method, path, query, headers, body, and, under mTLS,
        the client certificate you presented) and serves a few fixed routes:

          /                 echoes the request (any method, any path)
          /status/<code>    responds with that HTTP status (e.g. /status/404)
          /sse              a short text/event-stream (try it with certapi sse)
          /token            an OAuth 2.0 token response (try it with certapi token)
          (Upgrade)         a WebSocket echo on any path (try it with certapi ws)

        Options:
          --port <n>        Port to listen on (default 8770; 0 picks a free port)
          --http            Plain HTTP (default) — hit it with anything, no certificates
          --tls             HTTPS with a generated self-signed server certificate
          --mtls            HTTPS that also requires a client certificate (any cert is accepted)
          --cert-dir <dir>  Where to write generated certificates (default ./certapi-mock-certs)
          -q, --quiet       Don't log each request

        With --tls / --mtls the server certificate (and, for --mtls, a ready-to-use client .pfx) are
        written to the cert dir so you can trust/present them. Runs until Ctrl+C.

        Examples:
          certapi mock
          curl http://127.0.0.1:8770/anything

          certapi mock --mtls --port 9443
          certapi send https://localhost:9443/orders --cert-file .\certapi-mock-certs\mock-client.pfx --insecure
        """;

    public static int Run(Args args, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string? portRaw = args.Value("--port");
        int port = 8770;
        if (portRaw is not null && (!int.TryParse(portRaw, out port) || port is < 0 or > 65535))
            throw new CliUsageException($"--port expects 0-65535, got '{portRaw}'.");

        bool tls = args.Flag("--tls");
        bool mtls = args.Flag("--mtls");
        if (tls && mtls) throw new CliUsageException("--tls and --mtls are mutually exclusive.");
        var mode = mtls ? MockTlsMode.Mtls : tls ? MockTlsMode.Https : MockTlsMode.Http;
        string certDir = args.Value("--cert-dir") ?? Path.Combine(Directory.GetCurrentDirectory(), "certapi-mock-certs");
        bool quiet = args.Flag("-q", "--quiet");
        if (args.Positionals().Count > 0) throw new CliUsageException(Help);

        X509Certificate2? serverCert = null;
        if (mode != MockTlsMode.Http)
            serverCert = GenerateCertificates(mode, certDir, stderr);

        Action<MockRequestLog>? onRequest = quiet ? null : Log;
        MockServer server;
        try
        {
            server = MockServer.Start(port, mode, serverCert, onRequest);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            stderr.WriteLine($"error: could not listen on port {port} ({ex.Message}). Try a different --port.");
            serverCert?.Dispose();
            return ExitCodes.Failure;
        }

        stderr.WriteLine($"certapi mock listening on {server.BaseUrl}  ({mode})");
        stderr.WriteLine("routes: /  /status/<code>  /sse  /token  (WebSocket on any path)");
        if (mode != MockTlsMode.Http)
        {
            stderr.WriteLine($"certificates in {certDir}");
            stderr.WriteLine(mode == MockTlsMode.Mtls
                ? "present mock-client.pfx as your client cert; use --insecure (or trust mock-ca.cer) for the server cert."
                : "use --insecure (or trust mock-ca.cer) for the self-signed server certificate.");
        }
        stderr.WriteLine("press Ctrl+C to stop.");

        services.Cancel.WaitHandle.WaitOne();   // block until Program.cs cancels on Ctrl+C

        stderr.WriteLine("stopping…");
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        serverCert?.Dispose();
        return ExitCodes.Ok;

        void Log(MockRequestLog r)
        {
            string who = r.ClientCertSubject is { } s ? $"  ({s})" : "";
            lock (stderr) stderr.WriteLine($"  {DateTime.Now:HH:mm:ss}  {r.Method,-6} {r.Path} → {r.Status}{who}");
        }
    }

    /// <summary>Generate a CA + server certificate (and, for mTLS, a client certificate), write the
    /// public certs and a client .pfx to <paramref name="certDir"/>, and return the server cert.</summary>
    private static X509Certificate2 GenerateCertificates(MockTlsMode mode, string certDir, TextWriter stderr)
    {
        Directory.CreateDirectory(certDir);
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("certapi mock CA");
        var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, serverAuth: true, clientAuth: false, dnsNames: new[] { "localhost" });

        File.WriteAllBytes(Path.Combine(certDir, "mock-ca.cer"), ca.Export(X509ContentType.Cert));
        File.WriteAllBytes(Path.Combine(certDir, "mock-server.cer"), serverCert.Export(X509ContentType.Cert));

        if (mode == MockTlsMode.Mtls)
        {
            using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate(
                "certapi mock client", ca, serverAuth: false, clientAuth: true);
            File.WriteAllBytes(Path.Combine(certDir, "mock-client.pfx"), clientCert.Export(X509ContentType.Pfx));
        }

        ca.Dispose();
        return serverCert;
    }
}
