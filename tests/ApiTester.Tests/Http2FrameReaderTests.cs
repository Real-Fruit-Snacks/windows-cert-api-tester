using System.IO;
using System.Net.Http;
using ApiTester.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ApiTester.Tests;

/// <summary>The HTTP/2 frame reader, tested as what it is: a pure function from bytes to frames.
/// Every case here builds the bytes by hand, so a failure means the decoder is wrong rather than
/// that a server behaved unexpectedly.</summary>
public class Http2FrameReaderTests
{
    /// <summary>A frame: 3-byte length, type, flags, 4-byte stream identifier, payload.</summary>
    private static byte[] Frame(byte type, byte flags, int stream, params byte[] payload)
    {
        var bytes = new List<byte>
        {
            (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length,
            type, flags,
            (byte)(stream >> 24), (byte)(stream >> 16), (byte)(stream >> 8), (byte)stream
        };
        bytes.AddRange(payload);
        return bytes.ToArray();
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void The_preface_is_recognised_and_not_mistaken_for_a_frame()
    {
        var bytes = Concat(Http2FrameReader.Preface.ToArray(), Frame(4, 0, 0));
        var read = Http2FrameReader.Read(bytes, WireDirection.Sent);

        Assert.True(read.PrefaceSeen);
        var frame = Assert.Single(read.Frames);
        Assert.Equal("SETTINGS", frame.TypeName);
    }

    [Fact]
    public void A_stream_without_the_preface_is_reported_as_such()
    {
        // The normal case for this product: connections are pooled, so a capture usually joins one
        // already running. That fact is what bounds header decoding, so it must be reported.
        var read = Http2FrameReader.Read(Frame(4, 0, 0), WireDirection.Sent);
        Assert.False(read.PrefaceSeen);
        Assert.Single(read.Frames);
    }

    [Fact]
    public void Settings_values_are_named_not_left_as_numbers()
    {
        // MAX_CONCURRENT_STREAMS=100, INITIAL_WINDOW_SIZE=65535
        var payload = new byte[] { 0, 3, 0, 0, 0, 100, 0, 4, 0, 0, 0xff, 0xff };
        var frame = Assert.Single(Http2FrameReader.Read(Frame(4, 0, 0, payload), WireDirection.Received).Frames);

        Assert.Equal("MAX_CONCURRENT_STREAMS=100 INITIAL_WINDOW_SIZE=65535", frame.Detail);
    }

    [Fact]
    public void A_settings_ack_carries_no_payload_and_says_ack()
    {
        var frame = Assert.Single(Http2FrameReader.Read(Frame(4, 0x01, 0), WireDirection.Received).Frames);
        Assert.Equal("ACK", frame.FlagNames);
        Assert.Equal("", frame.Detail);
    }

    [Fact]
    public void Goaway_gives_the_error_code_and_the_debug_text()
    {
        // This is the frame that answers "why did the gateway hang up?", so its debug data — the
        // one place a server explains itself in words — must survive to the report.
        var payload = Concat(
            new byte[] { 0, 0, 0, 7 },                       // last stream 7
            new byte[] { 0, 0, 0, 11 },                      // ENHANCE_YOUR_CALM
            "too many requests"u8.ToArray());
        var frame = Assert.Single(Http2FrameReader.Read(Frame(7, 0, 0, payload), WireDirection.Received).Frames);

        Assert.Equal("lastStream=7 error=ENHANCE_YOUR_CALM debug=\"too many requests\"", frame.Detail);
    }

    [Fact]
    public void Rst_stream_and_window_update_are_read()
    {
        var rst = Assert.Single(Http2FrameReader.Read(
            Frame(3, 0, 5, 0, 0, 0, 8), WireDirection.Received).Frames);
        Assert.Equal("error=CANCEL", rst.Detail);
        Assert.Equal(5, rst.StreamId);

        var window = Assert.Single(Http2FrameReader.Read(
            Frame(8, 0, 0, 0, 0, 0x40, 0), WireDirection.Received).Frames);
        Assert.Equal("increment=16384", window.Detail);
    }

    [Fact]
    public void An_unknown_frame_type_is_surfaced_rather_than_hidden()
    {
        // Extensions are legal. A reader that dropped them would misrepresent the connection.
        var frame = Assert.Single(Http2FrameReader.Read(Frame(0x63, 0, 1, 1, 2, 3), WireDirection.Received).Frames);
        Assert.Equal("UNKNOWN(99)", frame.TypeName);
        Assert.Equal(3, frame.Length);
    }

    [Fact]
    public void The_reserved_bit_of_a_stream_identifier_is_ignored()
    {
        // RFC 9113 §4.1: the high bit is reserved and MUST be ignored, not folded into the value.
        var bytes = Frame(0, 0, 0, 0xff);
        bytes[5] |= 0x80;
        var frame = Assert.Single(Http2FrameReader.Read(bytes, WireDirection.Sent).Frames);
        Assert.Equal(0, frame.StreamId);
    }

    [Fact]
    public void The_same_flag_bit_is_named_for_its_own_frame_type()
    {
        // 0x01 is END_STREAM on DATA and ACK on SETTINGS. One shared table would mislabel half.
        var data = Assert.Single(Http2FrameReader.Read(Frame(0, 0x01, 1, 65), WireDirection.Sent).Frames);
        var settings = Assert.Single(Http2FrameReader.Read(Frame(4, 0x01, 0), WireDirection.Sent).Frames);

        Assert.Equal("END_STREAM", data.FlagNames);
        Assert.Equal("ACK", settings.FlagNames);
    }

    [Fact]
    public void A_frame_cut_short_by_the_capture_limit_is_reported_not_invented()
    {
        var whole = Frame(0, 0, 1, 1, 2, 3, 4, 5, 6, 7, 8);
        var read = Http2FrameReader.Read(whole[..^3], WireDirection.Received);

        Assert.Empty(read.Frames);
        Assert.Equal(whole.Length - 3, read.TrailingBytes);
    }

    [Fact]
    public void Frames_are_stamped_from_the_chunk_they_start_in()
    {
        // A window update arriving long after the data that filled the window is the whole answer
        // to "why did it hang", so the timestamps have to be real rather than uniform.
        var first = Frame(4, 0, 0);
        var second = Frame(8, 0, 0, 0, 0, 0x40, 0);
        var offsets = new List<(int, TimeSpan)>
        {
            (0, TimeSpan.FromMilliseconds(5)),
            (first.Length, TimeSpan.FromMilliseconds(4000)),
        };

        var frames = Http2FrameReader.Read(Concat(first, second), WireDirection.Received, offsets).Frames;

        Assert.Equal(TimeSpan.FromMilliseconds(5), frames[0].At);
        Assert.Equal(TimeSpan.FromMilliseconds(4000), frames[1].At);
    }

    // ---------------------------------------------------------------- HPACK, scoped

    [Fact]
    public void Static_table_references_are_decoded_because_they_need_no_history()
    {
        // 0x82 = indexed field 2 (:method GET), 0x87 = 7 (:scheme https), 0x84 = 4 (:path /)
        var frame = Assert.Single(Http2FrameReader.Read(
            Frame(1, 0x04, 1, 0x82, 0x87, 0x84), WireDirection.Sent).Frames);

        Assert.Contains(":method: GET", frame.Detail);
        Assert.Contains(":scheme: https", frame.Detail);
        Assert.Contains(":path: /", frame.Detail);
        Assert.Contains("3 field(s)", frame.Detail);
    }

    [Fact]
    public void A_dynamic_table_reference_is_counted_and_admitted_never_guessed()
    {
        // Index 62 is the first dynamic slot: unknowable without the connection's history.
        var frame = Assert.Single(Http2FrameReader.Read(
            Frame(1, 0x04, 1, 0xbe), WireDirection.Sent).Frames);

        Assert.Contains("1 field(s)", frame.Detail);
        Assert.Contains("needing the connection's HPACK history", frame.Detail);
    }

    [Fact]
    public void An_uncompressed_literal_is_decoded_name_and_value()
    {
        // Literal without indexing, new name: 0x00, then len+"x-probe", then len+"hello".
        var payload = new List<byte> { 0x00, 7 };
        payload.AddRange("x-probe"u8.ToArray());
        payload.Add(5);
        payload.AddRange("hello"u8.ToArray());

        var frame = Assert.Single(Http2FrameReader.Read(
            Frame(1, 0x04, 1, payload.ToArray()), WireDirection.Sent).Frames);

        Assert.Contains("x-probe: hello", frame.Detail);
    }

    [Fact]
    public void A_huffman_literal_is_named_as_such_and_stepped_over_correctly()
    {
        // The value is Huffman-coded (high bit of the length byte). It is not decoded — see the
        // Hpack class note — but the walk must still land on the next field, which the trailing
        // static reference proves.
        var payload = new List<byte> { 0x00, 7 };
        payload.AddRange("x-probe"u8.ToArray());
        payload.Add(0x83);                       // Huffman, 3 bytes
        payload.AddRange(new byte[] { 0xaa, 0xbb, 0xcc });
        payload.Add(0x82);                       // :method GET — reached only if the walk is right

        var frame = Assert.Single(Http2FrameReader.Read(
            Frame(1, 0x04, 1, payload.ToArray()), WireDirection.Sent).Frames);

        Assert.Contains("x-probe: (huffman, 3B)", frame.Detail);
        Assert.Contains(":method: GET", frame.Detail);
        Assert.Contains("2 field(s)", frame.Detail);
    }

    [Fact]
    public void A_header_block_never_leaks_a_credential_without_the_explicit_flag()
    {
        // Program rule: every new output path proves a token cannot appear by default.
        var payload = new List<byte> { 0x00, 13 };
        payload.AddRange("authorization"u8.ToArray());
        payload.Add(16);
        payload.AddRange("Bearer sekrit-42"u8.ToArray());
        var bytes = Frame(1, 0x04, 1, payload.ToArray());

        var redacted = Assert.Single(Http2FrameReader.Read(bytes, WireDirection.Sent).Frames);
        Assert.DoesNotContain("sekrit-42", redacted.Detail);
        Assert.Contains("authorization: (redacted)", redacted.Detail);

        var opened = Assert.Single(Http2FrameReader.Read(bytes, WireDirection.Sent, null, includeSecrets: true).Frames);
        Assert.Contains("Bearer sekrit-42", opened.Detail);
    }

    [Fact]
    public void Padding_and_priority_are_stripped_before_the_header_block_is_read()
    {
        // Both sit between the frame header and the block; misreading either shifts every field.
        var payload = new List<byte> { 2 };                       // PADDED: 2 bytes of padding
        payload.AddRange(new byte[] { 0, 0, 0, 5, 16 });           // PRIORITY: dependency + weight
        payload.Add(0x82);                                         // :method GET
        payload.AddRange(new byte[] { 0, 0 });                     // the padding itself

        var frame = Assert.Single(Http2FrameReader.Read(
            Frame(1, 0x04 | 0x08 | 0x20, 1, payload.ToArray()), WireDirection.Sent).Frames);

        Assert.Contains(":method: GET", frame.Detail);
        Assert.Contains("1 field(s)", frame.Detail);
    }

    [Fact]
    public void A_multi_byte_hpack_integer_is_read()
    {
        // RFC 7541 §5.1: a value filling the prefix continues in seven-bit octets. Index 1337
        // encodes as 0xff 0x9a 0x0a with a 7-bit prefix; it is a dynamic index, so it must count
        // as one undecodable field rather than derail the walk.
        var frame = Assert.Single(Http2FrameReader.Read(
            Frame(1, 0x04, 1, 0xff, 0x9a, 0x0a, 0x82), WireDirection.Sent).Frames);

        Assert.Contains("2 field(s)", frame.Detail);
        Assert.Contains(":method: GET", frame.Detail);
    }
}

/// <summary>The frame view as the user meets it, over a <see cref="WireLog"/>.</summary>
public class WireLogFrameTests
{
    private static byte[] Frame(byte type, byte flags, int stream, params byte[] payload)
    {
        var bytes = new List<byte>
        {
            (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length,
            type, flags,
            (byte)(stream >> 24), (byte)(stream >> 16), (byte)(stream >> 8), (byte)stream
        };
        bytes.AddRange(payload);
        return bytes.ToArray();
    }

    [Fact]
    public void Both_directions_appear_in_one_timeline()
    {
        var log = new WireLog();
        log.Record(WireDirection.Sent, Http2FrameReader.Preface);
        log.Record(WireDirection.Sent, Frame(4, 0, 0));
        log.Record(WireDirection.Received, Frame(4, 0x01, 0));
        log.Record(WireDirection.Received, Frame(7, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1));

        string report = log.RenderFrames();

        Assert.Contains(">> ", report);
        Assert.Contains("<< ", report);
        Assert.Contains("SETTINGS", report);
        Assert.Contains("GOAWAY", report);
        Assert.Contains("error=PROTOCOL_ERROR", report);
    }

    [Fact]
    public void A_frame_split_across_two_chunks_is_still_one_frame()
    {
        // The tap records whatever each read returned, which has nothing to do with frame
        // boundaries; a reader that parsed chunk by chunk would see garbage.
        var whole = Frame(4, 0, 0, 0, 3, 0, 0, 0, 100);
        var log = new WireLog();
        log.Record(WireDirection.Received, whole.AsSpan(0, 4));
        log.Record(WireDirection.Received, whole.AsSpan(4));

        string report = log.RenderFrames();

        Assert.Contains("SETTINGS", report);
        Assert.Contains("MAX_CONCURRENT_STREAMS=100", report);
    }

    [Fact]
    public void An_http1_exchange_says_so_instead_of_reporting_nothing()
    {
        var log = new WireLog();
        log.Record(WireDirection.Sent, "GET / HTTP/1.1\r\nHost: x\r\n\r\n"u8);

        string report = log.RenderFrames();

        Assert.Contains("not an HTTP/2 connection", report);
        Assert.Contains("--wire", report);
    }

    [Fact]
    public void Joining_an_established_connection_is_stated_in_the_report()
    {
        var log = new WireLog();
        log.Record(WireDirection.Sent, Frame(1, 0x04, 1, 0x82));

        Assert.Contains("cannot be replayed", log.RenderFrames());
    }

    [Fact]
    public async Task A_real_http2_exchange_decodes_to_real_frames()
    {
        // The hand-built cases above prove the decoder; this proves it is pointed at the right
        // bytes on a live connection — that ALPN negotiated h2 through the tapped TLS stream, and
        // that what the tap recorded is genuinely HTTP/2 framing rather than plausible-looking
        // rubbish. Everything specific asserted here was produced by a real server.
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, serverAuth: true, clientAuth: false, dnsNames: new[] { "localhost" });

        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0, listen =>
        {
            listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
            listen.UseHttps(serverCert);
        }));
        var app = builder.Build();
        app.MapGet("/frames", () => "ok");
        await app.StartAsync();
        try
        {
            var log = new WireLog();
            var response = await new ApiClient().SendAsync(
                new ApiRequest { Method = HttpMethod.Get, Url = app.Urls.First() + "/frames" },
                clientCertificate: null,
                transport: new TransportOptions
                {
                    IgnoreServerCertificateErrors = true,
                    Version = HttpVersionMode.Http2,
                },
                wireLog: log);

            Assert.True(response.IsSuccess, response.Error?.Message);

            string report = log.RenderFrames();

            // The client opens with the preface and SETTINGS; the server answers with its own.
            Assert.Contains("SETTINGS", report);
            Assert.Contains("HEADERS", report);
            Assert.Contains(">> ", report);
            Assert.Contains("<< ", report);
            // A real capture that started at byte one: the dynamic-table caveat must NOT appear.
            Assert.DoesNotContain("cannot be replayed", report);
            // Request pseudo-headers are static-table references, so they decode without history.
            Assert.Contains(":method: GET", report);
            // And nothing was left dangling — every byte parsed as a whole frame.
            Assert.DoesNotContain("left over", report);
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
