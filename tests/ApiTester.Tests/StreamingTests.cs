using ApiTester.Core;

namespace ApiTester.Tests;

public class StreamingTests
{
    [Fact]
    public async Task SseClient_streams_named_and_multiline_events()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        var events = new List<(string?, string)>
        {
            ("greeting", "hello"),
            (null, "line1\nline2"),
            ("tick", "3"),
        };
        await using var server = await LoopbackMtlsServer.StartSseAsync(serverCert, clientCert.Thumbprint!, events);

        var received = new List<SseEvent>();
        await foreach (var ev in SseClient.StreamAsync(server.BaseUrl, clientCert, null, ignoreServerCertificateErrors: true))
            received.Add(ev);

        Assert.Equal(3, received.Count);
        Assert.Equal("greeting", received[0].Event);
        Assert.Equal("hello", received[0].Data);
        Assert.Null(received[1].Event);
        Assert.Equal("line1\nline2", received[1].Data);   // two data: lines are rejoined with a newline
        Assert.Equal("tick", received[2].Event);
        Assert.Equal("3", received[2].Data);
    }

    [Fact]
    public async Task SseClient_stops_when_the_token_cancels()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        var events = new List<(string?, string)> { (null, "one"), (null, "two"), (null, "three") };
        await using var server = await LoopbackMtlsServer.StartSseAsync(serverCert, clientCert.Thumbprint!, events);

        using var cts = new CancellationTokenSource();
        var received = new List<string>();
        await foreach (var ev in SseClient.StreamAsync(server.BaseUrl, clientCert, null, true, cts.Token))
        {
            received.Add(ev.Data);
            cts.Cancel();   // stop after the first event
        }
        Assert.Equal(new[] { "one" }, received);
    }

    [Fact]
    public async Task WebSocketSession_echoes_text_messages()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartWebSocketEchoAsync(serverCert, clientCert.Thumbprint!);

        await using var session = new WebSocketSession();
        await session.ConnectAsync(server.WebSocketUrl, clientCert, null, ignoreServerCertificateErrors: true);
        await session.SendTextAsync("hello");
        await session.SendTextAsync("world");

        var got = new List<string>();
        await foreach (var msg in session.ReceiveAllAsync())
        {
            if (msg.IsClose) break;
            got.Add(msg.Text);
            if (got.Count == 2) break;
        }
        await session.CloseAsync();

        Assert.Equal(new[] { "hello", "world" }, got);
    }

    [Fact]
    public async Task WebSocketSession_round_trips_a_large_message()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartWebSocketEchoAsync(serverCert, clientCert.Thumbprint!);

        await using var session = new WebSocketSession();
        await session.ConnectAsync(server.WebSocketUrl, clientCert, null, ignoreServerCertificateErrors: true);

        string big = new string('x', 5000);   // > 125 bytes forces the 16-bit length path
        await session.SendTextAsync(big);

        string? echoed = null;
        await foreach (var msg in session.ReceiveAllAsync())
        {
            if (msg.IsClose) break;
            echoed = msg.Text;
            break;
        }
        await session.CloseAsync();

        Assert.Equal(big, echoed);
    }
}
