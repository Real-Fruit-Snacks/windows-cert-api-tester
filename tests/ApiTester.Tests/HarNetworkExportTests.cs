using ApiTester.App;
using ApiTester.Core;

namespace ApiTester.Tests;

public class HarNetworkExportTests
{
    [Fact]
    public void A_credential_in_the_exported_url_is_redacted()
    {
        // The app's "Export Network trace as HAR…" builds its entries itself rather than going
        // through the send path's builder, so it needed the same rule applied rather than inherited.
        var entry = new NetworkEntry
        {
            Method = "GET",
            Url = "https://svc:hunter2@h/x?token=url-secret",
            StatusCode = 200,
            ElapsedMs = 12
        };

        string json = HarNetworkExport.ToHar(new[] { entry }, includeSecrets: false, creatorVersion: "1.0.0");

        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("url-secret", json);
        Assert.Contains("svc:REDACTED@", json);
        Assert.Contains("token=REDACTED", json);
    }

    [Fact]
    public void The_exported_url_is_untouched_when_secrets_are_kept()
    {
        var entry = new NetworkEntry { Method = "GET", Url = "https://h/x?token=url-secret", StatusCode = 200 };

        string json = HarNetworkExport.ToHar(new[] { entry }, includeSecrets: true, creatorVersion: "1.0.0");

        Assert.Contains("token=url-secret", json);
    }

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
