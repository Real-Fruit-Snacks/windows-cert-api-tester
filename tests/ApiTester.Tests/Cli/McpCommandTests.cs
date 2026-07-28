using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Cli.Commands;
using ApiTester.Cli.Mcp;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class McpCommandTests
{
    private static (X509Certificate2 ca, X509Certificate2 server, X509Certificate2 client) Certs()
    {
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        var server = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        var client = SelfSignedCertificateFactory.CreateSignedCertificate("McpClient", ca, false, true);
        return (ca, server, client);
    }

    private static ToolDef Tool(IReadOnlyList<ToolDef> tools, string name) => tools.First(t => t.Name == name);

    private static JsonElement Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Send_request_uses_the_pinned_cert_against_an_allowed_host()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartAsync(server, client.Thumbprint!, "{\"ok\":true}");
            var host = new Uri(upstream.BaseUrl).Host;   // 127.0.0.1
            var tools = McpCommand.BuildTools(client, new HostAllowlist(new[] { host }),
                insecure: true, timeout: 30, includeLocalMachine: false, workspace: null,
                noAutoToken: false, new CliServices());

            var result = Tool(tools, "send_request").Handler(Args($"{{\"method\":\"GET\",\"url\":\"{upstream.BaseUrl}\"}}"));
            Assert.False(result.IsError);
            using var doc = JsonDocument.Parse(result.Json);
            Assert.Equal(200, doc.RootElement.GetProperty("status").GetInt32());
            Assert.True(doc.RootElement.GetProperty("clientCertPresented").GetBoolean());
        }
    }

    [Fact]
    public void Send_request_refuses_a_host_off_the_allowlist_before_connecting()
    {
        var tools = McpCommand.BuildTools(null, new HostAllowlist(new[] { "internal.corp" }),
            insecure: false, timeout: 5, includeLocalMachine: false, workspace: null,
            noAutoToken: false, new CliServices());
        var result = Tool(tools, "send_request").Handler(Args("{\"url\":\"https://evil.com/x\"}"));
        Assert.True(result.IsError);
        Assert.Contains("not allowed", result.Json);
    }

    [Fact]
    public void List_certificates_returns_the_store()
    {
        var services = new CliServices
        {
            ListCertificates = _ => new[]
            {
                new CertificateInfo { Subject = "CN=A", Issuer = "CN=CA", Thumbprint = "AA",
                    NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                    HasClientAuthEku = true, Certificate = null! }
            }
        };
        var tools = McpCommand.BuildTools(null, new HostAllowlist(Array.Empty<string>()), false, 5, false, null, false, services);
        var result = Tool(tools, "list_certificates").Handler(Args("{}"));
        Assert.False(result.IsError);
        Assert.Contains("CN=A", result.Json);
    }

    [Fact]
    public void Self_test_passes()
    {
        var tools = McpCommand.BuildTools(null, new HostAllowlist(Array.Empty<string>()), false, 5, false, null, false, new CliServices());
        var result = Tool(tools, "self_test").Handler(Args("{}"));
        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Json);
        Assert.True(doc.RootElement.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public void Missing_store_value_is_a_usage_error()
    {
        int code = CliApp.Run(new[] { "mcp", "--store", "Nope" }, TextReader.Null, new StringWriter(), TextWriter.Null, services: new CliServices());
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task Send_request_does_not_follow_a_redirect_to_an_off_allowlist_host()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            // The allowed upstream 302s to an off-allowlist host; the tool must return the 302,
            // NOT follow it (which would present the pinned certificate to evil.example).
            await using var upstream = await LoopbackMtlsServer.StartRedirectAsync(server, client.Thumbprint!, "https://evil.example/steal");
            var host = new Uri(upstream.BaseUrl).Host;   // 127.0.0.1 — only this host is allowed
            var tools = McpCommand.BuildTools(client, new HostAllowlist(new[] { host }),
                insecure: true, timeout: 30, includeLocalMachine: false, workspace: null,
                noAutoToken: false, new CliServices());

            var result = Tool(tools, "send_request").Handler(Args($"{{\"url\":\"{upstream.BaseUrl}\"}}"));
            Assert.False(result.IsError);
            using var doc = JsonDocument.Parse(result.Json);
            Assert.Equal(302, doc.RootElement.GetProperty("status").GetInt32());   // returned, not followed
        }
    }

    [Fact]
    public async Task Send_request_captures_then_reuses_a_session_token()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("McpClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(
            serverCert, clientCert.Thumbprint!, "{\"access_token\":\"mcp-tok\"}");

        var services = new CliServices { IsGuiRunning = () => false };
        var tools = McpCommand.BuildTools(clientCert, new HostAllowlist(new List<string>()),
            insecure: true, timeout: 30, includeLocalMachine: false, workspace: null,
            noAutoToken: false, services);
        var send = tools.Single(t => t.Name == "send_request");

        ToolResult Call(string json) =>
            send.Handler(System.Text.Json.JsonDocument.Parse(json).RootElement);

        var first = Call($"{{\"url\":\"{server.BaseUrl}\"}}");
        Assert.Contains("captured bearer token", first.Json);

        var second = Call($"{{\"url\":\"{server.BaseUrl}\"}}");
        Assert.Contains("using captured token", second.Json);

        // An explicit Authorization header wins over the session token.
        var explicitAuth = Call($"{{\"url\":\"{server.BaseUrl}\",\"headers\":{{\"Authorization\":\"Bearer mine\"}}}}");
        Assert.DoesNotContain("using captured token", explicitAuth.Json);
    }

    [Fact]
    public async Task No_auto_token_disables_the_session_store()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("McpClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(
            serverCert, clientCert.Thumbprint!, "{\"access_token\":\"mcp-tok\"}");

        var services = new CliServices { IsGuiRunning = () => false };
        var tools = McpCommand.BuildTools(clientCert, new HostAllowlist(new List<string>()),
            insecure: true, timeout: 30, includeLocalMachine: false, workspace: null,
            noAutoToken: true, services);
        var send = tools.Single(t => t.Name == "send_request");
        var first = send.Handler(System.Text.Json.JsonDocument.Parse($"{{\"url\":\"{server.BaseUrl}\"}}").RootElement);
        Assert.DoesNotContain("captured bearer token", first.Json);
    }

    [Fact]
    public async Task Run_saved_resolves_a_query_variable_on_the_wire()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);

            var node = new CollectionNode
            {
                Id = "r1", Name = "get item", IsFolder = false,
                Request = new RequestModel { Method = "GET", Path = upstream.BaseUrl, IgnoreServerCert = true }
            };
            node.Request!.QueryParams.Add(new ParamRow { Key = "api_key", Value = "{{tok}}" });

            var state = new AppState();
            state.Collections.Add(node);
            state.Environments.Add(new ApiEnvironment
            {
                Id = "e1", Name = "E",
                Variables = { new Variable { Key = "tok", Value = "SECRET123" } }
            });
            var ws = Path.Combine(Path.GetTempPath(), $"certapi-mcp-queryvar-{Guid.NewGuid():N}.json");
            state.SaveTo(ws);
            try
            {
                var host = new Uri(upstream.BaseUrl).Host;   // 127.0.0.1
                var tools = McpCommand.BuildTools(client, new HostAllowlist(new[] { host }),
                    insecure: true, timeout: 30, includeLocalMachine: false, workspace: ws,
                    noAutoToken: false, new CliServices { LiveStatePath = ws });

                var result = Tool(tools, "run_saved").Handler(Args("{\"path\":\"get item\",\"env\":\"E\"}"));

                Assert.False(result.IsError);
                using var doc = JsonDocument.Parse(result.Json);
                Assert.Equal(200, doc.RootElement.GetProperty("status").GetInt32());
                // Genuine wire truth: the echo server answers with the request text it actually
                // received, so this is what the server actually got — not what the client composed.
                string echoedRequest = doc.RootElement.GetProperty("body").GetString()!;
                Assert.Contains("api_key=SECRET123", echoedRequest);
                Assert.DoesNotContain("%7B%7B", echoedRequest);
            }
            finally { File.Delete(ws); }
        }
    }

    // ---------------------------------------------------------------- v1.67.0: parity & new tools

    private static string SaveWorkspace(AppState state, string tag)
    {
        var ws = Path.Combine(Path.GetTempPath(), $"certapi-mcp-{tag}-{Guid.NewGuid():N}.json");
        state.SaveTo(ws);
        return ws;
    }

    [Fact]
    public async Task Send_request_reaches_a_pinned_host_without_insecure()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartAsync(server, client.Thumbprint!, "{\"ok\":true}");
            var host = new Uri(upstream.BaseUrl).Host;

            var state = new AppState();
            TrustService.Trust(state, host, server);
            var ws = SaveWorkspace(state, "pin");
            try
            {
                // The sharp edge this closes: --insecure is OFF, and the pin alone must carry it,
                // exactly as it does for send/run/fuzz/bench.
                var tools = McpCommand.BuildTools(client, new HostAllowlist(new[] { host }),
                    insecure: false, timeout: 30, includeLocalMachine: false, workspace: ws,
                    noAutoToken: false, new CliServices { LiveStatePath = ws });

                var result = Tool(tools, "send_request").Handler(Args($"{{\"url\":\"{upstream.BaseUrl}\"}}"));

                Assert.False(result.IsError);
                using var doc = JsonDocument.Parse(result.Json);
                Assert.Equal(200, doc.RootElement.GetProperty("status").GetInt32());
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task Send_request_without_a_pin_or_insecure_refuses_a_selfsigned_host()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartAsync(server, client.Thumbprint!, "{\"ok\":true}");
            var host = new Uri(upstream.BaseUrl).Host;
            var ws = SaveWorkspace(new AppState(), "nopin");
            try
            {
                var tools = McpCommand.BuildTools(client, new HostAllowlist(new[] { host }),
                    insecure: false, timeout: 30, includeLocalMachine: false, workspace: ws,
                    noAutoToken: false, new CliServices { LiveStatePath = ws });

                var result = Tool(tools, "send_request").Handler(Args($"{{\"url\":\"{upstream.BaseUrl}\"}}"));

                Assert.True(result.IsError);
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task Run_saved_applies_saved_assertions_and_captures_like_run_would()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var oauth = await LoopbackMtlsServer.StartOAuthTokenAsync(
                server, client.Thumbprint!, "cid", "shh");

            var node = new CollectionNode
            {
                Id = "login1", Name = "login", IsFolder = false,
                Request = new RequestModel
                {
                    Method = "POST", Path = oauth.BaseUrl, IgnoreServerCert = true,
                    ContentType = "application/x-www-form-urlencoded",
                    Body = "grant_type=client_credentials&client_id=cid&client_secret=shh",
                    Captures = { new CaptureRule { Variable = "token", Source = CaptureSource.Body, Path = "access_token" } },
                    Assertions = { new AssertionRule { Enabled = true, Target = AssertTarget.Status, Op = AssertOp.Equals, Value = "200" } }
                }
            };
            var state = new AppState();
            state.Collections.Add(node);
            var ws = SaveWorkspace(state, "fidelity");
            try
            {
                var host = new Uri(oauth.BaseUrl).Host;
                var tools = McpCommand.BuildTools(client, new HostAllowlist(new[] { host }),
                    insecure: false, timeout: 30, includeLocalMachine: false, workspace: ws,
                    noAutoToken: false, new CliServices { LiveStatePath = ws });

                var result = Tool(tools, "run_saved").Handler(Args("{\"path\":\"login\"}"));

                Assert.False(result.IsError);
                using var doc = JsonDocument.Parse(result.Json);
                // The saved request named no certificate, so the pinned one carried it — the
                // DefaultCertificate seam, observable as an accepted mTLS handshake.
                Assert.Equal(200, doc.RootElement.GetProperty("status").GetInt32());
                Assert.True(doc.RootElement.GetProperty("passed").GetBoolean());
                var assertion = doc.RootElement.GetProperty("assertions")[0];
                Assert.True(assertion.GetProperty("passed").GetBoolean());
                var capture = doc.RootElement.GetProperty("captures")[0];
                Assert.Equal("token", capture.GetProperty("variable").GetString());
                Assert.True(capture.GetProperty("ok").GetBoolean());
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task Run_saved_reports_a_failing_saved_assertion_as_not_passed()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartAsync(server, client.Thumbprint!, "{\"ok\":true}");
            var node = new CollectionNode
            {
                Id = "r1", Name = "strict", IsFolder = false,
                Request = new RequestModel
                {
                    Method = "GET", Path = upstream.BaseUrl, IgnoreServerCert = true,
                    Assertions = { new AssertionRule { Enabled = true, Target = AssertTarget.Status, Op = AssertOp.Equals, Value = "418" } }
                }
            };
            var state = new AppState();
            state.Collections.Add(node);
            var ws = SaveWorkspace(state, "assertfail");
            try
            {
                var host = new Uri(upstream.BaseUrl).Host;
                var tools = McpCommand.BuildTools(client, new HostAllowlist(new[] { host }),
                    insecure: false, timeout: 30, includeLocalMachine: false, workspace: ws,
                    noAutoToken: false, new CliServices { LiveStatePath = ws });

                var result = Tool(tools, "run_saved").Handler(Args("{\"path\":\"strict\"}"));

                // The transport succeeded, so the tool call is not an error — but the request
                // did not pass, and the assertion says why. That is `certapi run`'s contract.
                Assert.False(result.IsError);
                using var doc = JsonDocument.Parse(result.Json);
                Assert.Equal(200, doc.RootElement.GetProperty("status").GetInt32());
                Assert.False(doc.RootElement.GetProperty("passed").GetBoolean());
                Assert.False(doc.RootElement.GetProperty("assertions")[0].GetProperty("passed").GetBoolean());
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task Run_chain_captures_flow_into_a_later_tool_call_in_the_same_session()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var oauth = await LoopbackMtlsServer.StartOAuthTokenAsync(
                server, client.Thumbprint!, "cid", "shh");
            await using var echo = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);

            var login = new CollectionNode
            {
                Id = "login1", Name = "login", IsFolder = false,
                Request = new RequestModel
                {
                    Method = "POST", Path = oauth.BaseUrl, IgnoreServerCert = true,
                    ContentType = "application/x-www-form-urlencoded",
                    Body = "grant_type=client_credentials&client_id=cid&client_secret=shh",
                    Captures = { new CaptureRule { Variable = "token", Source = CaptureSource.Body, Path = "access_token" } }
                }
            };
            var fetch = new CollectionNode
            {
                Id = "fetch1", Name = "fetch", IsFolder = false,
                Request = new RequestModel { Method = "GET", Path = echo.BaseUrl, IgnoreServerCert = true }
            };
            fetch.Request!.QueryParams.Add(new ParamRow { Key = "api_key", Value = "{{token}}" });

            var state = new AppState();
            state.Collections.Add(login);
            state.Collections.Add(fetch);
            state.Chains.Add(new RequestChain
            {
                Name = "sess", EnvironmentName = "Sess",
                Steps = { new ChainStep { RequestId = "login1" } }
            });
            var ws = SaveWorkspace(state, "chain");
            try
            {
                var host = new Uri(oauth.BaseUrl).Host;
                var tools = McpCommand.BuildTools(client, new HostAllowlist(new[] { host }),
                    insecure: false, timeout: 30, includeLocalMachine: false, workspace: ws,
                    noAutoToken: false, new CliServices { LiveStatePath = ws });

                var chainResult = Tool(tools, "run_chain").Handler(Args("{\"name\":\"sess\"}"));
                Assert.False(chainResult.IsError);
                using (var doc = JsonDocument.Parse(chainResult.Json))
                {
                    Assert.True(doc.RootElement.GetProperty("passed").GetBoolean());
                    var step = doc.RootElement.GetProperty("steps")[0];
                    Assert.True(step.GetProperty("passed").GetBoolean());
                    Assert.True(step.GetProperty("captures")[0].GetProperty("ok").GetBoolean());
                }

                // The whole point of the session model: what the chain captured resolves
                // {{variables}} in a LATER tool call, with no state ever written to disk.
                var fetchResult = Tool(tools, "run_saved").Handler(Args("{\"path\":\"fetch\",\"env\":\"Sess\"}"));
                Assert.False(fetchResult.IsError);
                using var fetchDoc = JsonDocument.Parse(fetchResult.Json);
                string echoed = fetchDoc.RootElement.GetProperty("body").GetString()!;
                Assert.Contains("api_key=", echoed);
                Assert.DoesNotContain("%7B%7B", echoed);   // {{token}} resolved, not escaped

                // And the workspace file on disk is byte-for-byte what the session started from.
                var reloaded = AppState.LoadFrom(ws);
                Assert.DoesNotContain(reloaded.Environments, e => e.Name == "Sess");
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public void Run_chain_gates_every_step_against_the_allowlist()
    {
        var node = new CollectionNode
        {
            Id = "r1", Name = "blocked", IsFolder = false,
            Request = new RequestModel { Method = "GET", Path = "https://127.0.0.1:1/", IgnoreServerCert = true }
        };
        var state = new AppState();
        state.Collections.Add(node);
        state.Chains.Add(new RequestChain
        {
            Name = "walled",
            Steps = { new ChainStep { RequestId = "r1" }, new ChainStep { RequestId = "r1" } }
        });
        var ws = SaveWorkspace(state, "gate");
        try
        {
            var tools = McpCommand.BuildTools(null, new HostAllowlist(new[] { "allowed.invalid" }),
                insecure: true, timeout: 5, includeLocalMachine: false, workspace: ws,
                noAutoToken: false, new CliServices { LiveStatePath = ws });

            var result = Tool(tools, "run_chain").Handler(Args("{\"name\":\"walled\"}"));

            using var doc = JsonDocument.Parse(result.Json);
            Assert.False(doc.RootElement.GetProperty("passed").GetBoolean());
            var step = doc.RootElement.GetProperty("steps")[0];
            Assert.Contains("not allowed", step.GetProperty("error").GetString());
            Assert.Single(doc.RootElement.GetProperty("skipped").EnumerateArray());
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public void List_environments_returns_names_and_counts_but_never_values()
    {
        var state = new AppState();
        state.Environments.Add(new ApiEnvironment
        {
            Id = "e1", Name = "Prod",
            Variables =
            {
                new Variable { Key = "base", Value = "https://api.example" },
                new Variable { Key = "apikey", Value = "SUPERSECRETVALUE", Secret = true }
            }
        });
        state.ActiveEnvironmentId = "e1";
        var ws = SaveWorkspace(state, "envs");
        try
        {
            var tools = McpCommand.BuildTools(null, new HostAllowlist(Array.Empty<string>()),
                insecure: false, timeout: 5, includeLocalMachine: false, workspace: ws,
                noAutoToken: false, new CliServices { LiveStatePath = ws });

            var result = Tool(tools, "list_environments").Handler(Args("{}"));

            Assert.False(result.IsError);
            using var doc = JsonDocument.Parse(result.Json);
            var env = doc.RootElement.GetProperty("environments")[0];
            Assert.Equal("Prod", env.GetProperty("name").GetString());
            Assert.True(env.GetProperty("active").GetBoolean());
            Assert.Equal(2, env.GetProperty("variables").GetInt32());
            Assert.DoesNotContain("SUPERSECRETVALUE", result.Json);
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public void Resources_expose_the_workspace_with_secrets_redacted()
    {
        var state = new AppState();
        state.Collections.Add(new CollectionNode
        {
            Id = "r1", Name = "orders", IsFolder = false,
            Request = new RequestModel
            {
                Method = "GET", Path = "https://api.example/orders",
                AuthType = "Bearer", AuthSecret = "hunter2"
            }
        });
        state.Environments.Add(new ApiEnvironment
        {
            Id = "e1", Name = "Prod",
            Variables =
            {
                new Variable { Key = "base", Value = "https://api.example" },
                new Variable { Key = "apikey", Value = "SUPERSECRETVALUE", Secret = true }
            }
        });
        state.Chains.Add(new RequestChain { Name = "sess", Steps = { new ChainStep { RequestId = "r1" } } });

        var resources = McpCommand.BuildResources(state);

        var request = resources.Single(r => r.Uri.StartsWith("certapi://requests/", StringComparison.Ordinal));
        string requestText = request.Read();
        Assert.Contains("(redacted)", requestText);
        Assert.DoesNotContain("hunter2", requestText);

        var env = resources.Single(r => r.Uri.StartsWith("certapi://environments/", StringComparison.Ordinal));
        string envText = env.Read();
        Assert.Contains("https://api.example", envText);
        Assert.Contains("value withheld", envText);
        Assert.DoesNotContain("SUPERSECRETVALUE", envText);

        var chains = resources.Single(r => r.Uri == "certapi://chains");
        Assert.Contains("sess", chains.Read());
    }

    [Fact]
    public async Task Grpc_list_and_a_unary_call_work_through_the_allowlist()
    {
        await using var server = await ApiTester.Tests.Grpc.GrpcTestServer.StartAsync();
        var host = server.Uri.Host;
        var ws = SaveWorkspace(new AppState(), "grpc");
        try
        {
            var tools = McpCommand.BuildTools(null, new HostAllowlist(new[] { host }),
                insecure: false, timeout: 30, includeLocalMachine: false, workspace: ws,
                noAutoToken: false, new CliServices { LiveStatePath = ws });

            var list = Tool(tools, "grpc_list").Handler(Args($"{{\"address\":\"{server.Address}\"}}"));
            Assert.False(list.IsError);
            Assert.Contains("certapi.test.Echo", list.Json);
            Assert.Contains("\"kind\":\"unary\"", list.Json);
            Assert.Contains("bidirectional", list.Json);

            var call = Tool(tools, "grpc_call").Handler(Args(
                $"{{\"address\":\"{server.Address}\",\"method\":\"Echo/Unary\",\"data\":\"{{\\\"text\\\":\\\"hi\\\",\\\"count\\\":2}}\"}}"));
            Assert.False(call.IsError);
            using var doc = JsonDocument.Parse(call.Json);
            Assert.Equal(0, doc.RootElement.GetProperty("statusCode").GetInt32());
            Assert.Equal("hi", doc.RootElement.GetProperty("message").GetProperty("text").GetString());
        }
        finally { File.Delete(ws); }
    }

    [Fact]
    public void Grpc_call_refuses_an_address_off_the_allowlist()
    {
        var ws = SaveWorkspace(new AppState(), "grpcgate");
        try
        {
            var tools = McpCommand.BuildTools(null, new HostAllowlist(new[] { "allowed.invalid" }),
                insecure: false, timeout: 5, includeLocalMachine: false, workspace: ws,
                noAutoToken: false, new CliServices { LiveStatePath = ws });

            var result = Tool(tools, "grpc_call").Handler(Args(
                "{\"address\":\"http://127.0.0.1:5000\",\"method\":\"Echo/Unary\"}"));

            Assert.True(result.IsError);
            Assert.Contains("not allowed", result.Json);
        }
        finally { File.Delete(ws); }
    }
}
