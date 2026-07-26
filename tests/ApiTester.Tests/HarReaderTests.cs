using ApiTester.Core;

namespace ApiTester.Tests;

public class HarReaderTests
{
    [Fact]
    public void Parse_throws_HarFormatException_on_invalid_json()
    {
        Assert.Throws<HarFormatException>(() => HarReader.Parse("{ not json"));
    }

    [Fact]
    public void Parse_throws_HarFormatException_when_log_is_missing()
    {
        Assert.Throws<HarFormatException>(() => HarReader.Parse("{}"));
    }

    [Fact]
    public void Write_then_Parse_round_trips_creator_version_and_entries()
    {
        var entry = new HarEntry
        {
            StartedDateTime = DateTimeOffset.UtcNow,
            Time = 12.5,
            Request = new HarRequest { Method = "PUT", Url = "https://example.test/things/1" },
            Response = new HarResponse { Status = 204, StatusText = "No Content" }
        };

        string json = HarWriter.Write(new[] { entry }, "1.52.0");
        var har = HarReader.Parse(json);

        Assert.Equal("1.52.0", har.Log.Creator.Version);
        Assert.Single(har.Log.Entries);
        Assert.Equal("PUT", har.Log.Entries[0].Request.Method);
        Assert.Equal("https://example.test/things/1", har.Log.Entries[0].Request.Url);
        Assert.Equal(204, har.Log.Entries[0].Response.Status);
    }

    // A trimmed but realistic Chrome DevTools "Export HAR" entry: HTTP/2 pseudo-headers alongside
    // the real request headers, a queryString array, a JSON postData, and a response block.
    private const string ChromeExportedHarFixture = """
    {
      "log": {
        "version": "1.2",
        "creator": { "name": "WebInspector", "version": "537.36" },
        "entries": [
          {
            "startedDateTime": "2026-07-20T10:15:30.000Z",
            "time": 87.5,
            "request": {
              "method": "POST",
              "url": "https://api.example.com/v1/orders?source=web",
              "httpVersion": "HTTP/2",
              "headers": [
                { "name": ":method", "value": "POST" },
                { "name": ":authority", "value": "api.example.com" },
                { "name": ":scheme", "value": "https" },
                { "name": ":path", "value": "/v1/orders?source=web" },
                { "name": "Host", "value": "api.example.com" },
                { "name": "user-agent", "value": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" },
                { "name": "accept", "value": "application/json" },
                { "name": "content-type", "value": "application/json" }
              ],
              "queryString": [
                { "name": "source", "value": "web" }
              ],
              "postData": {
                "mimeType": "application/json",
                "text": "{\"item\":\"widget\",\"qty\":3}"
              },
              "headersSize": 320,
              "bodySize": 27
            },
            "response": {
              "status": 201,
              "statusText": "Created",
              "httpVersion": "HTTP/2",
              "headers": [
                { "name": "content-type", "value": "application/json" }
              ],
              "content": {
                "size": 40,
                "mimeType": "application/json",
                "text": "{\"id\":\"o-123\",\"status\":\"pending\"}"
              },
              "redirectURL": "",
              "headersSize": 120,
              "bodySize": 40
            },
            "cache": {},
            "timings": { "send": 0, "wait": 87, "receive": 0.5 }
          }
        ]
      }
    }
    """;

    [Fact]
    public void Parse_reads_real_browser_exported_har_fixture()
    {
        var har = HarReader.Parse(ChromeExportedHarFixture);

        Assert.Equal("1.2", har.Log.Version);
        Assert.Single(har.Log.Entries);
        Assert.Equal(201, har.Log.Entries[0].Response.Status);
    }

    [Fact]
    public void ToParsedRequest_excludes_http2_pseudo_headers_and_host_from_a_browser_exported_entry()
    {
        var har = HarReader.Parse(ChromeExportedHarFixture);
        var entry = har.Log.Entries[0];

        var parsed = HarReader.ToParsedRequest(entry);

        Assert.Equal("POST", parsed.Method);
        Assert.Equal("https://api.example.com/v1/orders?source=web", parsed.Url);
        Assert.Equal("{\"item\":\"widget\",\"qty\":3}", parsed.Body);
        Assert.Equal("application/json", parsed.ContentType);
        Assert.DoesNotContain(parsed.Headers, h => h.Key.StartsWith(':'));
        Assert.DoesNotContain(parsed.Headers, h => string.Equals(h.Key, "Host", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(parsed.Headers, h => h.Key == "user-agent" && h.Value == "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }
}
