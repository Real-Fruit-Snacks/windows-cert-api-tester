using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

// This suite is the frozen characterization baseline for the v1.57.0 connection-pooling refactor
// of ApiClient (see src/ApiTester.Core/ApiClient.cs). ApiClient currently opens a brand-new
// SocketsHttpHandler/HttpClient — and pays for a fresh TLS handshake — on every SendAsync call,
// because the handshake diagnostics are captured into closures created per call. The refactor will
// let a single ApiClient instance cache and reuse a handler whose configuration matches across
// calls. Every test below describes an observable behavior of ApiClient exactly as it stands today.
// It is green before that refactor and MUST stay green, unedited, after it: if a later slice needs
// a different assertion here, the behavior changed and that is a regression, not a test to update.
public class ApiClientCharacterizationTests
{
    // Freezes: status code, reason phrase, response headers (including an extra server header),
    // body bytes, and ContentType all come back exactly as the server sent them.
    [Fact]
    public async Task Response_relay_returns_status_reason_headers_body_and_content_type_exactly()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartWithHeadersAsync(
            serverCert, clientCert.Thumbprint!,
            new[] { new KeyValuePair<string, string>("X-Server", "characterization") },
            responseBody: "{\"frozen\":true}",
            responseContentType: "application/json");

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal(200, resp.StatusCode);
        Assert.Equal("OK", resp.ReasonPhrase);
        Assert.Contains(resp.Headers, h =>
            h.Key.Equals("X-Server", StringComparison.OrdinalIgnoreCase) && h.Value == "characterization");
        Assert.Equal("{\"frozen\":true}", Encoding.UTF8.GetString(resp.Body));
        Assert.Equal("application/json", resp.ContentType);
    }

    // Freezes: a direct (non-proxied) mTLS connection reports the full diagnostics view — protocol,
    // cipher, whether the client certificate was sent, and the server certificate's own facts.
    [Fact]
    public async Task Direct_mtls_connection_reports_full_diagnostics()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            trustServerCertificate: c => c is not null && c.Thumbprint == serverCert.Thumbprint);

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        var conn = resp.Connection;
        Assert.NotNull(conn);
        Assert.False(conn!.ViaProxy);
        Assert.Null(conn.ProxyUri);
        Assert.StartsWith("TLS", conn.TlsProtocol);
        Assert.False(string.IsNullOrEmpty(conn.CipherSuite));
        Assert.True(conn.ClientCertificateSent);
        Assert.Equal(clientCert.Subject, conn.ClientCertificateSubject);
        Assert.Equal(serverCert.Subject, conn.ServerCertificateSubject);
        Assert.Equal(serverCert.Issuer, conn.ServerCertificateIssuer);
        Assert.Equal(serverCert.Thumbprint, conn.ServerCertificateThumbprint);
        Assert.NotNull(conn.ServerCertificateNotAfter);
        Assert.NotEmpty(conn.ServerCertificateChain);
    }

    // Freezes: a null client certificate against a server that demands one fails the handshake and
    // is classified as one of the platform-dependent transport-failure kinds, with a non-empty
    // exception detail chain — never a silent or generically-classified failure.
    [Fact]
    public async Task Missing_client_certificate_fails_the_handshake_with_a_transport_error_and_detail()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCertificate: null,
            trustServerCertificate: _ => true);

        Assert.False(resp.IsSuccess);
        Assert.NotNull(resp.Error);
        Assert.True(
            resp.Error!.Kind is ApiErrorKind.CertificateRefused or ApiErrorKind.Network or ApiErrorKind.ConnectionReset,
            $"Unexpected error kind: {resp.Error.Kind} ({resp.Error.Message})");
        Assert.NotEmpty(resp.Error.Detail!);
    }

    // Freezes: every redirect in a chain is followed, and each hop's status/From/To is recorded in
    // the order the client actually traversed them, ending at the final response.
    [Fact]
    public async Task Redirect_chain_is_followed_and_every_hop_recorded_in_order()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartRedirectChainAsync(
            serverCert, clientCert.Thumbprint!, hops: 2);
        string origin = server.BaseUrl.TrimEnd('/');

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal(200, resp.StatusCode);
        // The echo-style body from the redirect-chain server: "<METHOD> <final path>|<body>".
        Assert.Equal("GET /hop2|", Encoding.UTF8.GetString(resp.Body));

        Assert.Equal(2, resp.Redirects.Count);
        Assert.Equal(new[] { 302, 302 }, resp.Redirects.Select(h => h.StatusCode));
        Assert.Equal(server.BaseUrl, resp.Redirects[0].From);
        Assert.Equal($"{origin}/hop1", resp.Redirects[0].To);
        Assert.Equal($"{origin}/hop1", resp.Redirects[1].From);
        Assert.Equal($"{origin}/hop2", resp.Redirects[1].To);
    }

    // Freezes: a cross-origin redirect drops the Authorization header before the next hop and flags
    // that hop as having done so; an https -> http redirect flags that hop as a scheme downgrade.
    [Fact]
    public async Task Redirect_hop_flags_authorization_dropped_on_cross_origin_and_scheme_downgrade_on_https_to_http()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        // A second loopback server on its own port is a genuinely different origin.
        await using var elsewhere = await LoopbackMtlsServer.StartEchoAsync(serverCert, clientCert.Thumbprint!);
        await using var crossOriginServer = await LoopbackMtlsServer.StartRedirectAsync(
            serverCert, clientCert.Thumbprint!, elsewhere.BaseUrl);

        var crossOriginResp = await new ApiClient().SendAsync(
            new ApiRequest
            {
                Method = HttpMethod.Get,
                Url = crossOriginServer.BaseUrl,
                Headers = new[] { new KeyValuePair<string, string>("Authorization", "Bearer secret-token") }
            },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.True(crossOriginResp.IsSuccess, crossOriginResp.Error?.Message);
        var crossOriginHop = Assert.Single(crossOriginResp.Redirects);
        Assert.True(crossOriginHop.AuthorizationDropped);
        string received = Encoding.UTF8.GetString(crossOriginResp.Body);
        Assert.DoesNotContain("Authorization", received, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-token", received);

        // Port 1 refuses the connection, so the hop is taken but the request never completes —
        // the downgrade still has to be reported even on a failed chain.
        await using var downgradeServer = await LoopbackMtlsServer.StartRedirectAsync(
            serverCert, clientCert.Thumbprint!, "http://127.0.0.1:1/");

        var downgradeResp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = downgradeServer.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.False(downgradeResp.IsSuccess);
        var downgradeHop = Assert.Single(downgradeResp.Redirects);
        Assert.True(downgradeHop.SchemeDowngrade);
    }

    // Freezes: exceeding MaxRedirects fails with TooManyRedirects while still reporting the hops
    // taken so far — those hops are where the client certificate went.
    [Fact]
    public async Task Exceeding_the_redirect_limit_fails_with_the_hops_taken_so_far_still_reported()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartRedirectChainAsync(
            serverCert, clientCert.Thumbprint!, hops: 5);
        string origin = server.BaseUrl.TrimEnd('/');

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true, MaxRedirects = 2 });

        Assert.False(resp.IsSuccess);
        Assert.Equal(ApiErrorKind.TooManyRedirects, resp.Error?.Kind);
        Assert.Equal(2, resp.Redirects.Count);
        Assert.Equal($"{origin}/hop1", resp.Redirects[0].To);
        Assert.Equal($"{origin}/hop2", resp.Redirects[1].To);
    }

    // Freezes: retry counts attempts against the server's own ground-truth request count, not a
    // stopwatch — a flaky server that fails twice then succeeds is seen exactly 3 times.
    [Fact]
    public async Task Retry_counts_attempts_against_the_servers_own_request_count()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: 2);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Retries = 2,
                RetryDelay = TimeSpan.Zero
            });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(3, resp.Attempts);
        Assert.Equal(3, server.RequestCount);
    }

    // Freezes: by default only idempotent methods are retried — a POST against a flaky server takes
    // exactly one attempt unless RetryUnsafeMethods opts it in, in which case it retries like any other.
    [Fact]
    public async Task Retry_is_idempotent_method_only_unless_unsafe_methods_are_opted_in()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        ApiRequest Post(string url) => new()
        {
            Method = HttpMethod.Post,
            Url = url,
            Body = "{\"charge\":1}",
            ContentType = "application/json"
        };

        await using var guarded = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: 2);
        var refused = await new ApiClient().SendAsync(
            Post(guarded.BaseUrl), clientCert,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Retries = 2,
                RetryDelay = TimeSpan.Zero,
                RetryUnsafeMethods = false
            });

        Assert.Equal(503, refused.StatusCode);
        Assert.Equal(1, refused.Attempts);
        Assert.Equal(1, guarded.RequestCount);

        await using var opted = await LoopbackMtlsServer.StartFlakyAsync(
            serverCert, clientCert.Thumbprint!, failures: 2);
        var allowed = await new ApiClient().SendAsync(
            Post(opted.BaseUrl), clientCert,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Retries = 2,
                RetryDelay = TimeSpan.Zero,
                RetryUnsafeMethods = true
            });

        Assert.Equal(200, allowed.StatusCode);
        Assert.Equal(3, allowed.Attempts);
        Assert.Equal(3, opted.RequestCount);
    }

    // Freezes: a --resolve pin dials the connection at the pinned address while the TLS server name
    // (SNI) and the Host header both keep the original hostname.
    [Fact]
    public async Task Resolve_override_dials_the_pinned_address_while_sni_and_host_keep_the_original_name()
    {
        const string hostname = "certapi-characterization-resolve.invalid";
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            hostname, ca, true, false, new[] { hostname });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartEchoAsync(serverCert, clientCert.Thumbprint!);
        int port = new Uri(server.BaseUrl).Port;

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = $"https://{hostname}:{port}/" },
            clientCert,
            transport: new TransportOptions
            {
                IgnoreServerCertificateErrors = true,
                Resolve = new[] { new ResolveOverride(hostname, port, "127.0.0.1") }
            });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        Assert.Contains($"Host: {hostname}:{port}", Encoding.UTF8.GetString(resp.Body));
        Assert.Equal(hostname, server.LastSniHost);
    }

    // Freezes: a gzip response body is decoded by default; with Decompress = false, the raw gzip
    // bytes (magic 0x1F 0x8B) are relayed exactly as the server framed them.
    [Fact]
    public async Task Gzip_response_is_decoded_by_default_and_relayed_raw_when_decompression_is_off()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var decodedServer = await LoopbackMtlsServer.StartGzipAsync(
            serverCert, clientCert.Thumbprint!, "{\"gzip\":\"ok\"}");
        var decoded = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = decodedServer.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.True(decoded.IsSuccess, decoded.Error?.Message);
        Assert.Equal("{\"gzip\":\"ok\"}", Encoding.UTF8.GetString(decoded.Body));

        await using var rawServer = await LoopbackMtlsServer.StartGzipAsync(
            serverCert, clientCert.Thumbprint!, "{\"gzip\":\"ok\"}");
        var raw = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = rawServer.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true, Decompress = false });

        Assert.True(raw.IsSuccess, raw.Error?.Message);
        Assert.True(raw.Body.Length >= 2, "expected the raw gzip stream, got an empty body");
        Assert.Equal(0x1F, raw.Body[0]);
        Assert.Equal(0x8B, raw.Body[1]);
    }

    // Freezes: connection refused maps to ConnectionRefused with the socket error and a non-empty
    // detail chain; an untrusted self-signed server with no bypass maps to ServerCertificateUntrusted
    // and succeeds once IgnoreServerCertificateErrors is turned on; a malformed URL maps to Unknown
    // with no status code.
    [Fact]
    public async Task Error_kinds_and_cause_chains_match_the_transport_failure()
    {
        var refused = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = "https://127.0.0.1:1/" },
            clientCertificate: null,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.Equal(ApiErrorKind.ConnectionRefused, refused.Error?.Kind);
        Assert.Equal(SocketError.ConnectionRefused, refused.Error?.SocketErrorCode);
        Assert.NotEmpty(refused.Error!.Detail!);

        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);

        var untrusted = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = false });
        Assert.Equal(ApiErrorKind.ServerCertificateUntrusted, untrusted.Error?.Kind);

        // The same server, bypass turned on: the handshake now succeeds.
        var bypassed = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });
        Assert.True(bypassed.IsSuccess, bypassed.Error?.Message);

        var malformed = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = "notaurl" },
            clientCertificate: null,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });
        Assert.Equal(ApiErrorKind.Unknown, malformed.Error?.Kind);
        Assert.Null(malformed.StatusCode);
    }

    // Freezes: a trustServerCertificate predicate pinned to a server's own thumbprint is what
    // decides trust — the same untrusted server succeeds when the predicate says yes and stays
    // ServerCertificateUntrusted when it says no.
    [Fact]
    public async Task Trust_predicate_pinned_to_the_servers_thumbprint_decides_whether_it_is_trusted()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!);

        var trusted = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = false },
            trustServerCertificate: c => c is not null && c.Thumbprint == serverCert.Thumbprint);
        Assert.True(trusted.IsSuccess, trusted.Error?.Message);

        var untrusted = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = false },
            trustServerCertificate: _ => false);
        Assert.Equal(ApiErrorKind.ServerCertificateUntrusted, untrusted.Error?.Kind);
    }

    // Freezes: a caller-supplied cookie jar carries a cookie between two sends on one ApiClient only
    // when the same CookieContainer is reused — two separate containers, or no container at all, see
    // no cookie on the second request.
    [Fact]
    public async Task Cookies_persist_across_sends_only_when_the_caller_shares_one_cookie_container()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        var client = new ApiClient();

        await using var sharedServer = await LoopbackMtlsServer.StartCookieEchoAsync(serverCert, clientCert.Thumbprint!);
        var jar = new CookieContainer();
        var first = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = sharedServer.BaseUrl }, clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true }, cookies: jar);
        Assert.True(first.IsSuccess, first.Error?.Message);
        var second = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = sharedServer.BaseUrl }, clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true }, cookies: jar);
        Assert.Equal("srv=ok", Encoding.UTF8.GetString(second.Body));

        await using var separateServer = await LoopbackMtlsServer.StartCookieEchoAsync(serverCert, clientCert.Thumbprint!);
        await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = separateServer.BaseUrl }, clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true }, cookies: new CookieContainer());
        var secondSeparate = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = separateServer.BaseUrl }, clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true }, cookies: new CookieContainer());
        Assert.Equal("none", Encoding.UTF8.GetString(secondSeparate.Body));

        await using var nullServer = await LoopbackMtlsServer.StartCookieEchoAsync(serverCert, clientCert.Thumbprint!);
        await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = nullServer.BaseUrl }, clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true }, cookies: null);
        var secondNull = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = nullServer.BaseUrl }, clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true }, cookies: null);
        Assert.Equal("none", Encoding.UTF8.GetString(secondNull.Body));
    }

    // Freezes: Windows Integrated Auth (NTLM) is answered automatically when WindowsAuth is set on
    // the request — without it the server's challenge stays a 401.
    [Fact]
    public async Task Windows_auth_option_answers_the_ntlm_challenge_the_mock_server_issues()
    {
        await using var srv = MockServer.Start(0, MockTlsMode.Http);

        var unauth = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = srv.BaseUrl + "windows-auth" }, null);
        Assert.Equal(401, unauth.StatusCode);

        var authed = await new ApiClient().SendAsync(
            new ApiRequest
            {
                Method = HttpMethod.Get,
                Url = srv.BaseUrl + "windows-auth",
                WindowsAuth = new WindowsAuthOptions(UseDefaultCredentials: true)
            }, null);

        Assert.Equal(200, authed.StatusCode);
        Assert.Contains("\"authenticated\":\"NTLM\"", Encoding.UTF8.GetString(authed.Body));
    }

    // Freezes: a multipart send carries a text part and a file part (read from disk at send time) —
    // the raw request shows the boundary, both part names, the file's name, and its content.
    [Fact]
    public async Task Multipart_send_carries_the_text_field_and_the_files_name_and_content()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartEchoAsync(serverCert, clientCert.Thumbprint!);

        var file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        File.WriteAllText(file, "characterization-file-content");
        try
        {
            var resp = await new ApiClient().SendAsync(
                new ApiRequest
                {
                    Method = HttpMethod.Post,
                    Url = server.BaseUrl,
                    Parts = new[]
                    {
                        new MultipartPart("field", "hello", null),
                        new MultipartPart("upload", null, file)
                    }
                },
                clientCert,
                transport: new TransportOptions { IgnoreServerCertificateErrors = true });

            Assert.True(resp.IsSuccess, resp.Error?.Message);
            string echoed = Encoding.UTF8.GetString(resp.Body);
            Assert.Contains("multipart/form-data; boundary=", echoed);
            Assert.Contains("name=field", echoed);
            Assert.Contains("name=upload", echoed);
            Assert.Contains(Path.GetFileName(file), echoed);
            Assert.Contains("characterization-file-content", echoed);
        }
        finally { File.Delete(file); }
    }

    // Freezes: a listener that accepts the socket but never completes the TLS handshake, given a
    // short-but-generous timeout, always ends as a classified transport error — never a hang and
    // never success. Which specific kind depends on the platform and CI runner load, so all three
    // plausible classifications are accepted (mirrors ApiClientTests.Slow_server_maps_to_a_transport_error).
    [Fact]
    public async Task Slow_handshake_maps_to_a_transport_error_rather_than_hanging()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = listener.AcceptTcpClientAsync(); // accept but never complete the TLS handshake
        try
        {
            var resp = await new ApiClient().SendAsync(
                new ApiRequest
                {
                    Method = HttpMethod.Get,
                    Url = $"https://127.0.0.1:{port}/",
                    Timeout = TimeSpan.FromMilliseconds(1200)   // generous: short timeouts flake under parallel test load
                },
                clientCertificate: null,
                transport: new TransportOptions { IgnoreServerCertificateErrors = true });

            var kind = resp.Error?.Kind;
            Assert.True(
                kind is ApiErrorKind.Timeout or ApiErrorKind.Network or ApiErrorKind.ConnectionReset,
                $"expected Timeout, Network or ConnectionReset, got {kind}");
        }
        finally { listener.Stop(); }
    }

    // Freezes: an already-cancelled token short-circuits the send with ApiErrorKind.None and the
    // exact message "Request cancelled." rather than throwing or being classified as any transport error.
    [Fact]
    public async Task An_already_cancelled_token_yields_a_cancelled_result_with_the_exact_message()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = "https://127.0.0.1:1/" },
            clientCertificate: null,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true },
            cancellationToken: cts.Token);

        Assert.Equal(ApiErrorKind.None, resp.Error?.Kind);
        Assert.Equal("Request cancelled.", resp.Error?.Message);
    }

    // Freezes: ValidateTransport refuses --resolve combined with a proxy, MaxRedirects < 1, and a
    // non-absolute explicit proxy URL; SendAsync itself refuses the same combination, returning the
    // identical message as an Unknown error rather than opening a connection.
    [Fact]
    public void Transport_validation_refuses_resolve_with_proxy_low_redirect_caps_and_relative_proxy_urls()
    {
        const string url = "https://api.example.com/things";

        string? zeroRedirects = ApiClient.ValidateTransport(new TransportOptions { MaxRedirects = 0 }, url);
        Assert.NotNull(zeroRedirects);
        Assert.Contains("--max-redirs", zeroRedirects);

        string? relativeProxy = ApiClient.ValidateTransport(
            new TransportOptions { Proxy = ProxyMode.Explicit, ProxyUrl = "127.0.0.1:8888" }, url);
        Assert.NotNull(relativeProxy);
        Assert.Contains("--proxy", relativeProxy);

        var resolveWithProxy = new TransportOptions
        {
            Proxy = ProxyMode.Explicit,
            ProxyUrl = "http://127.0.0.1:8888",
            Resolve = new[] { new ResolveOverride("api.example.com", 443, "10.0.0.1") }
        };
        string? problem = ApiClient.ValidateTransport(resolveWithProxy, url);
        Assert.NotNull(problem);
        Assert.Contains("--resolve", problem);

        Assert.Null(ApiClient.ValidateTransport(new TransportOptions(), url));
    }

    // Freezes: SendAsync's up-front refusal for an invalid transport combination reports the request
    // as an Unknown error carrying ValidateTransport's exact message, without a status code — and
    // without ever opening a connection.
    [Fact]
    public async Task SendAsync_refuses_an_invalid_transport_with_validates_exact_message_before_sending()
    {
        const string url = "https://api.example.com/things";
        var transport = new TransportOptions
        {
            Proxy = ProxyMode.Explicit,
            ProxyUrl = "http://127.0.0.1:8888",
            Resolve = new[] { new ResolveOverride("api.example.com", 443, "10.0.0.1") }
        };
        string? problem = ApiClient.ValidateTransport(transport, url);
        Assert.NotNull(problem);

        var resp = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = url }, clientCertificate: null, transport: transport);

        Assert.False(resp.IsSuccess);
        Assert.Null(resp.StatusCode);
        Assert.Equal(ApiErrorKind.Unknown, resp.Error?.Kind);
        Assert.Equal(problem, resp.Error?.Message);
    }
}
