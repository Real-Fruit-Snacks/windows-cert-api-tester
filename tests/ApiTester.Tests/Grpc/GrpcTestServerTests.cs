using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;
using Certapi.Test;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Reflection.V1Alpha;

namespace ApiTester.Tests.Grpc;

/// <summary>De-risks the harness (Kestrel + HTTP/2 + server reflection + mutual TLS) before any
/// ApiTester.Grpc client code exists, using the generated <see cref="Echo.EchoClient"/> stub
/// directly rather than a stand-in — so a later slice's GrpcCaller is trusted to be exercised
/// against a genuine server once it lands.</summary>
public class GrpcTestServerTests
{
    private static GrpcChannel CreatePlaintextChannel(string address)
    {
        // No AppContext switch needed: on .NET 9, SocketsHttpHandler negotiates HTTP/2 over
        // plaintext (h2c) whenever the request pins HTTP/2 exactly, which GrpcChannel already does.
        // (Confirmed empirically for slice D-3: this repo's own .NET Core 3.x-era comment claiming
        // otherwise no longer holds.)
        return GrpcChannel.ForAddress(address);
    }

    [Fact]
    public async Task Unary_call_round_trips_text_and_count()
    {
        await using var server = await GrpcTestServer.StartAsync();
        using var channel = CreatePlaintextChannel(server.Address);
        var client = new Echo.EchoClient(channel);

        var reply = await client.UnaryAsync(new EchoRequest { Text = "hello", Count = 3 });

        Assert.Equal("hello", reply.Text);
        Assert.Equal(3, reply.Count);
    }

    [Fact]
    public async Task ServerStream_yields_one_reply_per_requested_count()
    {
        await using var server = await GrpcTestServer.StartAsync();
        using var channel = CreatePlaintextChannel(server.Address);
        var client = new Echo.EchoClient(channel);

        using var call = client.ServerStream(new EchoRequest { Text = "s", Count = 3 });
        var indexes = new List<int>();
        while (await call.ResponseStream.MoveNext(CancellationToken.None))
        {
            indexes.Add(call.ResponseStream.Current.MessageIndex);
        }

        Assert.Equal(new[] { 0, 1, 2 }, indexes);
    }

    [Fact]
    public async Task Failing_method_surfaces_the_servers_non_ok_status()
    {
        await using var server = await GrpcTestServer.StartAsync();
        using var channel = CreatePlaintextChannel(server.Address);
        var client = new Echo.EchoClient(channel);

        var call = client.FailingAsync(new EchoRequest());
        var ex = await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, ex.Status.StatusCode);
        Assert.Equal("test denied", ex.Status.Detail);
    }

    [Fact]
    public async Task Request_metadata_is_observed_by_the_server()
    {
        await using var server = await GrpcTestServer.StartAsync();
        using var channel = CreatePlaintextChannel(server.Address);
        var client = new Echo.EchoClient(channel);

        var headers = new Metadata { { "x-test-key", "hello" } };
        var reply = await client.UnaryAsync(new EchoRequest { Text = "x" }, headers);

        Assert.Equal("hello", reply.ObservedMetadata);
    }

    [Fact]
    public async Task Mutual_tls_presents_the_clients_certificate_to_the_server()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);

        await using var server = await GrpcTestServer.StartTlsAsync(serverCert, requireClientCertificate: true);

        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { clientCert },
                // The server's certificate is self-signed for this test; validating it is not what
                // is under test here — presenting the client certificate is.
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };
        using var channel = GrpcChannel.ForAddress(server.Address, new GrpcChannelOptions { HttpHandler = handler });
        var client = new Echo.EchoClient(channel);

        var reply = await client.UnaryAsync(new EchoRequest { Text = "mtls" });

        Assert.Equal(clientCert.Thumbprint, reply.ObservedClientCertThumbprint);
    }

    [Fact]
    public async Task BidiStream_echoes_each_message_as_it_arrives()
    {
        await using var server = await GrpcTestServer.StartAsync();
        using var channel = CreatePlaintextChannel(server.Address);
        var client = new Echo.EchoClient(channel);

        // A hang-guard only (ruling 10): if the server ever buffered every request before
        // replying, the awaits below would never complete and this bounds how long that takes to
        // observe, rather than asserting anything about elapsed time.
        using var hang = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var call = client.BidiStream();

        await call.RequestStream.WriteAsync(new EchoRequest { Text = "a" });
        Assert.True(await call.ResponseStream.MoveNext(hang.Token));
        var first = call.ResponseStream.Current;

        // Only now, after the first response has arrived, is the second request written. If the
        // server buffered requests instead of echoing as they arrive, this line would never be
        // reached because the MoveNext above would hang until the hang-guard fires.
        await call.RequestStream.WriteAsync(new EchoRequest { Text = "b" });
        Assert.True(await call.ResponseStream.MoveNext(hang.Token));
        var second = call.ResponseStream.Current;

        await call.RequestStream.CompleteAsync();
        Assert.False(await call.ResponseStream.MoveNext(hang.Token));

        Assert.Equal(new[] { "a", "b" }, new[] { first.Text, second.Text });
        Assert.Equal(0, first.MessageIndex);
        Assert.Equal(1, second.MessageIndex);
    }

    [Fact]
    public async Task ClientStreamThenFail_surfaces_the_servers_non_ok_status()
    {
        await using var server = await GrpcTestServer.StartAsync();
        using var channel = CreatePlaintextChannel(server.Address);
        var client = new Echo.EchoClient(channel);

        using var call = client.ClientStreamThenFail();

        // The server ends the call as soon as its first message arrives, so a write can race the
        // server's failure and throw RpcException itself; that race is exactly what the later
        // client-streaming slice must tolerate, so it is caught and ignored here too.
        try
        {
            await call.RequestStream.WriteAsync(new EchoRequest { Text = "one" });
            await call.RequestStream.WriteAsync(new EchoRequest { Text = "two" });
        }
        catch (RpcException)
        {
        }

        var ex = await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, ex.Status.StatusCode);
        Assert.Equal("client stream refused", ex.Status.Detail);
    }

    [Fact]
    public async Task BidiStreamThenFail_writes_one_reply_then_fails()
    {
        await using var server = await GrpcTestServer.StartAsync();
        using var channel = CreatePlaintextChannel(server.Address);
        var client = new Echo.EchoClient(channel);

        using var call = client.BidiStreamThenFail();

        await call.RequestStream.WriteAsync(new EchoRequest { Text = "only" });
        await call.RequestStream.CompleteAsync();

        var replies = new List<EchoReply>();
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            while (await call.ResponseStream.MoveNext(CancellationToken.None))
            {
                replies.Add(call.ResponseStream.Current);
            }
        });

        Assert.Single(replies);
        Assert.Equal(StatusCode.ResourceExhausted, ex.Status.StatusCode);
        Assert.Equal("duplex cut short", ex.Status.Detail);
    }

    [Fact]
    public async Task Server_reflection_lists_the_echo_service()
    {
        await using var server = await GrpcTestServer.StartAsync();
        using var channel = CreatePlaintextChannel(server.Address);
        var client = new ServerReflection.ServerReflectionClient(channel);

        using var call = client.ServerReflectionInfo();
        await call.RequestStream.WriteAsync(new ServerReflectionRequest { ListServices = "" });
        await call.RequestStream.CompleteAsync();

        var names = new List<string>();
        while (await call.ResponseStream.MoveNext(CancellationToken.None))
        {
            foreach (var svc in call.ResponseStream.Current.ListServicesResponse.Service)
            {
                names.Add(svc.Name);
            }
        }

        Assert.Contains("certapi.test.Echo", names);
    }
}
