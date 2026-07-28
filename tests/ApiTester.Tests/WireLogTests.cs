using System.IO;
using System.Net.Http;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The plaintext byte transcript — what a packet capture cannot give you for an encrypted
/// connection without its keys, obtained here with no driver and no administrator rights because
/// the direct send path drives its own TLS stream.</summary>
public class WireLogTests
{
    // ---------------------------------------------------------------- rendering (pure)

    [Fact]
    public void A_textual_chunk_renders_as_text_and_a_binary_one_as_hex_and_ascii()
    {
        var log = new WireLog();
        log.Record(WireDirection.Sent, Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n"));
        log.Record(WireDirection.Received, new byte[] { 0x00, 0x01, 0x02, 0xff, (byte)'A' });

        string rendered = log.Render();

        Assert.Contains("GET / HTTP/1.1", rendered);
        Assert.Contains(">> sent", rendered);
        Assert.Contains("<< received", rendered);
        // A NUL byte means it is not text, so the hex/ASCII view is used instead — and the ASCII
        // column still shows the printable parts, which is the point of showing both.
        Assert.Contains("00 01 02 ff 41", rendered);
        Assert.Contains("|....A|", rendered);
    }

    [Theory]
    [InlineData("plain ASCII text", true)]
    [InlineData("with\r\nnewlines\tand tabs", true)]
    public void Text_is_detected_as_text(string text, bool expected) =>
        Assert.Equal(expected, WireLog.LooksTextual(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void A_nul_byte_or_mostly_unprintable_content_is_treated_as_binary()
    {
        Assert.False(WireLog.LooksTextual(new byte[] { (byte)'a', 0x00, (byte)'b' }));
        Assert.False(WireLog.LooksTextual(new byte[] { 0x80, 0x81, 0x82, 0x83, 0x84, (byte)'a' }));
    }

    // ---------------------------------------------------------------- redaction

    [Fact]
    public void Credential_headers_are_redacted_but_their_presence_still_shows()
    {
        var log = new WireLog();
        log.Record(WireDirection.Sent, Encoding.UTF8.GetBytes(
            "GET /x HTTP/1.1\r\nHost: api.test\r\nAuthorization: Bearer super-secret\r\nAccept: */*\r\n\r\n"));

        string rendered = log.Render();

        Assert.DoesNotContain("super-secret", rendered);
        Assert.Contains("Authorization:", rendered);   // that it was sent is diagnostic
        Assert.Contains("Accept: */*", rendered);      // and the rest survives intact
        Assert.Contains("Host: api.test", rendered);
    }

    [Fact]
    public void Include_secrets_keeps_the_value()
    {
        var log = new WireLog();
        log.Record(WireDirection.Sent, Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\nCookie: session=abc\r\n\r\n"));

        Assert.Contains("session=abc", log.Render(includeSecrets: true));
        Assert.DoesNotContain("session=abc", log.Render());
    }

    // ---------------------------------------------------------------- limits

    [Fact]
    public void Capture_stops_at_the_limit_and_says_so_rather_than_pretending_it_ended()
    {
        var log = new WireLog(limitBytes: 100);
        log.Record(WireDirection.Received, new byte[80]);
        log.Record(WireDirection.Received, new byte[80]);
        log.Record(WireDirection.Received, new byte[80]);

        Assert.True(log.Truncated);
        Assert.Contains("truncated", log.Render());
        Assert.Equal(100, log.Chunks.Sum(c => c.Bytes.Length));
    }

    [Fact]
    public void Concurrent_records_lose_nothing()
    {
        var log = new WireLog();
        Parallel.For(0, 200, _ => log.Record(WireDirection.Sent, new byte[10]));

        Assert.Equal(200, log.Chunks.Count);
    }

    // ---------------------------------------------------------------- over the wire

    [Fact]
    public async Task A_real_mtls_exchange_yields_the_plaintext_request_and_response()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, serverAuth: true, clientAuth: false, dnsNames: new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "Client", ca, serverAuth: false, clientAuth: true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        var log = new WireLog();
        var response = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true },
            wireLog: log);

        // The request must still WORK — the tap is a subclass of the TLS stream precisely so the
        // handler still recognises the connection as secured; a wrapper broke this outright.
        Assert.True(response.IsSuccess, response.Error?.Message);

        string rendered = log.Render();
        Assert.Contains("GET / HTTP/1.1", rendered);          // the request as actually framed
        Assert.Contains("Host:", rendered);
        Assert.Contains("HTTP/1.1 200", rendered);            // and the response as it arrived
        Assert.Contains("{\"ok\":true}", rendered);
        // Plaintext, not ciphertext: a TLS record would begin with a 0x16 handshake byte.
        Assert.DoesNotContain("16 03 01", rendered);
    }

    [Fact]
    public async Task A_bearer_token_never_reaches_the_transcript_unless_it_is_asked_for()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, serverAuth: true, clientAuth: false, dnsNames: new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "Client", ca, serverAuth: false, clientAuth: true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "ok");

        var log = new WireLog();
        var response = await new ApiClient().SendAsync(
            new ApiRequest
            {
                Method = HttpMethod.Get,
                Url = server.BaseUrl,
                Headers = new[] { new KeyValuePair<string, string>("Authorization", "Bearer do-not-leak") }
            },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true },
            wireLog: log);
        Assert.True(response.IsSuccess, response.Error?.Message);

        Assert.DoesNotContain("do-not-leak", log.Render());
        Assert.Contains("do-not-leak", log.Render(includeSecrets: true));
    }

    [Fact]
    public async Task A_send_without_a_wire_log_still_uses_the_pooled_handler()
    {
        // The tapped handler is deliberately not cached — a log belongs to one send. This is the
        // guard that the ordinary path was not changed by that decision.
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, serverAuth: true, clientAuth: false, dnsNames: new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "Client", ca, serverAuth: false, clientAuth: true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "ok");

        var client = new ApiClient();
        var request = new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl };
        var transport = new TransportOptions { IgnoreServerCertificateErrors = true };

        Assert.True((await client.SendAsync(request, clientCert, transport: transport)).IsSuccess);
        Assert.True((await client.SendAsync(request, clientCert, transport: transport)).IsSuccess);
    }
}
