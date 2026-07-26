using ApiTester.App;
using ApiTester.Core;

namespace ApiTester.Tests;

public class HarNetworkExportTests
{
    [Fact]
    public void ToHar_produces_an_honest_partial_when_only_metadata_was_kept()
    {
        var entry = new NetworkEntry
        {
            Method = "GET",
            Url = "https://h/x",
            StatusCode = 200,
            Size = 1234,
            ContentType = "application/json",
            ElapsedMs = 12
        };

        var json = HarNetworkExport.ToHar(new[] { entry }, includeSecrets: false, creatorVersion: "1.52.0");
        var har = HarReader.Parse(json);

        var parsed = Assert.Single(har.Log.Entries);
        Assert.Equal(200, parsed.Response.Status);
        Assert.Equal(1234, parsed.Response.Content.Size);
        Assert.Equal("", parsed.Response.Content.Text);
    }

    [Fact]
    public void ToHar_redacts_authorization_header_by_default_but_not_when_secrets_included()
    {
        var entry = new NetworkEntry
        {
            Method = "GET",
            Url = "https://h/x",
            RequestHeaders = { new("Authorization", "Bearer secret") }
        };

        var redactedJson = HarNetworkExport.ToHar(new[] { entry }, includeSecrets: false, creatorVersion: "1.52.0");
        var redacted = HarReader.Parse(redactedJson);
        var redactedAuth = Assert.Single(redacted.Log.Entries).Request.Headers.Single(h => h.Name == "Authorization");
        Assert.Equal("[redacted]", redactedAuth.Value);

        var fullJson = HarNetworkExport.ToHar(new[] { entry }, includeSecrets: true, creatorVersion: "1.52.0");
        var full = HarReader.Parse(fullJson);
        var fullAuth = Assert.Single(full.Log.Entries).Request.Headers.Single(h => h.Name == "Authorization");
        Assert.Equal("Bearer secret", fullAuth.Value);
    }

    [Fact]
    public void ToHar_carries_client_certificate_facts_into_the_certapi_extension_block()
    {
        var entry = new NetworkEntry
        {
            Method = "GET",
            Url = "https://h/x",
            ClientCertPresented = true,
            ClientCertSubject = "CN=Me"
        };

        var json = HarNetworkExport.ToHar(new[] { entry }, includeSecrets: true, creatorVersion: "1.52.0");
        var har = HarReader.Parse(json);

        var parsed = Assert.Single(har.Log.Entries);
        Assert.NotNull(parsed.Certapi);
        Assert.True(parsed.Certapi!.ClientCertificateSent);
        Assert.Equal("CN=Me", parsed.Certapi.ClientCertificateSubject);
    }
}
