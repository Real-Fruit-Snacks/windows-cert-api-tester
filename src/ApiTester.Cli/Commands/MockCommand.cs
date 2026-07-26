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
          /windows-auth     a 401 NTLM challenge, then success (try it with certapi send --windows-auth)
          /cookie-auth      sets a session cookie, then reports authenticated once you send it back
          (Upgrade)         a WebSocket echo on any path (try it with certapi ws)

        Options:
          --port <n>        Port to listen on (default 8770; 0 picks a free port)
          --http            Plain HTTP (default) — hit it with anything, no certificates
          --tls             HTTPS with a generated self-signed server certificate
          --mtls            HTTPS that also requires a client certificate (any cert is accepted)
          --cert-dir <dir>  Where to write generated certificates (default ./certapi-mock-certs)
          -q, --quiet       Don't log each request
          --har <file>      Replay a captured HTTP Archive (HAR) instead of the built-in routes:
                            each request is answered with the recorded response for that method
                            and path (query included when it disambiguates), in recorded order
          --no-match-status <code>
                            Status for a request that matches nothing in the archive (default 404)

        With --tls / --mtls the server certificate (and, for --mtls, a ready-to-use client .pfx) are
        written to the cert dir so you can trust/present them. Runs until Ctrl+C.

        With --har, replay mode turns a captured session into an offline fake backend: point your
        app, a test suite, or a teammate's client at the mock and it answers with the recorded
        responses instead of live traffic. The built-in echo routes are not served while replaying.

        Examples:
          certapi mock
          curl http://127.0.0.1:8770/anything

          certapi mock --mtls --port 9443
          certapi send https://localhost:9443/orders --cert-file .\certapi-mock-certs\mock-client.pfx --insecure

          certapi mock --har session.har --port 8770
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

        string? harFile = args.Value("--har");
        string? noMatchRaw = args.Value("--no-match-status");
        if (noMatchRaw is not null && harFile is null)
            throw new CliUsageException("--no-match-status only applies together with --har.");
        int noMatchStatus = 404;
        if (noMatchRaw is not null && (!int.TryParse(noMatchRaw, out noMatchStatus) || noMatchStatus is < 100 or > 599))
            throw new CliUsageException($"--no-match-status expects an HTTP status 100-599, got '{noMatchRaw}'.");

        if (args.Positionals().Count > 0) throw new CliUsageException(Help);

        HarReplaySource? replay = null;
        if (harFile is not null)
        {
            if (!File.Exists(harFile)) throw new CliDataException($"No such file: {harFile}");
            Har har;
            try
            {
                har = HarReader.Parse(File.ReadAllText(harFile));
            }
            catch (HarFormatException ex)
            {
                throw new CliDataException(ex.Message);
            }
            replay = new HarReplaySource(har, new HarReplayOptions { NoMatchStatus = noMatchStatus });
        }

        X509Certificate2? serverCert = null;
        if (mode != MockTlsMode.Http)
            serverCert = MockCertificates.Generate(mode, certDir).ServerCertificate;

        Action<MockRequestLog>? onRequest = quiet ? null : Log;
        MockServer server;
        try
        {
            server = MockServer.Start(port, mode, serverCert, onRequest, replay);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            stderr.WriteLine($"error: could not listen on port {port} ({ex.Message}). Try a different --port.");
            serverCert?.Dispose();
            return ExitCodes.Failure;
        }

        stderr.WriteLine($"certapi mock listening on {server.BaseUrl}  ({mode})");
        if (replay is not null)
        {
            stderr.WriteLine($"replaying {replay.Count} recorded responses from {harFile}");
            stderr.WriteLine($"built-in routes are not served in replay mode; a request matching nothing answers {noMatchStatus}");
        }
        else
        {
            stderr.WriteLine("routes: /  /status/<code>  /sse  /token  /windows-auth  /cookie-auth  (WebSocket on any path)");
        }
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
            string matched = r.Replay switch { "exact" => "  exact-match", "path" => "  path-match", "miss" => "  miss", _ => "" };
            lock (stderr) stderr.WriteLine($"  {DateTime.Now:HH:mm:ss}  {r.Method,-6} {r.Path} → {r.Status}{who}{matched}");
        }
    }
}
