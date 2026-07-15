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

    private static async Task HandleClientAsync(
        TcpClient client, X509Certificate2 serverCert, string expectedClientThumbprint,
        string body, string contentType, CancellationToken ct)
    {
        await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
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
