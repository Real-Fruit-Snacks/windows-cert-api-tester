using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

/// <summary>Wire-truth regression tests for a confirmed defect: a {{variable}} used as a query
/// parameter's key or value used to reach the server percent-escaped as literal "%7B%7B...%7D%7D"
/// text, because the URL was composed (and escaped) before the resolver ever saw the token. Every
/// assertion here is about what the loopback server actually received — read back out of the echo
/// server's response text or its own request count — never about a string the client composed.</summary>
public class QueryVariableCliTests
{
    private static (X509Certificate2 ca, X509Certificate2 server, X509Certificate2 client) Certs()
    {
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        var server = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        var client = SelfSignedCertificateFactory.CreateSignedCertificate("QueryVarClient", ca, false, true);
        return (ca, server, client);
    }

    private static CliServices Services(X509Certificate2 client, string livePath) => new()
    {
        FindCertificate = _ => client,
        IsGuiRunning = () => false,
        LiveStatePath = livePath
    };

    /// <summary>A saved request as a top-level collection node with a known name, addressable directly
    /// by a positional "run &lt;name&gt;".</summary>
    private static CollectionNode Request(string name, string id, string url, string certThumbprint) =>
        new()
        {
            Id = id, Name = name, IsFolder = false,
            Request = new RequestModel
            {
                Method = "GET", Path = url, IgnoreServerCert = true, CertThumbprint = certThumbprint
            }
        };

    /// <summary>A saved request that logs in against <see cref="LoopbackMtlsServer.StartOAuthTokenAsync"/>
    /// and captures the issued access_token into {{token}}.</summary>
    private static CollectionNode LoginRequest(string id, string url, string certThumbprint)
    {
        var node = Request("login", id, url, certThumbprint);
        node.Request!.Method = "POST";
        node.Request.ContentType = "application/x-www-form-urlencoded";
        node.Request.Body = "grant_type=client_credentials&client_id=cid&client_secret=shh";
        node.Request.Captures.Add(new CaptureRule { Variable = "token", Source = CaptureSource.Body, Path = "access_token" });
        return node;
    }

    private static string Workspace(Action<AppState> build)
    {
        var state = new AppState();
        build(state);
        var path = Path.Combine(Path.GetTempPath(), $"certapi-queryvar-{Guid.NewGuid():N}.json");
        state.SaveTo(path);
        return path;
    }

    /// <summary>Pulls the request-target (the part between the method and "HTTP/1.1") out of an echoed
    /// request's first line, so a test can inspect exactly the query the server received.</summary>
    private static string RequestTarget(string echoedRequestText)
    {
        var requestLine = echoedRequestText.Split("\r\n")[0].Split(' ');
        return requestLine.Length > 1 ? requestLine[1] : "";
    }

    private static string LastHarEntryResponseText(string harPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(harPath));
        var entries = doc.RootElement.GetProperty("log").GetProperty("entries").EnumerateArray().ToList();
        return entries[^1].GetProperty("response").GetProperty("content").GetProperty("text").GetString()!;
    }

    [Fact]
    public async Task A_query_variable_reaches_the_server_as_its_value()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);

            var node = Request("get item", "r1", api.BaseUrl, client.Thumbprint!);
            node.Request!.QueryParams.Add(new ParamRow { Key = "api_key", Value = "{{tok}}" });

            var har = Path.Combine(Path.GetTempPath(), $"certapi-queryvar-{Guid.NewGuid():N}.har");
            var ws = Workspace(state =>
            {
                state.Collections.Add(node);
                state.Environments.Add(new ApiEnvironment
                {
                    Id = "e1", Name = "E",
                    Variables = { new Variable { Key = "tok", Value = "SECRET123" } }
                });
            });
            try
            {
                var so = new StringWriter(); var se = new StringWriter();
                int code = CliApp.Run(
                    new[] { "run", "get item", "--workspace", ws, "--env", "E", "--no-auto-token", "--har", har },
                    so, se, services: Services(client, ws));
                Assert.Equal(0, code);

                string echoed = LastHarEntryResponseText(har);
                Assert.Contains("api_key=SECRET123", echoed);
                Assert.DoesNotContain("%7B%7B", echoed);
            }
            finally { File.Delete(ws); File.Delete(har); }
        }
    }

    [Fact]
    public async Task A_resolved_query_value_containing_query_syntax_reaches_the_server_intact()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);

            var node = Request("get item", "r1", api.BaseUrl, client.Thumbprint!);
            node.Request!.QueryParams.Add(new ParamRow { Key = "api_key", Value = "{{tok}}" });

            var har = Path.Combine(Path.GetTempPath(), $"certapi-queryvar-{Guid.NewGuid():N}.har");
            var ws = Workspace(state =>
            {
                state.Collections.Add(node);
                state.Environments.Add(new ApiEnvironment
                {
                    Id = "e1", Name = "E",
                    Variables = { new Variable { Key = "tok", Value = "a b&c=d é" } }
                });
            });
            try
            {
                var so = new StringWriter(); var se = new StringWriter();
                int code = CliApp.Run(
                    new[] { "run", "get item", "--workspace", ws, "--env", "E", "--no-auto-token", "--har", har },
                    so, se, services: Services(client, ws));
                Assert.Equal(0, code);

                string echoed = LastHarEntryResponseText(har);
                var target = RequestTarget(echoed);
                var (_, query) = QueryString.Split(target);
                var pair = Assert.Single(QueryString.Parse(query));
                Assert.Equal("api_key", pair.Key);
                Assert.Equal("a b&c=d é", pair.Value);   // proves substitution-then-encoding
            }
            finally { File.Delete(ws); File.Delete(har); }
        }
    }

    [Fact]
    public async Task A_query_parameter_key_variable_reaches_the_server_resolved()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);

            var node = Request("get item", "r1", api.BaseUrl, client.Thumbprint!);
            node.Request!.QueryParams.Add(new ParamRow { Key = "{{keyname}}", Value = "v" });

            var har = Path.Combine(Path.GetTempPath(), $"certapi-queryvar-{Guid.NewGuid():N}.har");
            var ws = Workspace(state =>
            {
                state.Collections.Add(node);
                state.Environments.Add(new ApiEnvironment
                {
                    Id = "e1", Name = "E",
                    Variables = { new Variable { Key = "keyname", Value = "api_key" } }
                });
            });
            try
            {
                var so = new StringWriter(); var se = new StringWriter();
                int code = CliApp.Run(
                    new[] { "run", "get item", "--workspace", ws, "--env", "E", "--no-auto-token", "--har", har },
                    so, se, services: Services(client, ws));
                Assert.Equal(0, code);

                string echoed = LastHarEntryResponseText(har);
                Assert.Contains("api_key=v", echoed);
            }
            finally { File.Delete(ws); File.Delete(har); }
        }
    }

    [Fact]
    public async Task An_unresolved_query_variable_fails_the_request_under_strict_vars()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            // failures: 0 → every request answered 200, and RequestCount is the server's own record of
            // how many it actually answered — nothing reaching zero is proof nothing went on the wire.
            await using var upstream = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);

            var node = Request("get item", "r1", upstream.BaseUrl, client.Thumbprint!);
            node.Request!.QueryParams.Add(new ParamRow { Key = "api_key", Value = "{{tok}}" });

            var ws = Workspace(state => state.Collections.Add(node));
            try
            {
                var so = new StringWriter(); var se = new StringWriter();
                int code = CliApp.Run(
                    new[] { "run", "get item", "--workspace", ws, "--strict-vars" },
                    so, se, services: Services(client, ws));

                Assert.Equal(1, code);
                string combined = so.ToString() + se.ToString();
                Assert.Contains("unresolved variables", combined);
                Assert.Contains("{{tok}}", combined);
                Assert.Equal(0, upstream.RequestCount);
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task An_unresolved_query_variable_is_warned_about_without_strict_vars()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);

            var node = Request("get item", "r1", upstream.BaseUrl, client.Thumbprint!);
            node.Request!.QueryParams.Add(new ParamRow { Key = "api_key", Value = "{{tok}}" });

            var ws = Workspace(state => state.Collections.Add(node));
            try
            {
                var so = new StringWriter(); var se = new StringWriter();
                int code = CliApp.Run(
                    new[] { "run", "get item", "--workspace", ws },
                    so, se, services: Services(client, ws));

                Assert.Equal(0, code);
                Assert.Contains("warning: unresolved variables", se.ToString());
                Assert.Contains("{{tok}}", se.ToString());
                Assert.Equal(1, upstream.RequestCount);   // the request still went out
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task A_chain_step_feeds_a_captured_token_as_a_query_parameter()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var login = await LoopbackMtlsServer.StartOAuthTokenAsync(server, client.Thumbprint!, "cid", "shh");
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);

            var step1 = LoginRequest("r-login", login.BaseUrl, client.Thumbprint!);
            var step2 = Request("call the API", "r-call", api.BaseUrl, client.Thumbprint!);
            step2.Request!.QueryParams.Add(new ParamRow { Key = "api_key", Value = "{{token}}" });

            var har = Path.Combine(Path.GetTempPath(), $"certapi-queryvar-{Guid.NewGuid():N}.har");
            var ws = Workspace(state =>
            {
                state.Collections.Add(step1);
                state.Collections.Add(step2);
                state.Chains.Add(new RequestChain
                {
                    Name = "login then call", EnvironmentName = "ChainEnv",
                    Steps = { new ChainStep { RequestId = "r-login" }, new ChainStep { RequestId = "r-call" } }
                });
            });
            try
            {
                var so = new StringWriter(); var se = new StringWriter();
                // --no-auto-token leaves the chain's own capture as the only way the token can reach the
                // second server, so a pass here cannot be the automatic session-token machinery.
                int code = CliApp.Run(
                    new[] { "run", "--chain", "login then call", "--workspace", ws, "--no-auto-token", "--har", har },
                    so, se, services: Services(client, ws));
                Assert.Equal(0, code);

                string echoed = LastHarEntryResponseText(har);
                Assert.Contains("api_key=at-client_credentials", echoed);
            }
            finally { File.Delete(ws); File.Delete(har); }
        }
    }

    [Fact]
    public async Task Bench_resolves_a_query_variable_in_a_saved_requests_url()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            // failures: 0 → every request answered 200, and RequestCount is the server's own record of
            // how many it actually answered — chosen over the echo server because bench does not
            // surface response bodies, so RequestCount is the only wire-truth available to it.
            await using var upstream = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);

            var node = Request("get item", "r1", upstream.BaseUrl, client.Thumbprint!);
            node.Request!.QueryParams.Add(new ParamRow { Key = "api_key", Value = "{{tok}}" });

            var ws = Workspace(state =>
            {
                state.Collections.Add(node);
                state.Environments.Add(new ApiEnvironment
                {
                    Id = "e1", Name = "E",
                    Variables = { new Variable { Key = "tok", Value = "SECRET123" } }
                });
            });
            try
            {
                var so = new StringWriter(); var se = new StringWriter();
                int code = CliApp.Run(
                    new[] { "bench", "get item", "-n", "1", "-c", "1", "--workspace", ws, "--env", "E" },
                    so, se, services: Services(client, ws));

                Assert.Equal(0, code);
                Assert.Equal(1, upstream.RequestCount);
                string stdout = so.ToString();
                // This assertion on the reported URL is indirect: bench prints the URL it benched
                // rather than echoing what the server received. The direct wire proof for this same
                // composition (substitution before percent-encoding) is
                // A_query_variable_reaches_the_server_as_its_value, above, which reads the echo
                // server's own response text instead of bench's report.
                Assert.Contains("api_key=SECRET123", stdout);
                Assert.DoesNotContain("%7B%7B", stdout);
                Assert.Contains("1 sent", stdout);
                Assert.Contains("1 succeeded", stdout);
            }
            finally { File.Delete(ws); }
        }
    }

    [Fact]
    public async Task The_json_envelope_reports_the_url_that_was_actually_sent()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var api = await LoopbackMtlsServer.StartEchoAsync(server, client.Thumbprint!);

            var node = Request("get item", "r1", api.BaseUrl, client.Thumbprint!);
            node.Request!.QueryParams.Add(new ParamRow { Key = "api_key", Value = "{{tok}}" });

            var har = Path.Combine(Path.GetTempPath(), $"certapi-queryvar-{Guid.NewGuid():N}.har");
            var ws = Workspace(state =>
            {
                state.Collections.Add(node);
                state.Environments.Add(new ApiEnvironment
                {
                    Id = "e1", Name = "E",
                    Variables = { new Variable { Key = "tok", Value = "SECRET123" } }
                });
            });
            try
            {
                var so = new StringWriter(); var se = new StringWriter();
                int code = CliApp.Run(
                    new[] { "run", "get item", "--workspace", ws, "--env", "E", "--no-auto-token", "--json", "--har", har },
                    so, se, services: Services(client, ws));
                Assert.Equal(0, code);

                using var doc = JsonDocument.Parse(so.ToString());
                string reportedUrl = doc.RootElement.GetProperty("results")[0].GetProperty("url").GetString()!;

                string echoed = LastHarEntryResponseText(har);
                string requestTarget = RequestTarget(echoed);

                // The report and the wire must be the same string, not two independently-composed ones.
                Assert.Equal(requestTarget, new Uri(reportedUrl).PathAndQuery);
                Assert.Contains("api_key=SECRET123", reportedUrl);
                Assert.DoesNotContain("%7B%7B", reportedUrl);
            }
            finally { File.Delete(ws); File.Delete(har); }
        }
    }

    [Fact]
    public async Task The_json_envelope_reports_the_attempted_url_when_a_variable_is_unresolved()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var upstream = await LoopbackMtlsServer.StartFlakyAsync(server, client.Thumbprint!, failures: 0);

            var node = Request("get item", "r1", upstream.BaseUrl, client.Thumbprint!);
            node.Request!.QueryParams.Add(new ParamRow { Key = "api_key", Value = "{{tok}}" });

            var ws = Workspace(state => state.Collections.Add(node));
            try
            {
                var so = new StringWriter(); var se = new StringWriter();
                int code = CliApp.Run(
                    new[] { "run", "get item", "--workspace", ws, "--json", "--strict-vars" },
                    so, se, services: Services(client, ws));

                Assert.Equal(1, code);
                Assert.Equal(0, upstream.RequestCount);

                using var doc = JsonDocument.Parse(so.ToString());
                var result = doc.RootElement.GetProperty("results")[0];
                Assert.Contains("api_key=%7B%7Btok%7D%7D", result.GetProperty("url").GetString());
                Assert.Contains("unresolved variables", result.GetProperty("error").GetString());
            }
            finally { File.Delete(ws); }
        }
    }
}
