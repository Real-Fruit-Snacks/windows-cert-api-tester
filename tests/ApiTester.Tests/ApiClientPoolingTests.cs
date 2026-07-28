using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

// Proves the v1.57.0 pooling behavior of ApiClient: equivalent direct-connection requests share a
// handler -- and so a pooled, already-handshaked connection -- while a wrong sharing key (a
// different client certificate, a different trust policy, different Windows-auth credentials,
// different decompression) never lets them. Every assertion here is either what the server itself
// observed (LoopbackMtlsServer.StartKeepAliveAsync's counters) or what a caller reads back through
// the public ApiClient surface -- never internal cache state, and never a wall clock.
public class ApiClientPoolingTests
{
    [Fact]
    public async Task Two_sends_to_one_origin_share_one_connection()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert);
        using var client = new ApiClient();
        var transport = new TransportOptions { IgnoreServerCertificateErrors = true };

        var first = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert, transport: transport);
        var second = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert, transport: transport);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(1, server.ConnectionCount);
        Assert.Equal(2, server.TotalRequestCount);
        Assert.Contains("conn=1 req=1", Encoding.UTF8.GetString(first.Body));
        Assert.Contains("conn=1 req=2", Encoding.UTF8.GetString(second.Body));
    }

    [Fact]
    public async Task A_pooled_second_request_reports_the_handshake_that_established_the_connection()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert);
        using var client = new ApiClient();
        var transport = new TransportOptions { IgnoreServerCertificateErrors = true };

        var first = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert, transport: transport);
        var second = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert, transport: transport);

        // Not a fresh handshake, but still the truth about the connection this response came back
        // over -- proven in the same test as ConnectionCount == 1 so it cannot pass by accident on a
        // second, undetected connection.
        Assert.Equal(1, server.ConnectionCount);

        var firstConn = first.Connection!;
        var secondConn = second.Connection!;
        Assert.False(string.IsNullOrEmpty(firstConn.TlsProtocol));
        Assert.False(string.IsNullOrEmpty(firstConn.CipherSuite));
        Assert.True(firstConn.ClientCertificateSent);
        Assert.Equal(firstConn.TlsProtocol, secondConn.TlsProtocol);
        Assert.Equal(firstConn.CipherSuite, secondConn.CipherSuite);
        Assert.Equal(firstConn.ClientCertificateSent, secondConn.ClientCertificateSent);
        Assert.Equal(serverCert.Thumbprint, secondConn.ServerCertificateThumbprint);
        Assert.NotEmpty(secondConn.ServerCertificateChain);
    }

    [Fact]
    public async Task A_client_certificate_is_still_presented_on_a_pooled_connection()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert);
        using var client = new ApiClient();
        var transport = new TransportOptions { IgnoreServerCertificateErrors = true };

        await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert, transport: transport);
        var second = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert, transport: transport);

        // Proven server-side: the certificate the handshake actually presented, not anything the
        // client claims about itself.
        Assert.Equal(1, server.ConnectionCount);
        Assert.Equal(2, server.TotalRequestCount);
        Assert.Equal(new[] { clientCert.Thumbprint }, server.ClientCertificateThumbprintsByConnection);
        Assert.Contains($"cert={clientCert.Thumbprint}", Encoding.UTF8.GetString(second.Body));
    }

    [Fact]
    public async Task Two_different_client_certificates_never_share_a_connection()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCertA = SelfSignedCertificateFactory.CreateSignedCertificate("ClientA", ca, false, true);
        using var clientCertB = SelfSignedCertificateFactory.CreateSignedCertificate("ClientB", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert);
        using var client = new ApiClient();
        var transport = new TransportOptions { IgnoreServerCertificateErrors = true };

        var respA = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCertA, transport: transport);
        var respB = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCertB, transport: transport);

        Assert.True(respA.IsSuccess, respA.Error?.Message);
        Assert.True(respB.IsSuccess, respB.Error?.Message);
        Assert.Equal(2, server.ConnectionCount);
        Assert.Equal(
            new HashSet<string?> { clientCertA.Thumbprint, clientCertB.Thumbprint },
            server.ClientCertificateThumbprintsByConnection.ToHashSet());
    }

    [Fact]
    public async Task A_different_trust_policy_never_shares_a_connection()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        // (a) Ignoring server-certificate errors and an equivalent trust delegate are two different
        // means to the same end -- and so, deliberately, two different keys.
        await using (var server = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert))
        {
            using var client = new ApiClient();
            var ignoring = await client.SendAsync(
                new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert,
                transport: new TransportOptions { IgnoreServerCertificateErrors = true });
            var trusting = await client.SendAsync(
                new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert,
                transport: new TransportOptions { IgnoreServerCertificateErrors = false },
                trustServerCertificate: c => c is not null && c.Thumbprint == serverCert.Thumbprint);

            Assert.True(ignoring.IsSuccess, ignoring.Error?.Message);
            Assert.True(trusting.IsSuccess, trusting.Error?.Message);
            Assert.Equal(2, server.ConnectionCount);
        }

        // (b) Two separately-written trust delegates -- even textually identical -- never share:
        // there is no sound way to fingerprint a delegate's behavior, so identity is the only test,
        // and two distinct lambda expressions are never identical even if their bodies match.
        await using (var server = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert))
        {
            using var client = new ApiClient();
            Func<X509Certificate2?, bool> trust1 = c => c is not null && c.Thumbprint == serverCert.Thumbprint;
            Func<X509Certificate2?, bool> trust2 = c => c is not null && c.Thumbprint == serverCert.Thumbprint;

            var resp1 = await client.SendAsync(
                new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert,
                transport: new TransportOptions(), trustServerCertificate: trust1);
            var resp2 = await client.SendAsync(
                new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert,
                transport: new TransportOptions(), trustServerCertificate: trust2);

            Assert.True(resp1.IsSuccess, resp1.Error?.Message);
            Assert.True(resp2.IsSuccess, resp2.Error?.Message);
            Assert.Equal(2, server.ConnectionCount);
        }

        // (c) The same delegate instance, reused: this is what does share, proving the conservatism
        // above is deliberate rather than "never share anything".
        await using (var server = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert))
        {
            using var client = new ApiClient();
            Func<X509Certificate2?, bool> trust = c => c is not null && c.Thumbprint == serverCert.Thumbprint;

            var resp1 = await client.SendAsync(
                new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert,
                transport: new TransportOptions(), trustServerCertificate: trust);
            var resp2 = await client.SendAsync(
                new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert,
                transport: new TransportOptions(), trustServerCertificate: trust);

            Assert.True(resp1.IsSuccess, resp1.Error?.Message);
            Assert.True(resp2.IsSuccess, resp2.Error?.Message);
            Assert.Equal(1, server.ConnectionCount);
        }
    }

    [Fact]
    public async Task Windows_auth_credentials_are_part_of_the_sharing_key()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert);
        using var client = new ApiClient();
        var transport = new TransportOptions { IgnoreServerCertificateErrors = true };

        var withoutAuth = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert, transport: transport);
        var withAuth = await client.SendAsync(
            new ApiRequest
            {
                Method = HttpMethod.Get,
                Url = server.BaseUrl,
                WindowsAuth = new WindowsAuthOptions(false, "DOM", "user", "pw")
            }, clientCert, transport: transport);

        Assert.True(withoutAuth.IsSuccess, withoutAuth.Error?.Message);
        Assert.True(withAuth.IsSuccess, withAuth.Error?.Message);
        Assert.Equal(2, server.ConnectionCount);
    }

    [Fact]
    public async Task Decompression_setting_is_part_of_the_sharing_key()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert);
        using var client = new ApiClient();

        var decompressed = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true, Decompress = true });
        var raw = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true, Decompress = false });

        Assert.True(decompressed.IsSuccess, decompressed.Error?.Message);
        Assert.True(raw.IsSuccess, raw.Error?.Message);
        Assert.Equal(2, server.ConnectionCount);
    }

    [Fact]
    public async Task Cookie_jars_do_not_leak_between_callers_sharing_a_connection()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert);
        using var client = new ApiClient();
        var transport = new TransportOptions { IgnoreServerCertificateErrors = true };

        var jarA = new CookieContainer();
        var first = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert,
            transport: transport, cookies: jarA);
        Assert.Contains("cookie=none", Encoding.UTF8.GetString(first.Body));

        var jarB = new CookieContainer();
        var second = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert,
            transport: transport, cookies: jarB);
        Assert.Contains("cookie=none", Encoding.UTF8.GetString(second.Body));

        var third = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert,
            transport: transport, cookies: jarA);
        Assert.Contains("cookie=srv=ok", Encoding.UTF8.GetString(third.Body));

        var fourth = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, clientCert,
            transport: transport, cookies: null);
        Assert.Contains("cookie=none", Encoding.UTF8.GetString(fourth.Body));

        // Isolation proven *while* the connection was genuinely shared -- this is the point: cookie
        // jars stay separate per caller even though every one of these four sends rode the same
        // pooled connection.
        Assert.Equal(1, server.ConnectionCount);
        Assert.Equal(4, server.TotalRequestCount);
    }

    [Fact]
    public async Task A_disposed_client_refuses_to_send()
    {
        var client = new ApiClient();
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            client.SendAsync(new ApiRequest { Method = HttpMethod.Get, Url = "https://127.0.0.1:1/" }, null));
    }

    // This test is known to stall roughly one run in three, and when it does the default
    // ApiRequest.Timeout (100 seconds) turns a fast, loud failure into a slow, silent one that eats
    // a CI run. StallTimeout below is a mitigation, not a fix -- the root cause of the underlying
    // stall is not known. Five hypotheses were investigated and eliminated: (1) handler-cache
    // eviction -- the cache is lease-counted and correct, and the stall is observed at send #1 while
    // eviction only begins at send #8, so eviction cannot be the cause; (2) thread-pool starvation --
    // an instrumented capture at the moment of the stall showed pending=0, so no queued work was
    // waiting on a starved pool; (3) target-framework or WPF-specific differences -- 26 clean
    // standalone runs across two target frameworks never reproduced it; (4) xunit's
    // concurrency-limiting synchronization context -- the stall still occurs with test
    // parallelization disabled; (5) a TLS 1.3 client-certificate handshake stall -- the stall still
    // occurs with the connection pinned to TLS 1.2. The instrumented capture that does reproduce it
    // reads "send#1 FAIL:Timeout conns=1 reqs=1 pending=0": a second connection is opened whose
    // handshake never completes, on both the client and server sides, while the first connection and
    // its request/response accounting look entirely normal. The product code is exonerated for now --
    // 26 out of 26 standalone runs were clean, and the failure has only ever been reproduced under the
    // `dotnet test` host.
    //
    // A sixth investigation was run and is recorded here as INCONCLUSIVE rather than as a sixth
    // elimination, because it never got the chance to discriminate: ten sends over one certificate
    // (one handler, one pooled connection) versus ten over ten distinct certificates were compared in
    // the same host, to separate "per-certificate" from "per-connection" as mechanisms. Both arms
    // passed 8/8, and a concurrent control -- this very test, run 8 more times in the same session --
    // also passed 8/8. The stall had simply stopped reproducing. It did not return under 24 CPU
    // burners on a 28-core machine either (0/6), so raw processor contention is not the trigger.
    //
    // What the failing and non-failing periods actually differed by is concurrent *test-host* activity:
    // every observed failure happened while a Stryker.NET mutation run and several parallel build
    // agents were active, each spawning its own `dotnet test` host with the assembly loading and disk
    // traffic that implies. That -- not CPU load, and not certificate key containers -- is the most
    // promising remaining lead, and it is untested. Until it is understood, every send in this test gets a bounded
    // per-request timeout instead of the framework default: StallTimeout is 15 seconds, roughly 18x the
    // ~800ms this test takes when it passes, which is generous enough that the bound itself can never
    // become a flake, while still turning a 100-second silent stall into a 15-second one that fails
    // loudly with "The request timed out."
    //
    // FOR THE NEXT PERSON WHO SEES THIS STALL: `ConnectionInspector` (shipped in v1.87.0, and driven
    // from the command line by `certapi connections`) answers, from the runtime's own events, what
    // several of the investigations above had to infer — which connection each request went out on,
    // whether it was newly opened or taken from the pool, and how many requests it had already
    // served. Wrapping the sends below in one turns "was hypothesis (1), handler-cache eviction,
    // actually happening?" from an argument into an observation. It is not wired in here on purpose:
    // the stall does not currently reproduce, and instrumenting a passing test proves nothing.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Many_distinct_certificates_do_not_break_the_bounded_cache()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });

        await using var server = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert);
        using var client = new ApiClient();
        var transport = new TransportOptions { IgnoreServerCertificateErrors = true };

        // Ten distinct certificates through a cache whose capacity is 8: this exercises eviction
        // under live use, not just an empty cache filling up.
        var certs = Enumerable.Range(0, 10)
            .Select(i => SelfSignedCertificateFactory.CreateSignedCertificate($"Client{i}", ca, false, true))
            .ToList();
        try
        {
            foreach (var cert in certs)
            {
                var resp = await client.SendAsync(
                    new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl, Timeout = StallTimeout },
                    cert, transport: transport);
                Assert.True(resp.IsSuccess, resp.Error?.Message);
            }

            Assert.Equal(10, server.ConnectionCount);
        }
        finally
        {
            foreach (var cert in certs) cert.Dispose();
        }
    }

    [Fact]
    public async Task An_equal_certificate_object_shares_the_handler_even_after_the_original_is_disposed()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });

        byte[] pfxBytes;
        using (var original = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true))
            pfxBytes = original.Export(X509ContentType.Pfx);

        await using var server = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert);
        using var client = new ApiClient();
        var transport = new TransportOptions { IgnoreServerCertificateErrors = true };

        // Loaded exactly the way SelfSignedCertificateFactory loads its own certificates: SChannel
        // cannot use an ephemeral key, so an Exportable-only PKCS#12 round-trip is what makes the
        // private key usable by SslStream at all.
        var objectA = X509CertificateLoader.LoadPkcs12(pfxBytes, (string?)null, X509KeyStorageFlags.Exportable);
        string expectedThumbprint = objectA.Thumbprint;
        var first = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, objectA, transport: transport);
        Assert.True(first.IsSuccess, first.Error?.Message);

        // The original object is gone -- exactly what certapi run and the GUI do on every request,
        // fetching a fresh X509Certificate2 from the store each time -- so only an entry-owned copy
        // of the certificate (not a reference to this disposed object) can make the second send
        // below succeed at all.
        objectA.Dispose();

        var objectB = X509CertificateLoader.LoadPkcs12(pfxBytes, (string?)null, X509KeyStorageFlags.Exportable);
        try
        {
            var second = await client.SendAsync(
                new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl }, objectB, transport: transport);

            Assert.True(second.IsSuccess, second.Error?.Message);
            // The handler and its connection were shared across the two distinct-but-equal objects,
            // not reopened because the first object was disposed.
            Assert.Equal(1, server.ConnectionCount);
            Assert.Equal(new[] { expectedThumbprint }, server.ClientCertificateThumbprintsByConnection);
        }
        finally
        {
            objectB.Dispose();
        }
    }

    [Fact]
    public async Task A_cross_origin_redirect_reports_the_handshake_of_the_origin_it_ended_on()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        // A genuinely different origin -- its own TLS connection and its own handshake -- that the
        // redirect actually lands on, so BuildConnection's backwards scan over visitedOrigins has
        // more than one origin to choose between.
        await using var finalServer = await LoopbackMtlsServer.StartKeepAliveAsync(serverCert);
        await using var redirectServer = await LoopbackMtlsServer.StartRedirectAsync(
            serverCert, clientCert.Thumbprint!, finalServer.BaseUrl);

        using var client = new ApiClient();
        var resp = await client.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = redirectServer.BaseUrl }, clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.True(resp.IsSuccess, resp.Error?.Message);
        var conn = resp.Connection!;
        // If the backwards scan were keyed on the wrong origin, the handshake that actually carried
        // this response back would be unreachable and every one of these would come back null/false
        // instead.
        Assert.False(string.IsNullOrEmpty(conn.TlsProtocol));
        Assert.True(conn.ClientCertificateSent);
        Assert.NotNull(conn.ServerCertificateThumbprint);
    }
}
