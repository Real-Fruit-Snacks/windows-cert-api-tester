using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ApiTester.Tests;

/// <summary>A minimal loopback HTTP CONNECT proxy for tests. It tunnels bytes without touching the
/// TLS session, and counts what it was asked to reach — which is the only honest way to prove that
/// --proxy and --no-proxy take genuinely different paths rather than both falling through to a
/// direct connection.</summary>
public sealed class LoopbackConnectProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentBag<Task> _handlers = new();
    /// <summary>Every socket this proxy owns, so disposal can break reads that no cancellation token
    /// can interrupt (a pending socket receive ends when the socket closes, not when a token trips).</summary>
    private readonly ConcurrentBag<TcpClient> _sockets = new();
    private readonly object _gate = new();
    private readonly List<string> _targets = new();
    private readonly List<string> _proxyAuthorizations = new();
    /// <summary>The Proxy-Authorization value a CONNECT must carry, or null to tunnel unconditionally.</summary>
    private readonly string? _requiredAuthorization;
    private int _connectCount;
    private bool _disposed;

    /// <summary>The proxy's own address, e.g. http://127.0.0.1:51234 — pass this as ProxyUrl.</summary>
    public string Url { get; }

    /// <summary>How many CONNECT tunnels have been established.</summary>
    public int ConnectCount => Volatile.Read(ref _connectCount);

    /// <summary>The "host:port" of each CONNECT target, in order.</summary>
    public IReadOnlyList<string> Targets
    {
        get { lock (_gate) return _targets.ToArray(); }
    }

    /// <summary>The Proxy-Authorization header values received, if any — lets a test prove
    /// --proxy-user credentials actually reached the proxy.</summary>
    public IReadOnlyList<string> ProxyAuthorizations
    {
        get { lock (_gate) return _proxyAuthorizations.ToArray(); }
    }

    private LoopbackConnectProxy(TcpListener listener, int port, string? requiredAuthorization)
    {
        _listener = listener;
        _requiredAuthorization = requiredAuthorization;
        Url = $"http://127.0.0.1:{port}";
        _acceptLoop = AcceptLoopAsync();
    }

    public static Task<LoopbackConnectProxy> StartAsync() => Start(requiredAuthorization: null);

    /// <summary>A proxy that answers an unauthenticated CONNECT with 407 Proxy Authentication
    /// Required. A client only ever sends credentials once it has been challenged, so a proxy that
    /// never challenges cannot show whether --proxy-user did anything.</summary>
    public static Task<LoopbackConnectProxy> StartRequiringBasicAuthAsync(string username, string password) =>
        Start("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

    private static Task<LoopbackConnectProxy> Start(string? requiredAuthorization)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return Task.FromResult(new LoopbackConnectProxy(listener, port, requiredAuthorization));
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _sockets.Add(client);
                _handlers.Add(Task.Run(async () =>
                {
                    try { await HandleAsync(client, _cts.Token); }
                    catch { /* tunnel read/write errors are expected in tests */ }
                    finally { client.Dispose(); }
                }));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        var downstream = client.GetStream();

        string? head = await ReadHeadAsync(downstream, ct);
        if (head is null) return;

        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        // This proxy exists only to prove tunnelling; anything else is refused rather than guessed at.
        if (requestLine.Length < 2 || !requestLine[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            await RespondAsync(downstream, "HTTP/1.1 405 Method Not Allowed\r\n\r\n", ct);
            return;
        }

        string target = requestLine[1];
        string? authorization = null;
        foreach (var line in lines)
            if (line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
                authorization = line["Proxy-Authorization:".Length..].Trim();
        if (authorization is not null)
        {
            lock (_gate) _proxyAuthorizations.Add(authorization);
        }

        if (_requiredAuthorization is not null && authorization != _requiredAuthorization)
        {
            await RespondAsync(downstream,
                "HTTP/1.1 407 Proxy Authentication Required\r\n" +
                "Proxy-Authenticate: Basic realm=\"test\"\r\n" +
                "Content-Length: 0\r\n" +
                "Connection: close\r\n\r\n", ct);
            return;
        }

        int colon = target.LastIndexOf(':');
        if (colon < 1 || !int.TryParse(target[(colon + 1)..], out int port))
        {
            await RespondAsync(downstream, "HTTP/1.1 400 Bad Request\r\n\r\n", ct);
            return;
        }

        var upstreamClient = new TcpClient { NoDelay = true };
        _sockets.Add(upstreamClient);
        using (upstreamClient)
        {
            await upstreamClient.ConnectAsync(target[..colon], port, ct);
            // Counted only once the tunnel really stands up, so a test asserting on it is asserting
            // that the bytes had somewhere to go.
            lock (_gate) _targets.Add(target);
            Interlocked.Increment(ref _connectCount);
            await RespondAsync(downstream, "HTTP/1.1 200 Connection Established\r\n\r\n", ct);

            var upstream = upstreamClient.GetStream();
            var toServer = downstream.CopyToAsync(upstream, ct);
            var toClient = upstream.CopyToAsync(downstream, ct);
            await Task.WhenAny(toServer, toClient);

            // One side is done; half-close both so the other peer sees an orderly end of stream
            // instead of a reset that could truncate a response mid-flight.
            Shutdown(client);
            Shutdown(upstreamClient);
            try { await Task.WhenAll(toServer, toClient).WaitAsync(TimeSpan.FromSeconds(5), ct); }
            catch { /* a peer that never closes must not hang the test run */ }
        }
    }

    private static void Shutdown(TcpClient client)
    {
        try { client.Client.Shutdown(SocketShutdown.Send); } catch { /* already gone */ }
    }

    private static async Task RespondAsync(NetworkStream stream, string response, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Read the request line and headers, one byte at a time, stopping exactly at the blank
    /// line. Reading in blocks could swallow the first tunnelled bytes, which is precisely the data
    /// this proxy must not touch. Null when the peer closed first.</summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (head.Length < 8192)
        {
            int n = await stream.ReadAsync(one, ct);
            if (n == 0) return null;
            head.Append((char)one[0]);
            if (head.Length >= 4 &&
                head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n')
                return head.ToString(0, head.Length - 4);
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _listener.Stop();
        foreach (var socket in _sockets) { try { socket.Dispose(); } catch { /* ignore */ } }
        try { await _acceptLoop; } catch { /* ignore */ }
        try { await Task.WhenAll(_handlers.ToArray()); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
