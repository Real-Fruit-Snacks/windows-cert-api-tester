using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The mock refusing the way a real protected endpoint refuses — before it routes, with
/// an honest challenge header — so a client's authentication paths meet something realistic rather
/// than an echo. The decision itself is a pure function, so most of this is data.</summary>
public class MockRequirementTests
{
    private static Dictionary<string, string> Headers(params (string Name, string Value)[] entries)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in entries) d[name] = value;
        return d;
    }

    // ---------------------------------------------------------------- the decision (pure)

    [Fact]
    public void A_bearer_requirement_accepts_only_the_exact_token()
    {
        var require = new MockRequirements { Bearer = "expected" };

        Assert.Null(require.Refuse(null, null, null, Headers(("Authorization", "Bearer expected"))));
        Assert.NotNull(require.Refuse(null, null, null, Headers()));                                  // absent
        Assert.NotNull(require.Refuse(null, null, null, Headers(("Authorization", "Bearer other"))));  // wrong
        Assert.NotNull(require.Refuse(null, null, null, Headers(("Authorization", "Basic expected")))); // wrong scheme
        // The token is compared exactly; the scheme word is not case-sensitive, as HTTP says.
        Assert.Null(require.Refuse(null, null, null, Headers(("Authorization", "bearer expected"))));
        Assert.NotNull(require.Refuse(null, null, null, Headers(("Authorization", "Bearer EXPECTED"))));
    }

    [Fact]
    public void Requiring_any_client_certificate_is_satisfied_by_any_certificate()
    {
        var require = new MockRequirements { ClientCert = true };

        Assert.Null(require.Refuse("CN=Anyone", "CN=Any CA", "AABB", Headers()));
        Assert.NotNull(require.Refuse(null, null, null, Headers()));
    }

    [Fact]
    public void A_required_issuer_and_thumbprint_are_each_checked()
    {
        var byIssuer = new MockRequirements { ClientCert = true, ClientCertIssuer = "Corp Issuing CA" };
        Assert.Null(byIssuer.Refuse("CN=Me", "CN=Corp Issuing CA, O=Corp", "AABB", Headers()));
        Assert.NotNull(byIssuer.Refuse("CN=Me", "CN=Someone Else", "AABB", Headers()));

        var byThumb = new MockRequirements { ClientCert = true, ClientCertThumbprint = "AABBCC" };
        Assert.Null(byThumb.Refuse("CN=Me", "CN=CA", "aabbcc", Headers()));   // thumbprints ignore case
        Assert.NotNull(byThumb.Refuse("CN=Me", "CN=CA", "DDEEFF", Headers()));
    }

    [Fact]
    public void Both_requirements_must_be_met_when_both_are_declared()
    {
        var require = new MockRequirements { ClientCert = true, Bearer = "tok" };

        Assert.Null(require.Refuse("CN=Me", "CN=CA", "AABB", Headers(("Authorization", "Bearer tok"))));
        Assert.NotNull(require.Refuse(null, null, null, Headers(("Authorization", "Bearer tok"))));
        Assert.NotNull(require.Refuse("CN=Me", "CN=CA", "AABB", Headers()));
    }

    [Fact]
    public void The_challenge_matches_what_is_being_asked_for()
    {
        Assert.Equal("WWW-Authenticate", new MockRequirements { Bearer = "t" }.Challenge().Key);
        Assert.Contains("Bearer", new MockRequirements { Bearer = "t" }.Challenge().Value);
        Assert.Contains("Certificate", new MockRequirements { ClientCert = true }.Challenge().Value);
        // A proxy-style refusal challenges on the proxy header instead, as HTTP requires.
        Assert.Equal("Proxy-Authenticate", new MockRequirements { Bearer = "t", OnFail = 407 }.Challenge().Key);
    }

    // ---------------------------------------------------------------- parsing

    [Fact]
    public void Requirements_are_read_from_the_scenario_file()
    {
        var scenario = MockScenario.Parse("""
            {
              "routes": [ { "match": { "path": "/x" }, "respond": { "status": 200 } } ],
              "require": { "clientCert": { "issuer": "CN=Corp Issuing CA" }, "bearer": "tok", "onFail": 403 }
            }
            """);

        Assert.NotNull(scenario.Require);
        Assert.True(scenario.Require!.ClientCert);
        Assert.Equal("CN=Corp Issuing CA", scenario.Require.ClientCertIssuer);
        Assert.Equal("tok", scenario.Require.Bearer);
        Assert.Equal(403, scenario.Require.OnFail);
    }

    [Fact]
    public void Client_cert_true_means_any_certificate_will_do()
    {
        var scenario = MockScenario.Parse("""
            {"routes":[],"require":{"clientCert":true}}
            """);

        Assert.True(scenario.Require!.ClientCert);
        Assert.Null(scenario.Require.ClientCertIssuer);
    }

    [Fact]
    public void A_requirement_that_asks_for_nothing_is_ignored_and_named()
    {
        var scenario = MockScenario.Parse("""{"routes":[],"require":{"onFail":403}}""");

        Assert.Null(scenario.Require);
        Assert.Contains(scenario.Warnings, w => w.Contains("asks for nothing"));
    }

    [Fact]
    public void An_impossible_onFail_status_falls_back_to_401_and_says_so()
    {
        var scenario = MockScenario.Parse("""{"routes":[],"require":{"bearer":"t","onFail":200}}""");

        Assert.Equal(401, scenario.Require!.OnFail);
        Assert.Contains(scenario.Warnings, w => w.Contains("onFail"));
    }

    // ---------------------------------------------------------------- over the wire

    [Fact]
    public async Task A_missing_bearer_is_refused_before_any_route_is_consulted()
    {
        var scenario = MockScenario.Parse("""
            {
              "routes": [ { "match": { "path": "/data" }, "respond": { "status": 200, "body": "secret" } } ],
              "require": { "bearer": "letmein" }
            }
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);
        using var http = new HttpClient();

        var refused = await http.GetAsync($"http://127.0.0.1:{mock.Port}/data");

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains("Bearer", refused.Headers.WwwAuthenticate.ToString());
        // The route never ran, so its body cannot have leaked into the refusal.
        Assert.DoesNotContain("secret", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_right_bearer_gets_through_to_the_route()
    {
        var scenario = MockScenario.Parse("""
            {
              "routes": [ { "match": { "path": "/data" }, "respond": { "status": 200, "body": "secret" } } ],
              "require": { "bearer": "letmein" }
            }
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("Authorization", "Bearer letmein");

        var response = await http.GetAsync($"http://127.0.0.1:{mock.Port}/data");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("secret", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_403_requirement_refuses_with_403()
    {
        var scenario = MockScenario.Parse("""
            {"routes":[{"match":{"path":"/x"},"respond":{"status":200}}],
             "require":{"bearer":"t","onFail":403}}
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Http, scenario: scenario);
        using var http = new HttpClient();

        var response = await http.GetAsync($"http://127.0.0.1:{mock.Port}/x");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_client_certificate_requirement_is_satisfied_by_a_real_mtls_handshake()
    {
        // End to end: the mock demands a certificate, the client presents one from the same
        // authority, and the route answers.
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("Corp Issuing CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", ca, serverAuth: true, clientAuth: false, dnsNames: new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "Client", ca, serverAuth: false, clientAuth: true);

        var scenario = MockScenario.Parse("""
            {"routes":[{"match":{"path":"/secure"},"respond":{"status":200,"body":"admitted"}}],
             "require":{"clientCert":{"issuer":"Corp Issuing CA"}}}
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Mtls, serverCert, scenario: scenario);

        var response = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = $"https://127.0.0.1:{mock.Port}/secure" },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.True(response.IsSuccess, response.Error?.Message);
        Assert.Equal("admitted", System.Text.Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public async Task A_certificate_from_the_wrong_authority_is_refused_with_a_challenge()
    {
        using var wantedCa = SelfSignedCertificateFactory.CreateCertificateAuthority("Corp Issuing CA");
        using var otherCa = SelfSignedCertificateFactory.CreateCertificateAuthority("Some Other CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate(
            "localhost", wantedCa, serverAuth: true, clientAuth: false, dnsNames: new[] { "localhost" });
        using var wrongClient = SelfSignedCertificateFactory.CreateSignedCertificate(
            "Client", otherCa, serverAuth: false, clientAuth: true);

        var scenario = MockScenario.Parse("""
            {"routes":[{"match":{"path":"/secure"},"respond":{"status":200,"body":"admitted"}}],
             "require":{"clientCert":{"issuer":"Corp Issuing CA"}}}
            """);
        await using var mock = MockServer.Start(0, MockTlsMode.Mtls, serverCert, scenario: scenario);

        var response = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = $"https://127.0.0.1:{mock.Port}/secure" },
            wrongClient,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        // The handshake succeeds — the mock accepts any certificate at the TLS layer — and the
        // refusal happens at the application layer, which is how a real endpoint behaves.
        Assert.Equal(401, response.StatusCode);
        Assert.DoesNotContain("admitted", System.Text.Encoding.UTF8.GetString(response.Body));
    }
}
