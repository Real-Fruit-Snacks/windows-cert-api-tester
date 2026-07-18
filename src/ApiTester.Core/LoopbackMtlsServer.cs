using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ApiTester.Core;

public sealed class LoopbackMtlsServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly System.Collections.Concurrent.ConcurrentBag<Task> _handlers = new();
    private bool _disposed;

    public string BaseUrl { get; }

    /// <summary>The same loopback endpoint addressed as a WebSocket (wss://) rather than https://.</summary>
    public string WebSocketUrl => "wss://" + BaseUrl["https://".Length..];

    private LoopbackMtlsServer(TcpListener listener, int port,
        Func<TcpClient, CancellationToken, Task> handleClient)
    {
        _listener = listener;
        BaseUrl = $"https://127.0.0.1:{port}/";
        _acceptLoop = AcceptLoopAsync(handleClient);
    }

    public static Task<LoopbackMtlsServer> StartAsync(
        X509Certificate2 serverCertificate,
        string expectedClientThumbprint,
        string responseBody = "{\"ok\":true}",
        string responseContentType = "application/json")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = new LoopbackMtlsServer(listener, port,
            (client, ct) => HandleClientAsync(
                client, serverCertificate, expectedClientThumbprint,
                responseBody, responseContentType, ct));

        return Task.FromResult(server);
    }

    /// <summary>A server that echoes the request line, headers, and body back in its 200 response —
    /// lets a test assert exactly what the client sent through.</summary>
    public static Task<LoopbackMtlsServer> StartEchoAsync(
        X509Certificate2 serverCertificate, string expectedClientThumbprint)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new LoopbackMtlsServer(listener, port,
            (client, ct) => HandleEchoAsync(client, serverCertificate, expectedClientThumbprint, ct));
        return Task.FromResult(server);
    }

    /// <summary>A server that answers every request with a 302 to <paramref name="location"/>.</summary>
    public static Task<LoopbackMtlsServer> StartRedirectAsync(
        X509Certificate2 serverCertificate, string expectedClientThumbprint, string location)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new LoopbackMtlsServer(listener, port,
            (client, ct) => HandleRedirectAsync(client, serverCertificate, expectedClientThumbprint, location, ct));
        return Task.FromResult(server);
    }

    /// <summary>A server that sets a cookie (Set-Cookie: srv=ok) and echoes any Cookie header it
    /// received in the response body — lets a test prove a cookie jar carries cookies across calls.</summary>
    public static Task<LoopbackMtlsServer> StartCookieEchoAsync(
        X509Certificate2 serverCertificate, string expectedClientThumbprint)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new LoopbackMtlsServer(listener, port,
            (client, ct) => HandleCookieEchoAsync(client, serverCertificate, expectedClientThumbprint, ct));
        return Task.FromResult(server);
    }

    private static async Task HandleCookieEchoAsync(
        TcpClient client, X509Certificate2 serverCert, string expectedClientThumbprint, CancellationToken ct)
    {
        await using var ssl = await AuthenticateServerAsync(client, serverCert, expectedClientThumbprint, ct);
        var buffer = new byte[4096];
        var request = new StringBuilder();
        while (!request.ToString().Contains("\r\n\r\n"))
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) break;
            request.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
        string received = "none";
        foreach (var line in request.ToString().Split("\r\n"))
            if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
                received = line["Cookie:".Length..].Trim();

        var bodyBytes = Encoding.UTF8.GetBytes(received);
        var head =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/plain\r\n" +
            "Set-Cookie: srv=ok; Path=/\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n";
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await ssl.WriteAsync(bodyBytes, ct);
        await ssl.FlushAsync(ct);
    }

    private async Task AcceptLoopAsync(Func<TcpClient, CancellationToken, Task> handleClient)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _handlers.Add(Task.Run(async () =>
                {
                    try { await handleClient(client, _cts.Token); }
                    catch { /* handshake/read errors are expected in tests */ }
                    finally { client.Dispose(); }
                }));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Complete the server-side mTLS handshake, requiring a client cert whose thumbprint
    /// matches. A mismatch throws from AuthenticateAsServerAsync (swallowed by the accept loop).</summary>
    private static async Task<SslStream> AuthenticateServerAsync(
        TcpClient client, X509Certificate2 serverCert, string expectedClientThumbprint, CancellationToken ct)
    {
        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
            userCertificateValidationCallback: (_, cert, _, _) =>
                cert is not null &&
                cert.GetCertHashString().Equals(expectedClientThumbprint, StringComparison.OrdinalIgnoreCase));
        var options = new SslServerAuthenticationOptions
        {
            ServerCertificate = serverCert,
            ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        };
        await ssl.AuthenticateAsServerAsync(options, ct);
        return ssl;
    }

    /// <summary>A server that streams a fixed list of Server-Sent Events (text/event-stream) and closes.</summary>
    public static Task<LoopbackMtlsServer> StartSseAsync(
        X509Certificate2 serverCertificate, string expectedClientThumbprint,
        IReadOnlyList<(string? Event, string Data)> events)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new LoopbackMtlsServer(listener, port,
            (client, ct) => HandleSseAsync(client, serverCertificate, expectedClientThumbprint, events, ct));
        return Task.FromResult(server);
    }

    /// <summary>A WebSocket server (over mTLS) that echoes every text/binary frame back and mirrors a close.</summary>
    public static Task<LoopbackMtlsServer> StartWebSocketEchoAsync(
        X509Certificate2 serverCertificate, string expectedClientThumbprint)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new LoopbackMtlsServer(listener, port,
            (client, ct) => HandleWebSocketEchoAsync(client, serverCertificate, expectedClientThumbprint, ct));
        return Task.FromResult(server);
    }

    private static async Task HandleSseAsync(
        TcpClient client, X509Certificate2 serverCert, string expectedClientThumbprint,
        IReadOnlyList<(string? Event, string Data)> events, CancellationToken ct)
    {
        await using var ssl = await AuthenticateServerAsync(client, serverCert, expectedClientThumbprint, ct);

        var buffer = new byte[4096];
        var request = new StringBuilder();
        while (request.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) return;
            request.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }

        var head =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/event-stream\r\n" +
            "Cache-Control: no-cache\r\n" +
            "Connection: close\r\n\r\n";
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await ssl.FlushAsync(ct);

        foreach (var (ev, data) in events)
        {
            var sb = new StringBuilder();
            if (ev is not null) sb.Append("event: ").Append(ev).Append('\n');
            foreach (var dataLine in data.Split('\n')) sb.Append("data: ").Append(dataLine).Append('\n');
            sb.Append('\n');   // blank line dispatches the event
            await ssl.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()), ct);
            await ssl.FlushAsync(ct);
        }
    }

    private sealed record WsFrame(int Opcode, byte[] Payload);

    private static async Task HandleWebSocketEchoAsync(
        TcpClient client, X509Certificate2 serverCert, string expectedClientThumbprint, CancellationToken ct)
    {
        await using var ssl = await AuthenticateServerAsync(client, serverCert, expectedClientThumbprint, ct);

        // --- HTTP upgrade handshake ---
        var buffer = new byte[8192];
        var request = new StringBuilder();
        while (request.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) return;
            request.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
        string? key = null;
        foreach (var line in request.ToString().Split("\r\n"))
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                key = line["Sec-WebSocket-Key:".Length..].Trim();
        if (key is null) return;

        string accept = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(
            Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        string handshake =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(handshake), ct);
        await ssl.FlushAsync(ct);

        // --- frame loop: echo text/binary, mirror a close ---
        while (!ct.IsCancellationRequested)
        {
            var frame = await ReadFrameAsync(ssl, ct);
            if (frame is null) return;                       // connection closed abruptly
            if (frame.Opcode == 0x8)                          // close
            {
                await ssl.WriteAsync(BuildFrame(0x8, frame.Payload), ct);
                await ssl.FlushAsync(ct);
                return;
            }
            if (frame.Opcode is 0x1 or 0x2)                   // text or binary
            {
                await ssl.WriteAsync(BuildFrame(frame.Opcode, frame.Payload), ct);
                await ssl.FlushAsync(ct);
            }
            // ping/pong (0x9 / 0xA) are ignored by this test server
        }
    }

    private static async Task<WsFrame?> ReadFrameAsync(SslStream ssl, CancellationToken ct)
    {
        var h = new byte[2];
        if (!await ReadExactAsync(ssl, h, 2, ct)) return null;
        int opcode = h[0] & 0x0F;
        bool masked = (h[1] & 0x80) != 0;
        long len = h[1] & 0x7F;
        if (len == 126)
        {
            var ext = new byte[2];
            if (!await ReadExactAsync(ssl, ext, 2, ct)) return null;
            len = (ext[0] << 8) | ext[1];
        }
        else if (len == 127)
        {
            var ext = new byte[8];
            if (!await ReadExactAsync(ssl, ext, 8, ct)) return null;
            len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
        }
        byte[] mask = Array.Empty<byte>();
        if (masked)
        {
            mask = new byte[4];
            if (!await ReadExactAsync(ssl, mask, 4, ct)) return null;
        }
        var payload = new byte[len];
        if (len > 0 && !await ReadExactAsync(ssl, payload, (int)len, ct)) return null;
        if (masked) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
        return new WsFrame(opcode, payload);
    }

    private static async Task<bool> ReadExactAsync(SslStream ssl, byte[] buf, int count, CancellationToken ct)
    {
        int off = 0;
        while (off < count)
        {
            int n = await ssl.ReadAsync(buf.AsMemory(off, count - off), ct);
            if (n == 0) return false;
            off += n;
        }
        return true;
    }

    private static byte[] BuildFrame(int opcode, byte[] payload)
    {
        // Server-to-client frames are never masked.
        var header = new List<byte> { (byte)(0x80 | opcode) };
        if (payload.Length < 126) header.Add((byte)payload.Length);
        else if (payload.Length <= 0xFFFF)
        {
            header.Add(126);
            header.Add((byte)(payload.Length >> 8));
            header.Add((byte)(payload.Length & 0xFF));
        }
        else
        {
            header.Add(127);
            for (int i = 7; i >= 0; i--) header.Add((byte)(((long)payload.Length >> (8 * i)) & 0xFF));
        }
        var frame = new byte[header.Count + payload.Length];
        header.CopyTo(frame);
        Array.Copy(payload, 0, frame, header.Count, payload.Length);
        return frame;
    }

    private static async Task HandleClientAsync(
        TcpClient client, X509Certificate2 serverCert, string expectedClientThumbprint,
        string body, string contentType, CancellationToken ct)
    {
        await using var ssl = await AuthenticateServerAsync(client, serverCert, expectedClientThumbprint, ct);

        // Read request headers (until blank line). Body of request is ignored.
        var buffer = new byte[4096];
        var request = new StringBuilder();
        while (!request.ToString().Contains("\r\n\r\n"))
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) break;
            request.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }

        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header =
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n";
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await ssl.WriteAsync(bodyBytes, ct);
        await ssl.FlushAsync(ct);
    }

    private static async Task HandleEchoAsync(
        TcpClient client, X509Certificate2 serverCertificate, string expectedClientThumbprint, CancellationToken ct)
    {
        await using var ssl = await AuthenticateServerAsync(client, serverCertificate, expectedClientThumbprint, ct);

        var buffer = new byte[8192];
        var request = new StringBuilder();
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) break;
            request.Append(Encoding.UTF8.GetString(buffer, 0, n));
            headerEnd = request.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }
        int contentLength = ParseContentLength(request.ToString());
        int bodyHave = headerEnd >= 0 ? request.Length - (headerEnd + 4) : 0;
        while (bodyHave < contentLength)
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) break;
            request.Append(Encoding.UTF8.GetString(buffer, 0, n));
            bodyHave += n;
        }

        var bodyBytes = Encoding.UTF8.GetBytes(request.ToString());
        var head =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/plain\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n";
        await ssl.WriteAsync(Encoding.UTF8.GetBytes(head), ct);
        await ssl.WriteAsync(bodyBytes, ct);
        await ssl.FlushAsync(ct);
    }

    private static async Task HandleRedirectAsync(
        TcpClient client, X509Certificate2 serverCertificate, string expectedClientThumbprint,
        string location, CancellationToken ct)
    {
        await using var ssl = await AuthenticateServerAsync(client, serverCertificate, expectedClientThumbprint, ct);

        var buffer = new byte[4096];
        var request = new StringBuilder();
        while (!request.ToString().Contains("\r\n\r\n"))
        {
            int n = await ssl.ReadAsync(buffer, ct);
            if (n == 0) break;
            request.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
        var head =
            "HTTP/1.1 302 Found\r\n" +
            $"Location: {location}\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n\r\n";
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await ssl.FlushAsync(ct);
    }

    private static int ParseContentLength(string requestText)
    {
        foreach (var line in requestText.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line["Content-Length:".Length..].Trim(), out var len))
                return len;
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _listener.Stop();
        try { await _acceptLoop; } catch { /* ignore */ }
        try { await Task.WhenAll(_handlers.ToArray()); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
