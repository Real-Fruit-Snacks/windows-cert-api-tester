using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace ApiTester.Core;

/// <summary>Transport for <see cref="MockServer"/>: plain HTTP, HTTPS, or HTTPS requiring a client
/// certificate (mTLS).</summary>
public enum MockTlsMode { Http, Https, Mtls }

/// <summary>Generates the self-signed certificates a TLS/mTLS mock server uses and writes the public
/// certs (and, for mTLS, a ready-to-use client .pfx) to a folder. Shared by the CLI and the GUI so
/// both produce identical files.</summary>
public static class MockCertificates
{
    public sealed record Generated(X509Certificate2 ServerCertificate, IReadOnlyList<string> WrittenFiles);

    public static Generated Generate(MockTlsMode mode, string certDir)
    {
        Directory.CreateDirectory(certDir);
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("certapi mock CA");
        var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, serverAuth: true, clientAuth: false, dnsNames: new[] { "localhost" });

        var files = new List<string>();
        void Write(string name, byte[] bytes)
        {
            string path = Path.Combine(certDir, name);
            File.WriteAllBytes(path, bytes);
            files.Add(path);
        }

        Write("mock-ca.cer", ca.Export(X509ContentType.Cert));
        Write("mock-server.cer", serverCert.Export(X509ContentType.Cert));
        if (mode == MockTlsMode.Mtls)
        {
            using var client = SelfSignedCertificateFactory.CreateSignedCertificate(
                "certapi mock client", ca, serverAuth: false, clientAuth: true);
            Write("mock-client.pfx", client.Export(X509ContentType.Pfx));
        }
        return new Generated(serverCert, files);
    }
}

/// <summary>A one-line record of a handled request, for the console log.</summary>
public sealed record MockRequestLog(string Method, string Path, int Status, string? ClientCertSubject);

/// <summary>
/// A small standing local test server you can fire requests at — the persistent counterpart to the
/// one-shot self-test. It reflects each request back as JSON (method, path, query, headers, body, and
/// the presented client certificate under mTLS), and serves a few fixed routes so the app's own
/// features can be exercised end-to-end without a real API:
/// <list type="bullet">
///   <item><c>/status/{code}</c> — responds with that HTTP status.</item>
///   <item><c>/sse</c> — a short <c>text/event-stream</c>.</item>
///   <item><c>/token</c> — an OAuth 2.0 token response.</item>
///   <item><c>/windows-auth</c> — a 401 NTLM challenge, then an authenticated response.</item>
///   <item><c>/cookie-auth</c> — sets a session cookie, then reports authenticated once it returns.</item>
///   <item><c>Upgrade: websocket</c> (any path) — a WebSocket echo.</item>
///   <item>anything else — the JSON echo.</item>
/// </list>
/// Built on <see cref="TcpListener"/> + <see cref="SslStream"/> so TLS and mTLS work on loopback with
/// no netsh URL/cert reservation. One request per connection (<c>Connection: close</c>).
/// </summary>
public sealed class MockServer : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentBag<Task> _handlers = new();
    private readonly MockTlsMode _tls;
    private readonly X509Certificate2? _serverCert;
    private readonly Action<MockRequestLog>? _onRequest;
    private bool _disposed;

    public string BaseUrl { get; }
    public int Port { get; }

    private MockServer(TcpListener listener, int port, MockTlsMode tls,
        X509Certificate2? serverCert, Action<MockRequestLog>? onRequest)
    {
        _listener = listener;
        Port = port;
        _tls = tls;
        _serverCert = serverCert;
        _onRequest = onRequest;
        BaseUrl = $"{(tls == MockTlsMode.Http ? "http" : "https")}://127.0.0.1:{port}/";
        _acceptLoop = AcceptLoopAsync();
    }

    /// <summary>Start listening. <paramref name="port"/> 0 picks a free port (see <see cref="Port"/>).
    /// A server certificate is required for HTTPS/mTLS.</summary>
    public static MockServer Start(int port, MockTlsMode tls,
        X509Certificate2? serverCert = null, Action<MockRequestLog>? onRequest = null)
    {
        if (tls != MockTlsMode.Http && serverCert is null)
            throw new ArgumentException("A server certificate is required for HTTPS/mTLS.", nameof(serverCert));

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        int actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new MockServer(listener, actualPort, tls, serverCert, onRequest);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _handlers.Add(Task.Run(async () =>
                {
                    try { await HandleAsync(client, _cts.Token); }
                    catch { /* a broken connection must not take the server down */ }
                    finally { client.Dispose(); }
                }));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        Stream stream = client.GetStream();
        string? clientCertSubject = null;

        if (_tls != MockTlsMode.Http)
        {
            // Accept any presented client cert (ignore chain trust), but under mTLS a cert must be
            // presented — reject the handshake when none is (otherwise --mtls wouldn't require one).
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, cert, _, _) => _tls != MockTlsMode.Mtls || cert is not null);
            var options = new SslServerAuthenticationOptions
            {
                ServerCertificate = _serverCert,
                ClientCertificateRequired = _tls == MockTlsMode.Mtls,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            };
            await ssl.AuthenticateAsServerAsync(options, ct);
            if (ssl.RemoteCertificate is not null)
            {
                using var c = new X509Certificate2(ssl.RemoteCertificate);
                clientCertSubject = c.Subject;
            }
            stream = ssl;
        }

        await using (stream)
        {
            var request = await ReadRequestAsync(stream, ct);
            if (request is null) return;
            var (method, target, headers, body) = request.Value;
            string path = target.Split('?')[0];
            string query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";

            // WebSocket upgrade (any path).
            if (headers.TryGetValue("Upgrade", out var up) && up.Contains("websocket", StringComparison.OrdinalIgnoreCase))
            {
                await HandleWebSocketAsync(stream, headers, ct);
                _onRequest?.Invoke(new(method, path, 101, clientCertSubject));
                return;
            }

            // Windows Integrated Auth (Negotiate/NTLM) challenge on this route.
            if (path.Equals("/windows-auth", StringComparison.Ordinal))
            {
                await HandleWindowsAuthAsync(stream, method, headers, clientCertSubject, ct);
                return;
            }

            int status;
            if (path.StartsWith("/status/", StringComparison.Ordinal) &&
                int.TryParse(path["/status/".Length..], out var code) && code is >= 100 and <= 599)
            {
                status = code;
                await WriteResponseAsync(stream, status, "application/json",
                    $"{{\"status\":{code},\"server\":\"certapi mock\"}}", ct);
            }
            else if (path.Equals("/sse", StringComparison.Ordinal) || path.StartsWith("/sse/", StringComparison.Ordinal))
            {
                status = 200;
                await WriteSseAsync(stream, ct);
            }
            else if (path.Equals("/token", StringComparison.Ordinal) || path.StartsWith("/token/", StringComparison.Ordinal))
            {
                status = 200;
                await WriteResponseAsync(stream, 200, "application/json",
                    "{\"access_token\":\"mock-access-token\",\"token_type\":\"Bearer\"," +
                    "\"expires_in\":3600,\"refresh_token\":\"mock-refresh-token\",\"scope\":\"mock\"}", ct);
            }
            else if (path.Equals("/cookie-auth", StringComparison.Ordinal))
            {
                // Emulates a cookie-session-protected endpoint: hands out a cookie until the client
                // presents it, then reports the request as authenticated. Lets session capture be
                // exercised end to end.
                status = 200;
                bool hasCookie = headers.TryGetValue("Cookie", out var cookie) &&
                                 cookie.Contains("MOCKSID=ok", StringComparison.Ordinal);
                if (hasCookie)
                    await WriteResponseAsync(stream, 200, "application/json",
                        "{\"server\":\"certapi mock\",\"authenticated\":\"cookie\"}", ct);
                else
                    await WriteResponseAsync(stream, 200, "application/json",
                        "{\"server\":\"certapi mock\",\"authenticated\":false}", ct,
                        setCookie: "MOCKSID=ok; Path=/");
            }
            else
            {
                status = 200;
                await WriteResponseAsync(stream, 200, "application/json",
                    BuildEcho(method, path, query, headers, body, clientCertSubject), ct);
            }

            _onRequest?.Invoke(new(method, path, status, clientCertSubject));
        }
    }

    private static string BuildEcho(string method, string path, string query,
        IReadOnlyDictionary<string, string> headers, string body, string? clientCertSubject)
    {
        var obj = new Dictionary<string, object?>
        {
            ["server"] = "certapi mock",
            ["method"] = method,
            ["path"] = path,
            ["query"] = query.Length > 0 ? query : null,
            ["headers"] = headers.ToDictionary(h => h.Key, h => h.Value),
            ["body"] = body.Length > 0 ? body : null,
            ["clientCertificate"] = clientCertSubject
        };
        // Relaxed escaping keeps the echoed body readable (\" not ") — this is a dev tool.
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private static async Task WriteResponseAsync(Stream stream, int status, string contentType, string body,
        CancellationToken ct, string? setCookie = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head =
            $"HTTP/1.1 {status} {ReasonPhrase(status)}\r\n" +
            $"Content-Type: {contentType}; charset=utf-8\r\n" +
            $"Content-Length: {bytes.Length}\r\n" +
            (setCookie is null ? "" : $"Set-Cookie: {setCookie}\r\n") +
            "Access-Control-Allow-Origin: *\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    // Emulates a Windows-auth-protected endpoint: challenges with 401 WWW-Authenticate: NTLM until
    // the client presents an Authorization header, then accepts it (short-circuiting the handshake).
    // The challenge is sent keep-alive so the client can continue on the same connection.
    private async Task HandleWindowsAuthAsync(Stream stream, string method,
        Dictionary<string, string> headers, string? clientCertSubject, CancellationToken ct)
    {
        var currentHeaders = headers;
        string currentMethod = method;
        for (int leg = 0; leg < 4; leg++)   // bound the handshake
        {
            if (currentHeaders.TryGetValue("Authorization", out var auth) && auth.Length > 0)
            {
                string scheme = auth.Split(' ')[0];
                await WriteResponseAsync(stream, 200, "application/json",
                    $"{{\"server\":\"certapi mock\",\"authenticated\":\"{scheme}\",\"method\":\"{currentMethod}\"}}", ct);
                _onRequest?.Invoke(new(currentMethod, "/windows-auth", 200, clientCertSubject));
                return;
            }

            // Keep-alive 401 (no Connection: close) so the handshake stays on this connection.
            var head = "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: NTLM\r\nContent-Length: 0\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            await stream.FlushAsync(ct);
            _onRequest?.Invoke(new(currentMethod, "/windows-auth", 401, clientCertSubject));

            var next = await ReadRequestAsync(stream, ct);
            if (next is null) return;
            currentMethod = next.Value.Method;
            currentHeaders = next.Value.Headers;
        }
    }

    private static async Task WriteSseAsync(Stream stream, CancellationToken ct)
    {
        var head =
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n" +
            "Cache-Control: no-cache\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.FlushAsync(ct);
        for (int i = 1; i <= 3; i++)
        {
            var evt = $"event: tick\ndata: {{\"n\":{i},\"server\":\"certapi mock\"}}\n\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(evt), ct);
            await stream.FlushAsync(ct);
            try { await Task.Delay(150, ct); } catch (OperationCanceledException) { break; }
        }
    }

    // ---------- WebSocket echo ----------

    private async Task HandleWebSocketAsync(Stream stream, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key)) return;
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        var handshake =
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), ct);
        await stream.FlushAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            var frame = await ReadFrameAsync(stream, ct);
            if (frame is null) return;
            if (frame.Value.Opcode == 0x8) { await stream.WriteAsync(BuildFrame(0x8, frame.Value.Payload), ct); await stream.FlushAsync(ct); return; }
            if (frame.Value.Opcode is 0x1 or 0x2)
            {
                await stream.WriteAsync(BuildFrame(frame.Value.Opcode, frame.Value.Payload), ct);
                await stream.FlushAsync(ct);
            }
        }
    }

    private static async Task<(int Opcode, byte[] Payload)?> ReadFrameAsync(Stream s, CancellationToken ct)
    {
        var h = new byte[2];
        if (!await ReadExactAsync(s, h, 2, ct)) return null;
        int opcode = h[0] & 0x0F;
        bool masked = (h[1] & 0x80) != 0;
        long len = h[1] & 0x7F;
        if (len == 126) { var e = new byte[2]; if (!await ReadExactAsync(s, e, 2, ct)) return null; len = (e[0] << 8) | e[1]; }
        else if (len == 127) { var e = new byte[8]; if (!await ReadExactAsync(s, e, 8, ct)) return null; len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | e[i]; }
        byte[] mask = Array.Empty<byte>();
        if (masked) { mask = new byte[4]; if (!await ReadExactAsync(s, mask, 4, ct)) return null; }
        var payload = new byte[len];
        if (len > 0 && !await ReadExactAsync(s, payload, (int)len, ct)) return null;
        if (masked) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
        return (opcode, payload);
    }

    private static byte[] BuildFrame(int opcode, byte[] payload)
    {
        var header = new List<byte> { (byte)(0x80 | opcode) };
        if (payload.Length < 126) header.Add((byte)payload.Length);
        else if (payload.Length <= 0xFFFF) { header.Add(126); header.Add((byte)(payload.Length >> 8)); header.Add((byte)(payload.Length & 0xFF)); }
        else { header.Add(127); for (int i = 7; i >= 0; i--) header.Add((byte)(((long)payload.Length >> (8 * i)) & 0xFF)); }
        var frame = new byte[header.Count + payload.Length];
        header.CopyTo(frame);
        Array.Copy(payload, 0, frame, header.Count, payload.Length);
        return frame;
    }

    // ---------- request parsing ----------

    private static async Task<(string Method, string Target, Dictionary<string, string> Headers, string Body)?>
        ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var acc = new List<byte>();
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await stream.ReadAsync(buffer, ct);
            if (n == 0) break;
            acc.AddRange(new ArraySegment<byte>(buffer, 0, n));
            headerEnd = IndexOfHeaderEnd(acc);
            if (acc.Count > 1_048_576) break;   // 1 MB header guard
        }
        if (headerEnd < 0) return null;

        string headerText = Encoding.ASCII.GetString(acc.ToArray(), 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;
        string method = requestLine[0];
        string target = requestLine[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0) headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        int contentLength = headers.TryGetValue("Content-Length", out var clRaw) && int.TryParse(clRaw, out var cl) ? cl : 0;
        var bodyBytes = new List<byte>(acc.Skip(headerEnd + 4));
        while (bodyBytes.Count < contentLength)
        {
            int n = await stream.ReadAsync(buffer, ct);
            if (n == 0) break;
            bodyBytes.AddRange(new ArraySegment<byte>(buffer, 0, n));
        }
        string body = Encoding.UTF8.GetString(bodyBytes.ToArray());
        return (method, target, headers, body);
    }

    private static int IndexOfHeaderEnd(List<byte> data)
    {
        for (int i = 0; i + 3 < data.Count; i++)
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                return i;
        return -1;
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        int off = 0;
        while (off < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(off, count - off), ct);
            if (n == 0) return false;
            off += n;
        }
        return true;
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK", 201 => "Created", 202 => "Accepted", 204 => "No Content",
        301 => "Moved Permanently", 302 => "Found", 304 => "Not Modified",
        400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
        405 => "Method Not Allowed", 409 => "Conflict", 418 => "I'm a teapot", 429 => "Too Many Requests",
        500 => "Internal Server Error", 502 => "Bad Gateway", 503 => "Service Unavailable",
        _ => "Status"
    };

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
