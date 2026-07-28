using System.Net.Http;
using ApiTester.Core;

namespace ApiTester.Tests;

public class HarWriterTests
{
    private static ApiRequest MakeRequest(IEnumerable<KeyValuePair<string, string>>? headers = null, string? body = null, string? contentType = null)
        => new ApiRequest
        {
            Method = HttpMethod.Post,
            Url = "https://example.test/api/widgets?x=1",
            Headers = (headers ?? Array.Empty<KeyValuePair<string, string>>()).ToList(),
            Body = body,
            ContentType = contentType
        };

    // ---------------------------------------------------------------- credentials in the URL

    private static ApiRequest RequestTo(string url) => new()
    {
        Method = HttpMethod.Get,
        Url = url,
        Headers = new List<KeyValuePair<string, string>>
            { new("Authorization", "Bearer header-secret") },
    };

    [Fact]
    public void A_credential_in_the_query_string_is_redacted_like_the_header_beside_it()
    {
        // An archive is a file people attach to tickets and hand to teammates — the same reason the
        // Authorization header is redacted. `?api_key=` is a credential that merely looks like part
        // of an address, and it was being written out in full next to the redacted header.
        var entry = HarWriter.FromExchange(
            RequestTo("https://api.internal/orders?api_key=url-secret&page=2"),
            MakeResponse(), includeSecrets: false);

        Assert.DoesNotContain("url-secret", entry.Request.Url);
        Assert.Contains("api_key=REDACTED", entry.Request.Url);
        Assert.Contains("page=2", entry.Request.Url);      // a harmless parameter survives
    }

    [Fact]
    public void The_query_array_cannot_disagree_with_the_url_it_came_from()
    {
        // The archive states the request twice; a reader (or a replay) may consult either.
        var entry = HarWriter.FromExchange(
            RequestTo("https://api.internal/orders?token=url-secret"),
            MakeResponse(), includeSecrets: false);

        var token = entry.Request.QueryString.Single(q => q.Name == "token");
        Assert.Equal("REDACTED", token.Value);
        Assert.DoesNotContain("url-secret", entry.Request.Url);
    }

    [Fact]
    public void A_password_in_the_address_itself_is_redacted_too()
    {
        var entry = HarWriter.FromExchange(
            RequestTo("https://svc:hunter2@api.internal/orders"),
            MakeResponse(), includeSecrets: false);

        Assert.DoesNotContain("hunter2", entry.Request.Url);
        Assert.Contains("svc:REDACTED@", entry.Request.Url);
    }

    [Fact]
    public void Include_secrets_keeps_the_url_exactly_as_sent()
    {
        // The escape hatch has to be complete, or a replay that needs fidelity has no way to get it.
        string url = "https://svc:hunter2@api.internal/orders?api_key=url-secret";
        var entry = HarWriter.FromExchange(RequestTo(url), MakeResponse(), includeSecrets: true);

        Assert.Equal(url, entry.Request.Url);
        Assert.Equal("url-secret", entry.Request.QueryString.Single(q => q.Name == "api_key").Value);
    }

    [Fact]
    public void The_redirect_form_redacts_too_which_is_the_one_the_cli_actually_calls()
    {
        // FromExchangeWithRedirects overwrites the URL that FromExchange built, so redacting only
        // inside FromExchange applied the fix and then undid it one line later. Every `--har` on
        // the command line goes through this path, and the unit tests above went through the other
        // one — which is how that survived until an actual capture was inspected.
        var entry = Assert.Single(HarWriter.FromExchangeWithRedirects(
            RequestTo("https://api.internal/orders?api_key=url-secret"),
            MakeResponse(), includeSecrets: false));

        Assert.DoesNotContain("url-secret", entry.Request.Url);
        Assert.Equal("REDACTED", entry.Request.QueryString.Single(q => q.Name == "api_key").Value);
    }

    [Fact]
    public void A_url_with_nothing_secret_in_it_is_untouched()
    {
        string url = "https://api.internal/orders?page=2&sort=name";
        var entry = HarWriter.FromExchange(RequestTo(url), MakeResponse(), includeSecrets: false);

        Assert.Equal(url, entry.Request.Url);
    }

    private static ApiResponse MakeResponse(int status = 200, string? reason = "OK",
        IEnumerable<KeyValuePair<string, string>>? headers = null, byte[]? body = null, string? contentType = null,
        TimeSpan? elapsed = null, ConnectionInfo? connection = null, IReadOnlyList<RedirectHop>? redirects = null)
        => new ApiResponse
        {
            StatusCode = status,
            ReasonPhrase = reason,
            Headers = (headers ?? Array.Empty<KeyValuePair<string, string>>()).ToList(),
            Body = body ?? Array.Empty<byte>(),
            ContentType = contentType,
            Elapsed = elapsed ?? TimeSpan.FromMilliseconds(42),
            Connection = connection,
            Redirects = redirects ?? Array.Empty<RedirectHop>()
        };

    [Fact]
    public void FromExchange_keeps_authorization_header_intact_when_secrets_included()
    {
        var request = MakeRequest(headers: new[] { new KeyValuePair<string, string>("Authorization", "Bearer secret-token") });
        var response = MakeResponse();

        var entry = HarWriter.FromExchange(request, response, includeSecrets: true);

        var authHeader = entry.Request.Headers.Single(h => h.Name == "Authorization");
        Assert.Equal("Bearer secret-token", authHeader.Value);
    }

    [Fact]
    public void FromExchange_redacts_authorization_and_cookie_header_values_when_secrets_excluded()
    {
        var request = MakeRequest(headers: new[]
        {
            new KeyValuePair<string, string>("Authorization", "Bearer secret-token"),
            new KeyValuePair<string, string>("Cookie", "session=abc123")
        });
        var response = MakeResponse(headers: new[] { new KeyValuePair<string, string>("Set-Cookie", "session=abc123; Path=/") });

        var entry = HarWriter.FromExchange(request, response, includeSecrets: false);

        var authHeader = entry.Request.Headers.Single(h => h.Name == "Authorization");
        var cookieHeader = entry.Request.Headers.Single(h => h.Name == "Cookie");
        var setCookieHeader = entry.Response.Headers.Single(h => h.Name == "Set-Cookie");
        Assert.Equal("[redacted]", authHeader.Value);
        Assert.Equal("[redacted]", cookieHeader.Value);
        Assert.Equal("[redacted]", setCookieHeader.Value);
    }

    [Fact]
    public void FromExchange_drops_access_token_and_password_from_json_body_when_secrets_excluded()
    {
        var request = MakeRequest(
            body: "{\"username\":\"alice\",\"password\":\"hunter2\",\"access_token\":\"abc\",\"other\":\"keep-me\"}",
            contentType: "application/json");
        var response = MakeResponse();

        var entry = HarWriter.FromExchange(request, response, includeSecrets: false);

        Assert.NotNull(entry.Request.PostData);
        var bodyText = entry.Request.PostData!.Text;
        Assert.DoesNotContain("password", bodyText);
        Assert.DoesNotContain("access_token", bodyText);
        Assert.DoesNotContain("hunter2", bodyText);
        Assert.Contains("keep-me", bodyText);
        Assert.Contains("alice", bodyText);
    }

    [Fact]
    public void FromExchange_encodes_binary_response_body_as_base64()
    {
        byte[] binaryBody = { 0, 1, 2, 255, 254 };
        var request = MakeRequest();
        var response = MakeResponse(body: binaryBody, contentType: "application/octet-stream");

        var entry = HarWriter.FromExchange(request, response, includeSecrets: true);

        Assert.Equal("base64", entry.Response.Content.Encoding);
        var decoded = Convert.FromBase64String(entry.Response.Content.Text);
        Assert.Equal(binaryBody, decoded);
    }

    [Fact]
    public void FromExchangeWithRedirects_yields_one_entry_per_hop_plus_the_final_response()
    {
        var request = MakeRequest();
        var redirects = new List<RedirectHop>
        {
            new(301, "https://example.test/api/widgets?x=1", "https://example.test/v2/widgets?x=1", false, false),
            new(302, "https://example.test/v2/widgets?x=1", "https://example.test/final/widgets?x=1", false, false)
        };
        var response = MakeResponse(status: 200, redirects: redirects);

        var entries = HarWriter.FromExchangeWithRedirects(request, response, includeSecrets: true);

        Assert.Equal(3, entries.Count);
        Assert.Equal(301, entries[0].Response.Status);
        Assert.Equal("https://example.test/v2/widgets?x=1", entries[0].Response.RedirectUrl);
        Assert.Equal(302, entries[1].Response.Status);
        Assert.Equal("https://example.test/final/widgets?x=1", entries[1].Response.RedirectUrl);
        Assert.Equal(200, entries[2].Response.Status);
        Assert.Equal("https://example.test/final/widgets?x=1", entries[2].Request.Url);
    }

    [Fact]
    public void FromExchange_populates_certapi_block_from_connection_info()
    {
        var request = MakeRequest();
        var connection = new ConnectionInfo
        {
            ClientCertificateSent = true,
            TlsProtocol = "Tls13",
            ServerCertificateThumbprint = "ABCDEF1234567890"
        };
        var response = MakeResponse(connection: connection);

        var entry = HarWriter.FromExchange(request, response, includeSecrets: true);

        Assert.NotNull(entry.Certapi);
        Assert.True(entry.Certapi!.ClientCertificateSent);
        Assert.Equal("Tls13", entry.Certapi.TlsProtocol);
        Assert.Equal("ABCDEF1234567890", entry.Certapi.ServerCertificateThumbprint);

        var json = HarWriter.Write(new[] { entry }, "1.52.0");
        Assert.Contains("\"_certapi\"", json);
        Assert.Contains("\"tlsProtocol\"", json);
    }
}
