using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>End-to-end coverage of `certapi serve` as a browser sees it: several mounted upstreams,
/// CORS, cookie and Location rewriting and WebSocket relaying, all through a real listener talking to
/// real loopback mTLS upstreams. Every assertion is on what a client actually receives on
/// http://127.0.0.1:&lt;port&gt; or on the raw request an echo upstream reports it was sent.</summary>
public class ServeBrowserTests
{
    /// <summary>Every network wait is bounded, so a gateway that never answers fails the test rather
    /// than hanging the suite.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static (X509Certificate2 ca, X509Certificate2 server, X509Certificate2 client) Certs()
    {
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        var server = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        var client = SelfSignedCertificateFactory.CreateSignedCertificate("GatewayClient", ca, false, true);
        return (ca, server, client);
    }

    /// <summary>A client that leaves the gateway's answer alone: redirects are not followed and
    /// cookies are not swallowed by a container, so Location and Set-Cookie can be asserted as sent.</summary>
    private static HttpClient NewClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Limit };

    /// <summary>`serve` running on a background thread with its real gateway, so the routes and
    /// browser options under test are the ones the command built from its own flags.</summary>
    private sealed class ServeHost : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task<int> _run;

        public int Port { get; }
        public string Origin => $"http://127.0.0.1:{Port}";

        private ServeHost(X509Certificate2 clientCert, IEnumerable<string> extraArgs)
        {
            Port = FreePort();
            var services = new CliServices
            {
                Cancel = _cts.Token,
                ListCertificates = _ => new[]
                {
                    new CertificateInfo
                    {
                        Subject = "CN=GatewayClient", Issuer = "CN=CA", Thumbprint = clientCert.Thumbprint!,
                        NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                        HasClientAuthEku = true, Certificate = clientCert
                    }
                }
            };
            var args = new List<string> { "serve", "--port", Port.ToString(),
                                          "--cert", "GatewayClient", "--insecure", "-q" };
            args.AddRange(extraArgs);
            _run = Task.Run(() => CliApp.Run(args.ToArray(), TextWriter.Null, TextWriter.Null, services: services));
        }

        public static ServeHost Start(X509Certificate2 clientCert, params string[] extraArgs) =>
            new(clientCert, extraArgs);

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await _run.WaitAsync(Limit); } catch { /* the test's assertions are the verdict */ }
            _cts.Dispose();
        }
    }

    /// <summary>Every value of a response header, in order — the count matters as much as the value
    /// when a duplicate is the browser-breaking failure.</summary>
    private static string[] HeaderValues(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.ToArray() : Array.Empty<string>();

    /// <summary>Retry while the listener finishes binding.</summary>
    private static async Task<T> Poll<T>(Func<Task<T>> action)
    {
        Exception? last = null;
        for (int i = 0; i < 50; i++)
        {
            try { return await action(); }
            catch (Exception ex) { last = ex; await Task.Delay(100); }
        }
        throw last!;
    }

    [Fact]
    public async Task Each_mounted_upstream_receives_the_request_with_its_prefix_stripped()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var auth = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client,
                "--upstream", $"/api={api.BaseUrl}", "--upstream", $"/auth={auth.BaseUrl}");

            using var http = NewClient();
            string apiEcho = await Poll(() => http.GetStringAsync($"{serve.Origin}/api/orders?page=2"));
            string authEcho = await http.GetStringAsync($"{serve.Origin}/auth/token");

            // The echo body is the raw request the upstream received: the request line shows the
            // mount point was stripped, and the Host header shows which upstream received it.
            Assert.Contains("GET /orders?page=2 HTTP/1.1", apiEcho);
            Assert.Contains($"Host: {new Uri(api.BaseUrl).Authority}", apiEcho);
            Assert.Contains("GET /token HTTP/1.1", authEcho);
            Assert.Contains($"Host: {new Uri(auth.BaseUrl).Authority}", authEcho);
        }
    }

    [Fact]
    public async Task A_path_that_matches_no_mounted_prefix_is_a_404_and_reaches_no_upstream()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client, "--upstream", $"/api={api.BaseUrl}");

            using var http = NewClient();
            var resp = await Poll(() => http.GetAsync($"{serve.Origin}/nowhere/orders"));
            string body = await resp.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            // An echo upstream answers with the request it was sent, so an answer with no request
            // line in it is an answer no upstream produced.
            Assert.DoesNotContain("GET /", body);
        }
    }

    [Fact]
    public async Task A_preflight_is_answered_by_the_gateway_and_never_forwarded()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            // Nothing listens on this port, so an answer that is not a 502 is one the gateway wrote
            // itself without contacting anything.
            string unreachable = $"https://127.0.0.1:{FreePort()}";
            await using var serve = ServeHost.Start(client, "--upstream", $"/={unreachable}", "--cors");

            using var http = NewClient();
            HttpRequestMessage Preflight()
            {
                var m = new HttpRequestMessage(HttpMethod.Options, $"{serve.Origin}/orders");
                m.Headers.Add("Origin", "https://app.example");
                m.Headers.Add("Access-Control-Request-Method", "PUT");
                m.Headers.Add("Access-Control-Request-Headers", "authorization, x-trace");
                return m;
            }
            var resp = await Poll(() => http.SendAsync(Preflight()));

            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
            Assert.Equal(new[] { "https://app.example" }, HeaderValues(resp, "Access-Control-Allow-Origin"));
            Assert.Equal(new[] { "PUT" }, HeaderValues(resp, "Access-Control-Allow-Methods"));
            Assert.Equal(new[] { "authorization, x-trace" }, HeaderValues(resp, "Access-Control-Allow-Headers"));
            Assert.Equal(new[] { "true" }, HeaderValues(resp, "Access-Control-Allow-Credentials"));

            // A real request on the same route does reach for the upstream, and fails — which is
            // what makes the 204 above proof that the preflight went nowhere.
            var forwarded = await http.GetAsync($"{serve.Origin}/orders");
            Assert.Equal(HttpStatusCode.BadGateway, forwarded.StatusCode);
        }
    }

    [Fact]
    public async Task An_origin_outside_an_explicit_cors_allowlist_is_refused()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client,
                "--upstream", $"/={api.BaseUrl}", "--cors", "https://app.example");

            using var http = NewClient();
            HttpRequestMessage Preflight(string origin)
            {
                var m = new HttpRequestMessage(HttpMethod.Options, $"{serve.Origin}/orders");
                m.Headers.Add("Origin", origin);
                m.Headers.Add("Access-Control-Request-Method", "GET");
                return m;
            }

            var listed = await Poll(() => http.SendAsync(Preflight("https://app.example")));
            var stranger = await http.SendAsync(Preflight("https://evil.example"));

            Assert.Equal(HttpStatusCode.NoContent, listed.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
            Assert.Empty(HeaderValues(stranger, "Access-Control-Allow-Origin"));
        }
    }

    [Fact]
    public async Task A_response_carries_exactly_one_allow_origin_even_when_the_upstream_sent_its_own()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartWithHeadersAsync(
                server, client.Thumbprint!,
                new[]
                {
                    new KeyValuePair<string, string>(
                        "Access-Control-Allow-Origin", "https://upstream.example")
                },
                responseBody: "ok", responseContentType: "text/plain");
            await using var serve = ServeHost.Start(client, "--upstream", $"/={api.BaseUrl}", "--cors");

            using var http = NewClient();
            HttpRequestMessage FromApp()
            {
                var m = new HttpRequestMessage(HttpMethod.Get, $"{serve.Origin}/orders");
                m.Headers.Add("Origin", "https://app.example");
                return m;
            }
            var resp = await Poll(() => http.SendAsync(FromApp()));

            // Two allow-origin values is a hard browser failure, not a warning: the upstream's own
            // must be gone, and exactly the caller's origin left in its place.
            Assert.Equal(new[] { "https://app.example" }, HeaderValues(resp, "Access-Control-Allow-Origin"));
            Assert.Equal(new[] { "true" }, HeaderValues(resp, "Access-Control-Allow-Credentials"));
        }
    }

    [Fact]
    public async Task An_upstream_cookie_is_rewritten_to_one_the_loopback_browser_can_store()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartWithHeadersAsync(
                server, client.Thumbprint!,
                new[]
                {
                    new KeyValuePair<string, string>(
                        "Set-Cookie", "sid=abc; Domain=api.internal; Path=/; Secure; HttpOnly; SameSite=None")
                },
                responseBody: "ok", responseContentType: "text/plain");
            await using var serve = ServeHost.Start(client, "--upstream", $"/={api.BaseUrl}", "--rewrite-cookies");

            using var http = NewClient();
            var resp = await Poll(() => http.GetAsync($"{serve.Origin}/orders"));

            // Domain scopes the cookie to a host the browser is not talking to, Secure means nothing
            // is stored over plaintext loopback, and SameSite=None without Secure is rejected
            // outright; everything else the upstream asked for survives untouched.
            Assert.Equal(new[] { "sid=abc; Path=/; HttpOnly; SameSite=Lax" },
                         HeaderValues(resp, "Set-Cookie"));
        }
    }

    [Fact]
    public async Task A_redirect_to_the_upstream_comes_back_pointing_at_the_gateway()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            // This upstream redirects to itself, which is the case worth rewriting: left alone, the
            // browser would leave the gateway and the client certificate behind.
            await using var api = await LoopbackMtlsServer.StartRedirectChainAsync(
                server, client.Thumbprint!, hops: 1);
            await using var serve = ServeHost.Start(client,
                "--upstream", $"/svc={api.BaseUrl}", "--rewrite-location");

            using var http = NewClient();
            var resp = await Poll(() => http.GetAsync($"{serve.Origin}/svc/"));

            Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
            // Back through the mount point, so following it reaches the same upstream again.
            Assert.Equal(new[] { $"{serve.Origin}/svc/hop1" }, HeaderValues(resp, "Location"));
        }
    }

    [Fact]
    public async Task A_websocket_message_round_trips_through_the_gateway_on_the_negotiated_subprotocol()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartWebSocketEchoAsync(server, client.Thumbprint!);
            await using var serve = ServeHost.Start(client, "--upstream", $"/ws={api.BaseUrl}", "--allow-upgrade");

            using var cts = new CancellationTokenSource(Limit);
            var url = new Uri($"ws://127.0.0.1:{serve.Port}/ws/socket");
            // A ClientWebSocket cannot be reconnected once a connect has failed, so each attempt
            // while the listener binds needs a socket of its own.
            using var browser = await Poll(async () =>
            {
                var ws = new ClientWebSocket();
                ws.Options.AddSubProtocol("chat");
                try { await ws.ConnectAsync(url, cts.Token); return ws; }
                catch { ws.Dispose(); throw; }
            });

            await browser.SendAsync(Encoding.UTF8.GetBytes("hello upstream"),
                                    WebSocketMessageType.Text, true, cts.Token);
            var buffer = new byte[8192];
            var received = await browser.ReceiveAsync(buffer, cts.Token);

            Assert.Equal("chat", browser.SubProtocol);
            Assert.Equal(WebSocketMessageType.Text, received.MessageType);
            Assert.Equal("hello upstream", Encoding.UTF8.GetString(buffer, 0, received.Count));
        }
    }

    [Fact]
    public async Task Without_the_browser_flags_the_upstream_headers_are_relayed_untouched()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartWithHeadersAsync(
                server, client.Thumbprint!,
                new[]
                {
                    new KeyValuePair<string, string>(
                        "Access-Control-Allow-Origin", "https://upstream.example"),
                    new KeyValuePair<string, string>(
                        "Set-Cookie", "sid=abc; Domain=api.internal; Secure; SameSite=None")
                },
                responseBody: "ok", responseContentType: "text/plain");
            await using var serve = ServeHost.Start(client, "--upstream", $"/={api.BaseUrl}");

            using var http = NewClient();
            HttpRequestMessage FromApp()
            {
                var m = new HttpRequestMessage(HttpMethod.Get, $"{serve.Origin}/orders");
                m.Headers.Add("Origin", "https://app.example");
                return m;
            }
            var resp = await Poll(() => http.SendAsync(FromApp()));

            // The default is a faithful relay: nothing is stripped, nothing is added, and an
            // existing non-browser caller sees exactly what it saw before.
            Assert.Equal(new[] { "https://upstream.example" },
                         HeaderValues(resp, "Access-Control-Allow-Origin"));
            Assert.Equal(new[] { "sid=abc; Domain=api.internal; Secure; SameSite=None" },
                         HeaderValues(resp, "Set-Cookie"));
            Assert.Empty(HeaderValues(resp, "Access-Control-Allow-Credentials"));
        }
    }

    [Theory]
    [InlineData("https://api.internal")]      // no prefix and no '=' at all
    [InlineData("=https://api.internal")]     // nothing before the '='
    [InlineData(" =https://api.internal")]    // and nothing but blanks before it
    [InlineData("/api=api.internal")]         // not an absolute URL
    [InlineData("/api=ftp://api.internal")]   // not a URL the gateway can forward to
    public void A_malformed_upstream_spec_is_a_usage_error(string spec)
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(new[] { "serve", "--port", FreePort().ToString(), "--upstream", spec },
                              TextWriter.Null, stderr, services: new CliServices());

        Assert.Equal(2, code);
        Assert.Contains(spec.Trim(), stderr.ToString());   // the offending spec is quoted back
    }

    [Fact]
    public void Two_upstreams_on_the_same_mount_point_are_a_usage_error()
    {
        var stderr = new StringWriter();
        // Same prefix, differently spelled: last-wins would silently send /api somewhere the user
        // did not mean.
        int code = CliApp.Run(new[] { "serve", "--port", FreePort().ToString(),
                                      "--upstream", "/api=https://a.internal",
                                      "--upstream", "/api/=https://b.internal" },
                              TextWriter.Null, stderr, services: new CliServices());

        Assert.Equal(2, code);
        Assert.Contains("/api", stderr.ToString());
    }

    [Fact]
    public void A_positional_upstream_that_collides_with_a_root_mount_is_a_usage_error()
    {
        var stderr = new StringWriter();
        int code = CliApp.Run(new[] { "serve", "https://a.internal", "--port", FreePort().ToString(),
                                      "--upstream", "/=https://b.internal" },
                              TextWriter.Null, stderr, services: new CliServices());

        Assert.Equal(2, code);
    }

    [Theory]
    // A proxy address that is not an address.
    [InlineData("--proxy not-a-url")]
    // A pinned address cannot survive a proxy, which tunnels the connection somewhere else.
    [InlineData("--proxy http://proxy.corp:8080 --resolve api.internal:443:10.0.0.1")]
    public void Transport_settings_the_gateway_cannot_honour_are_a_usage_error(string flags)
    {
        var args = new List<string> { "serve", "--port", FreePort().ToString(),
                                      "--upstream", "/=https://api.internal" };
        args.AddRange(flags.Split(' '));

        int code = CliApp.Run(args.ToArray(), TextWriter.Null, new StringWriter(), services: new CliServices());

        // Said before the listener binds, rather than dropped on the floor once traffic arrives.
        Assert.Equal(2, code);
    }

    [Fact]
    public void Serving_with_no_upstream_at_all_is_a_usage_error()
    {
        int code = CliApp.Run(new[] { "serve", "--port", FreePort().ToString() },
                              TextWriter.Null, new StringWriter(), services: new CliServices());

        Assert.Equal(2, code);
    }
}
