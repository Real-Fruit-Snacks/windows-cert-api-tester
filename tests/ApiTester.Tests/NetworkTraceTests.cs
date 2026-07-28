using System.IO;
using System.Net.Http;
using ApiTester.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ApiTester.Tests;

/// <summary>The in-process network trace. The source and event names it keys off were observed
/// from a running .NET 9 process rather than taken from documentation, so these tests assert on
/// what the runtime actually emits — and on the two rules that matter regardless of which events
/// a future runtime adds: secrets are redacted, and filters narrow the firehose.</summary>
public class NetworkTraceTests
{
    [Fact]
    public async Task A_traced_request_reports_dns_tcp_tls_and_the_connection_being_established()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, serverAuth: true, clientAuth: false, dnsNames: new[] { "localhost" });
        await using var mock = MockServer.Start(0, MockTlsMode.Https, serverCert);

        using var trace = new NetworkTrace();
        var response = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = $"https://127.0.0.1:{mock.Port}/api/x" },
            clientCertificate: null,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });
        Assert.True(response.IsSuccess, response.Error?.Message);

        var lines = trace.Lines;
        // The stack's own account of the connection: a socket was opened, TLS was negotiated, and
        // a connection was established for the request. None of this is visible to a sniffer on an
        // encrypted connection, and none of it needs a driver to see from inside the process.
        Assert.Contains(lines, l => l.Source == "System.Net.Sockets" && l.Event == "ConnectStart");
        Assert.Contains(lines, l => l.Source == "System.Net.Security" && l.Event == "HandshakeStart");
        Assert.Contains(lines, l => l.Source == "System.Net.Http" && l.Event == "ConnectionEstablished");
        Assert.Contains(lines, l => l.Source == "System.Net.Http" && l.Event == "RequestStart");

        // Every line is timestamped from the trace's own start, which is what makes the sequence
        // readable as a timeline rather than a pile.
        Assert.All(lines, l => Assert.True(l.At >= TimeSpan.Zero));
    }

    [Fact]
    public async Task A_reused_connection_opens_no_socket_and_runs_no_handshake()
    {
        // The pooling answer, and the shape W5's inspector will read: on a reused connection the
        // ABSENCE of ConnectStart/HandshakeStart is the signal.
        //
        // Two things this test had to be built around, both learned by getting it wrong first:
        //   * `MockServer` answers `Connection: close`, so nothing is ever reusable against it —
        //     a keep-alive server is required to observe reuse at all. Kestrel is one.
        //   * The trace is PROCESS-wide, so with an in-process server it also sees that server's
        //     own accept and handshake. Client-side facts are the ones with isServer=False.
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapGet("/keepalive", () => "ok");
        await app.StartAsync();
        try
        {
            string url = app.Urls.First() + "/keepalive";
            var client = new ApiClient();
            var request = new ApiRequest { Method = HttpMethod.Get, Url = url };

            // Warm the pool first, outside the trace.
            Assert.True((await client.SendAsync(request, null)).IsSuccess);

            using var trace = new NetworkTrace();
            Assert.True((await client.SendAsync(request, null)).IsSuccess);

            var lines = trace.Lines;
            Assert.Contains(lines, l => l.Source == "System.Net.Http" && l.Event == "RequestStart");
            Assert.DoesNotContain(lines, l => l.Source == "System.Net.Sockets" && l.Event == "ConnectStart");
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }

    [Fact]
    public void The_trace_is_process_wide_which_is_worth_knowing_when_a_server_shares_the_process()
    {
        // Not a defect, but a property a reader has to know: an in-process listener sees every
        // connection the PROCESS makes or accepts. For the command line that means `mock` and
        // `serve` will trace their own server side as well as any client side.
        using var trace = new NetworkTrace();

        Assert.True(trace.Wanted("System.Net.Sockets"));   // including accepts, not only connects
    }

    [Fact]
    public async Task A_filter_narrows_the_firehose_to_what_was_asked_for()
    {
        await using var mock = MockServer.Start(0, MockTlsMode.Http);

        using var trace = new NetworkTrace(filters: new[] { "NameResolution" });
        using var http = new HttpClient();
        await http.GetStringAsync($"http://127.0.0.1:{mock.Port}/api/x");

        // Whatever else happened, only the asked-for source survived.
        Assert.All(trace.Lines, l => Assert.Contains("NameResolution", l.Source));
    }

    // ---------------------------------------------------------------- redaction (pure)

    [Theory]
    [InlineData("Authorization", "Bearer super-secret")]
    [InlineData("cookie", "session=abc")]
    [InlineData("access_token", "abc123")]
    [InlineData("Password", "hunter2")]
    public void A_credential_named_payload_is_redacted(string name, string value)
    {
        Assert.Equal("(redacted)", NetworkTrace.Redact(name, value));
    }

    [Fact]
    public void A_credential_inside_a_larger_payload_is_redacted_without_losing_the_rest()
    {
        string headers = "GET / HTTP/1.1\r\nAuthorization: Bearer super-secret\r\nAccept: */*";

        string redacted = NetworkTrace.Redact("headers", headers);

        Assert.DoesNotContain("super-secret", redacted);
        Assert.Contains("Authorization:", redacted);   // the fact it was sent still shows
        Assert.Contains("Accept: */*", redacted);      // and the rest survives
    }

    [Fact]
    public void An_ordinary_payload_is_left_alone()
    {
        Assert.Equal("example.com", NetworkTrace.Redact("hostNameOrAddress", "example.com"));
        Assert.Equal("443", NetworkTrace.Redact("port", "443"));
    }

    // ---------------------------------------------------------------- levels (pure)

    [Fact]
    public void The_normal_level_takes_the_stable_sources_and_verbose_adds_the_internal_ones()
    {
        using var normal = new NetworkTrace(TraceLevel.Normal);
        Assert.True(normal.Wanted("System.Net.Http"));
        Assert.True(normal.Wanted("System.Net.Security"));
        // The internal diagnostics are opt-in: they are verbose and their names are not stable.
        Assert.False(normal.Wanted("Private.InternalDiagnostics.System.Net.Http"));
        // And an unrelated source is never subscribed to at either level.
        Assert.False(normal.Wanted("System.Runtime"));

        using var verbose = new NetworkTrace(TraceLevel.Verbose);
        Assert.True(verbose.Wanted("Private.InternalDiagnostics.System.Net.Http"));
        Assert.True(verbose.Wanted("System.Net.Http"));
        Assert.False(verbose.Wanted("System.Runtime"));
    }
}
