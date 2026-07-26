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
