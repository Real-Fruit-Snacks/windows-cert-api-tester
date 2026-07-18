using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;

namespace ApiTester.Tests;

public class OAuthTests
{
    private static (X509Certificate2 ca, X509Certificate2 server, X509Certificate2 client) Certs()
    {
        var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        var server = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        var client = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        return (ca, server, client);
    }

    [Fact]
    public async Task Client_credentials_grant_returns_a_token()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var srv = await LoopbackMtlsServer.StartOAuthTokenAsync(server, client.Thumbprint!, "app", "s3cret");
            var result = await OAuthClient.RequestTokenAsync(new OAuthRequest
            {
                Grant = OAuthGrant.ClientCredentials,
                TokenEndpoint = srv.BaseUrl,
                ClientId = "app",
                ClientSecret = "s3cret",
                Scope = "api.read api.write"
            }, client, ignoreServerCertificateErrors: true);

            Assert.True(result.Success, result.FailureMessage);
            Assert.Equal("at-client_credentials", result.AccessToken);
            Assert.Equal("rt-client_credentials", result.RefreshToken);
            Assert.Equal("Bearer", result.TokenType);
            Assert.Equal(3600, result.ExpiresInSeconds);
            Assert.NotNull(result.ExpiresUtc);
            Assert.Equal("api.read api.write", result.Scope);
        }
    }

    [Fact]
    public async Task Client_secret_basic_authenticates_via_the_header()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var srv = await LoopbackMtlsServer.StartOAuthTokenAsync(server, client.Thumbprint!, "app", "s3cret");
            var result = await OAuthClient.RequestTokenAsync(new OAuthRequest
            {
                Grant = OAuthGrant.ClientCredentials,
                TokenEndpoint = srv.BaseUrl,
                ClientId = "app",
                ClientSecret = "s3cret",
                ClientAuth = OAuthClientAuth.Basic
            }, client, ignoreServerCertificateErrors: true);

            Assert.True(result.Success, result.FailureMessage);
            Assert.Equal("at-client_credentials", result.AccessToken);
        }
    }

    [Fact]
    public async Task Password_and_refresh_grants_flow_through()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var srv = await LoopbackMtlsServer.StartOAuthTokenAsync(server, client.Thumbprint!, "app", "s3cret");

            var pwd = await OAuthClient.RequestTokenAsync(new OAuthRequest
            {
                Grant = OAuthGrant.Password, TokenEndpoint = srv.BaseUrl,
                ClientId = "app", ClientSecret = "s3cret", Username = "ada", Password = "pw"
            }, client, ignoreServerCertificateErrors: true);
            Assert.True(pwd.Success, pwd.FailureMessage);
            Assert.Equal("at-password", pwd.AccessToken);

            var refreshed = await OAuthClient.RequestTokenAsync(new OAuthRequest
            {
                Grant = OAuthGrant.RefreshToken, TokenEndpoint = srv.BaseUrl,
                ClientId = "app", ClientSecret = "s3cret", RefreshToken = "rt-password"
            }, client, ignoreServerCertificateErrors: true);
            Assert.True(refreshed.Success, refreshed.FailureMessage);
            Assert.Equal("at-refresh_token", refreshed.AccessToken);
        }
    }

    [Fact]
    public async Task Bad_client_secret_is_a_typed_error()
    {
        var (ca, server, client) = Certs();
        using (ca) using (server) using (client)
        {
            await using var srv = await LoopbackMtlsServer.StartOAuthTokenAsync(server, client.Thumbprint!, "app", "s3cret");
            var result = await OAuthClient.RequestTokenAsync(new OAuthRequest
            {
                Grant = OAuthGrant.ClientCredentials, TokenEndpoint = srv.BaseUrl,
                ClientId = "app", ClientSecret = "WRONG"
            }, client, ignoreServerCertificateErrors: true);

            Assert.False(result.Success);
            Assert.Equal("invalid_client", result.Error);
            Assert.Contains("bad client credentials", result.FailureMessage);
        }
    }

    [Fact]
    public void Pkce_challenge_is_the_base64url_sha256_of_the_verifier()
    {
        // RFC 7636 Appendix B reference vector.
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            OAuthAuthorization.CodeChallenge(verifier));
    }

    [Fact]
    public async Task Redirect_listener_captures_the_code_and_state()
    {
        int port = OAuthRedirect.FreeLoopbackPort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listen = OAuthRedirect.WaitForCodeAsync(port, cts.Token);

        using var http = new System.Net.Http.HttpClient();
        var page = await http.GetStringAsync($"http://127.0.0.1:{port}/callback?code=the-code&state=the-state", cts.Token);

        var result = await listen;
        Assert.Equal("the-code", result.Code);
        Assert.Equal("the-state", result.State);
        Assert.Null(result.Error);
        Assert.Contains("Authorization complete", page);
    }

    [Fact]
    public async Task Redirect_listener_surfaces_an_error_response()
    {
        int port = OAuthRedirect.FreeLoopbackPort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listen = OAuthRedirect.WaitForCodeAsync(port, cts.Token);

        using var http = new System.Net.Http.HttpClient();
        await http.GetStringAsync($"http://127.0.0.1:{port}/callback?error=access_denied&error_description=user%20said%20no", cts.Token);

        var result = await listen;
        Assert.Equal("access_denied", result.Error);
        Assert.Equal("user said no", result.ErrorDescription);
        Assert.Null(result.Code);
    }

    [Fact]
    public void Authorization_url_carries_pkce_and_state()
    {
        string verifier = OAuthAuthorization.CreateCodeVerifier();
        string challenge = OAuthAuthorization.CodeChallenge(verifier);
        string url = OAuthAuthorization.BuildAuthorizationUrl(
            "https://auth.example.com/authorize", "client-123",
            "http://127.0.0.1:5005/callback", "openid profile", "xyz-state", challenge);

        Assert.StartsWith("https://auth.example.com/authorize?", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("client_id=client-123", url);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A5005%2Fcallback", url);
        Assert.Contains("scope=openid%20profile", url);
        Assert.Contains("state=xyz-state", url);
        Assert.Contains($"code_challenge={challenge}", url);
        Assert.Contains("code_challenge_method=S256", url);
    }
}
