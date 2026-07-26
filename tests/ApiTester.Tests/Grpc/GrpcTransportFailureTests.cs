using ApiTester.Core;
using ApiTester.Grpc;

namespace ApiTester.Tests.Grpc;

/// <summary>The defect this fixes: a dead HTTP/2 connection (an untrusted server certificate, a
/// closed port) used to surface as a bare <see cref="TaskCanceledException"/> — "a task was
/// canceled" — instead of the real gRPC status and cause chain. Real Kestrel, real TLS, a real
/// closed port; no mocks. Message substrings below were captured empirically against this .NET 9
/// build before being asserted on, per the brief.</summary>
public class GrpcTransportFailureTests
{
    [Fact]
    public async Task An_untrusted_server_certificate_is_diagnosed_as_a_certificate_problem()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, true, false, new[] { "localhost" });
        await using var server = await GrpcTestServer.StartTlsAsync(serverCert, requireClientCertificate: false);
        await using var caller = new GrpcCaller(server.Uri, clientCertificate: null, new TransportOptions());

        var ex = await Assert.ThrowsAsync<GrpcStatusException>(() => caller.DiscoverAsync(CancellationToken.None));

        Assert.Contains("certificate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_same_failure_is_not_reported_as_a_cancellation()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, true, false, new[] { "localhost" });
        await using var server = await GrpcTestServer.StartTlsAsync(serverCert, requireClientCertificate: false);
        await using var caller = new GrpcCaller(server.Uri, clientCertificate: null, new TransportOptions());

        var ex = await Record.ExceptionAsync(() => caller.DiscoverAsync(CancellationToken.None));

        Assert.NotNull(ex);
        Assert.False(ex is OperationCanceledException, $"must not be reported as a cancellation; observed: {ex}");
        Assert.DoesNotContain("A task was canceled", ex!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_connection_to_a_closed_port_is_diagnosed_as_a_connection_failure()
    {
        await using var caller = new GrpcCaller(new Uri("http://127.0.0.1:1"), clientCertificate: null, new TransportOptions());

        var ex = await Assert.ThrowsAsync<GrpcStatusException>(() => caller.DiscoverAsync(CancellationToken.None));

        Assert.Contains("refused", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_genuine_application_status_keeps_a_clean_detail()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = new GrpcCaller(server.Uri, clientCertificate: null, new TransportOptions());

        var result = await caller.InvokeAsync(
            "certapi.test.Echo", "Failing", "{}", Array.Empty<KeyValuePair<string, string>>(), CancellationToken.None);

        Assert.Contains("test denied", result.StatusDetail);
        Assert.DoesNotContain("caused by:", result.StatusDetail);
    }

    [Fact]
    public async Task Caller_cancellation_is_distinguishable_from_server_failure()
    {
        await using var server = await GrpcTestServer.StartAsync();
        await using var caller = new GrpcCaller(server.Uri, clientCertificate: null, new TransportOptions());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller.DiscoverAsync(cts.Token));
    }

    [Fact]
    public async Task A_reflection_less_server_still_reports_the_data_error_not_a_transport_error()
    {
        await using var server = await GrpcTestServer.StartAsync(reflection: false);
        await using var caller = new GrpcCaller(server.Uri, clientCertificate: null, new TransportOptions());

        await Assert.ThrowsAsync<GrpcReflectionUnavailableException>(() => caller.DiscoverAsync(CancellationToken.None));
    }
}
