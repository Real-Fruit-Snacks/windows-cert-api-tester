using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ApiTester.Core;
using ApiTester.Grpc;

namespace ApiTester.Tests.Grpc;

/// <summary>The certificate behavior that is the whole point of this tool: mutual TLS through
/// <see cref="GrpcCaller"/> proves the client certificate the server observed is the one actually
/// presented (with a negative control proving a server that requires one refuses a call without it),
/// and a pinned server certificate lets a call through <c>certapi trust add</c>-style, without the
/// blanket insecure bypass (with a negative control proving the pin is actually consulted). Real
/// Kestrel, real TLS, real certificates built with <see cref="SelfSignedCertificateFactory"/> — no
/// mocks.</summary>
public class GrpcTlsTests
{
    [Fact]
    public async Task Mutual_tls_call_reports_the_exact_thumbprint_of_the_certificate_presented()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("GrpcClient", ca, false, true);

        await using var server = await GrpcTestServer.StartTlsAsync(serverCert, requireClientCertificate: true);
        await using var caller = new GrpcCaller(
            server.Uri, clientCert, new TransportOptions { IgnoreServerCertificateErrors = true });

        var result = await caller.InvokeAsync(
            "certapi.test.Echo", "Unary", """{"text":"mtls"}""",
            Array.Empty<KeyValuePair<string, string>>(), CancellationToken.None);

        Assert.Equal(0, result.StatusCode);
        using var document = JsonDocument.Parse(result.ResponseJson);
        string observed = document.RootElement.GetProperty("observedClientCertThumbprint").GetString()!;
        Assert.Equal(clientCert.Thumbprint, observed, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Without_a_client_certificate_a_server_that_requires_one_refuses_the_call()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, true, false, new[] { "localhost" });

        await using var server = await GrpcTestServer.StartTlsAsync(serverCert, requireClientCertificate: true);
        await using var caller = new GrpcCaller(
            server.Uri, clientCertificate: null, new TransportOptions { IgnoreServerCertificateErrors = true });

        // Deterministic now that transport failures are translated properly: the refused
        // mutual-TLS handshake tears down the HTTP/2 connection the preceding reflection call
        // needs, and that connection failure surfaces as a GrpcStatusException carrying the real
        // cause (observed: an HTTP/2-handshake IOException) rather than as a bare
        // TaskCanceledException/OperationCanceledException — the defect this test guards against.
        try
        {
            var result = await caller.InvokeAsync(
                "certapi.test.Echo", "Unary", """{"text":"no-cert"}""",
                Array.Empty<KeyValuePair<string, string>>(), CancellationToken.None);

            Assert.NotEqual(0, result.StatusCode);
            Assert.False(string.IsNullOrWhiteSpace(result.StatusDetail));
        }
        catch (Exception ex)
        {
            var status = Assert.IsType<GrpcStatusException>(ex);
            Assert.False(string.IsNullOrWhiteSpace(status.StatusDetail));
            // The guarded defect is that a refused handshake used to surface as a bare
            // "A task was canceled." with the cause discarded. Assert that property directly rather
            // than matching a phase word like "handshake": which phase the teardown is reported in
            // is not guaranteed by the runtime and varied under load, so matching it made a real
            // regression and a rescheduled thread indistinguishable.
            Assert.DoesNotContain("A task was canceled", status.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("caused by", status.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_pinned_server_certificate_is_honored_without_the_blanket_insecure_bypass()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, true, false, new[] { "127.0.0.1" });

        await using var server = await GrpcTestServer.StartTlsAsync(serverCert, requireClientCertificate: false);

        var state = new AppState();
        TrustService.Trust(state, "127.0.0.1", serverCert);
        bool TrustServerCertificate(X509Certificate2? presented) =>
            presented is not null && TrustService.IsTrusted(state, "127.0.0.1", presented.Thumbprint!);

        await using var caller = new GrpcCaller(
            server.Uri, clientCertificate: null, new TransportOptions(), TrustServerCertificate);

        var result = await caller.InvokeAsync(
            "certapi.test.Echo", "Unary", """{"text":"pinned"}""",
            Array.Empty<KeyValuePair<string, string>>(), CancellationToken.None);

        Assert.Equal(0, result.StatusCode);
    }

    [Fact]
    public async Task Online_revocation_against_an_unreachable_crl_succeeds_by_default_but_is_fatal_when_strict()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, true, false, new[] { "127.0.0.1" },
            crlDistributionPoint: SelfSignedCertificateFactory.UnroutableCrlDistributionPoint);

        await using var server = await GrpcTestServer.StartTlsAsync(serverCert, requireClientCertificate: false);

        // The CA is self-signed and therefore untrusted on its own, so a pin matching the server
        // certificate's own thumbprint is supplied on every call below — exactly as
        // A_pinned_server_certificate_is_honored_without_the_blanket_insecure_bypass does. That holds
        // trust constant so the revocation mode is the only thing that differs between the calls.
        var state = new AppState();
        TrustService.Trust(state, "127.0.0.1", serverCert);
        bool TrustServerCertificate(X509Certificate2? presented) =>
            presented is not null && TrustService.IsTrusted(state, "127.0.0.1", presented.Thumbprint!);

        // An unreachable revocation endpoint yields an unknown status, and an unknown status must not
        // be fatal by default (ruling 3) — the pin still covers the untrusted root, as it does today.
        await using (var onlineCaller = new GrpcCaller(
            server.Uri, clientCertificate: null,
            new TransportOptions { Revocation = RevocationMode.Online }, TrustServerCertificate))
        {
            var result = await onlineCaller.InvokeAsync(
                "certapi.test.Echo", "Unary", """{"text":"online"}""",
                Array.Empty<KeyValuePair<string, string>>(), CancellationToken.None);
            Assert.Equal(0, result.StatusCode);
        }

        // --revocation-strict makes an unknown status fatal, and a pin does not rescue it. This
        // refusal is only possible if the platform genuinely attempted the revocation fetch and the
        // shared table then judged the result — the proof the mode actually reached the TLS stack.
        await using (var strictCaller = new GrpcCaller(
            server.Uri, clientCertificate: null,
            new TransportOptions { Revocation = RevocationMode.Online, RevocationStrict = true },
            TrustServerCertificate))
        {
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await strictCaller.InvokeAsync(
                    "certapi.test.Echo", "Unary", """{"text":"strict"}""",
                    Array.Empty<KeyValuePair<string, string>>(), CancellationToken.None));
        }

        // The default mode (RevocationMode.None) succeeds exactly as it does today, proving the
        // unchanged default path.
        await using (var defaultCaller = new GrpcCaller(
            server.Uri, clientCertificate: null, new TransportOptions(), TrustServerCertificate))
        {
            var result = await defaultCaller.InvokeAsync(
                "certapi.test.Echo", "Unary", """{"text":"default"}""",
                Array.Empty<KeyValuePair<string, string>>(), CancellationToken.None);
            Assert.Equal(0, result.StatusCode);
        }
    }

    [Fact]
    public async Task An_unpinned_server_certificate_is_rejected_when_nothing_is_trusted()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, true, false, new[] { "127.0.0.1" });

        await using var server = await GrpcTestServer.StartTlsAsync(serverCert, requireClientCertificate: false);

        // An empty trust store: the callback is wired exactly as production wires it, but nothing
        // has ever been pinned, so IsTrusted must return false for every thumbprint.
        var state = new AppState();
        bool TrustServerCertificate(X509Certificate2? presented) =>
            presented is not null && TrustService.IsTrusted(state, "127.0.0.1", presented.Thumbprint!);

        await using var caller = new GrpcCaller(
            server.Uri, clientCertificate: null, new TransportOptions(), TrustServerCertificate);

        // Deterministic now that transport failures are translated properly: the failed TLS
        // validation tears down the HTTP/2 connection mid-call, and that connection failure
        // surfaces as a GrpcStatusException carrying the real cause (observed: an
        // AuthenticationException naming the rejected certificate) rather than as a bare
        // TaskCanceledException/OperationCanceledException — the same defect the mutual-TLS
        // negative control above guards against.
        try
        {
            var result = await caller.InvokeAsync(
                "certapi.test.Echo", "Unary", """{"text":"unpinned"}""",
                Array.Empty<KeyValuePair<string, string>>(), CancellationToken.None);

            Assert.NotEqual(0, result.StatusCode);
            Assert.False(string.IsNullOrWhiteSpace(result.StatusDetail));
        }
        catch (Exception ex)
        {
            var status = Assert.IsType<GrpcStatusException>(ex);
            Assert.False(string.IsNullOrWhiteSpace(status.StatusDetail));
            Assert.Contains("certificate", status.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
