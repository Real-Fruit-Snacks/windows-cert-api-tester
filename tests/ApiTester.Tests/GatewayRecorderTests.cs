using System.IO;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Covers the recorder as data: an exchange it records must round-trip through the same
/// HAR reader and replay source the rest of the product uses (capture here, replay anywhere),
/// secrets are redacted unless kept, and concurrent appends do not lose entries — the gateway
/// records from many connection threads at once.</summary>
public class GatewayRecorderTests
{
    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoHeaders =
        Array.Empty<KeyValuePair<string, string>>();

    private static Har SaveAndReload(GatewayRecorder recorder) => SaveAndReload(recorder, out _);

    /// <summary>Writes the recording to a real file and reads it back, also handing out the raw
    /// on-disk text — a "the artifact never carried the secret" assertion has to look at the bytes
    /// that were written, not merely at the parsed headers.</summary>
    private static Har SaveAndReload(GatewayRecorder recorder, out string rawText)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rec-{Guid.NewGuid():N}.har");
        try
        {
            recorder.Save(path);
            rawText = File.ReadAllText(path);
            return HarReader.Parse(rawText);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void A_recorded_exchange_round_trips_through_the_replay_source()
    {
        var recorder = new GatewayRecorder();
        recorder.Record(
            "GET", "http://127.0.0.1:8080/orders?status=open",
            new[] { new KeyValuePair<string, string>("Accept", "application/json") },
            Array.Empty<byte>(), null,
            200, "OK",
            new[] { new KeyValuePair<string, string>("Content-Type", "application/json") },
            Encoding.UTF8.GetBytes("{\"orders\":[]}"), 12.0);

        var replay = new HarReplaySource(SaveAndReload(recorder));
        var served = replay.Match("GET", "/orders?status=open");

        Assert.NotNull(served);
        Assert.Equal(200, served!.Status);
        Assert.Equal("{\"orders\":[]}", Encoding.UTF8.GetString(served.Body));
        Assert.Equal("application/json", served.ContentType);
    }

    [Fact]
    public void Secrets_are_redacted_by_default_and_kept_only_when_asked()
    {
        var authHeader = new[] { new KeyValuePair<string, string>("Authorization", "Bearer super-secret") };

        var redacted = new GatewayRecorder();
        redacted.Record("GET", "http://h/x", authHeader, Array.Empty<byte>(), null,
            200, "OK", NoHeaders, Array.Empty<byte>(), 1.0);
        var redactedHar = SaveAndReload(redacted, out string rawText);
        var reqHeaders = redactedHar.Log.Entries[0].Request.Headers;
        Assert.Contains(reqHeaders, h => h.Name == "Authorization" && h.Value == "REDACTED");
        Assert.DoesNotContain("super-secret", rawText);   // the file itself, not just the parse

        var kept = new GatewayRecorder(includeSecrets: true);
        kept.Record("GET", "http://h/x", authHeader, Array.Empty<byte>(), null,
            200, "OK", NoHeaders, Array.Empty<byte>(), 1.0);
        var keptHeaders = SaveAndReload(kept).Log.Entries[0].Request.Headers;
        Assert.Contains(keptHeaders, h => h.Name == "Authorization" && h.Value == "Bearer super-secret");
    }

    [Fact]
    public void A_credential_in_the_recorded_url_is_redacted_like_the_header_beside_it()
    {
        // `serve --record` exists to produce an archive that goes somewhere else — replayed
        // offline, handed to a teammate. The Authorization header was redacted and the credential
        // in the URL two lines away was not.
        var recorder = new GatewayRecorder();
        recorder.Record("GET", "http://h/x?api_key=url-secret&page=2", NoHeaders, Array.Empty<byte>(), null,
            200, "OK", NoHeaders, Array.Empty<byte>(), 1.0);

        var har = SaveAndReload(recorder, out string rawText);
        var request = har.Log.Entries[0].Request;

        Assert.DoesNotContain("url-secret", rawText);      // the file itself
        Assert.Contains("api_key=REDACTED", request.Url);
        Assert.Contains("page=2", request.Url);            // a harmless parameter survives
        Assert.Equal("REDACTED", request.QueryString.Single(q => q.Name == "api_key").Value);
    }

    [Fact]
    public void The_recorded_url_keeps_its_credential_when_secrets_are_kept()
    {
        var recorder = new GatewayRecorder(includeSecrets: true);
        recorder.Record("GET", "http://h/x?api_key=url-secret", NoHeaders, Array.Empty<byte>(), null,
            200, "OK", NoHeaders, Array.Empty<byte>(), 1.0);

        Assert.Contains("api_key=url-secret", SaveAndReload(recorder).Log.Entries[0].Request.Url);
    }

    [Fact]
    public void Framing_headers_are_dropped_so_a_replay_never_re_frames_a_response()
    {
        var recorder = new GatewayRecorder();
        recorder.Record("GET", "http://h/x", NoHeaders, Array.Empty<byte>(), null,
            200, "OK",
            new[]
            {
                new KeyValuePair<string, string>("Content-Length", "5"),
                new KeyValuePair<string, string>("Transfer-Encoding", "chunked"),
                new KeyValuePair<string, string>("X-Real", "kept")
            },
            Encoding.UTF8.GetBytes("hello"), 1.0);

        var responseHeaders = SaveAndReload(recorder).Log.Entries[0].Response.Headers;
        Assert.DoesNotContain(responseHeaders, h => h.Name == "Content-Length");
        Assert.DoesNotContain(responseHeaders, h => h.Name == "Transfer-Encoding");
        Assert.Contains(responseHeaders, h => h.Name == "X-Real");
    }

    [Fact]
    public void Concurrent_records_lose_nothing()
    {
        var recorder = new GatewayRecorder();
        Parallel.For(0, 200, i =>
            recorder.Record("GET", $"http://h/item/{i}", NoHeaders, Array.Empty<byte>(), null,
                200, "OK", NoHeaders, Encoding.UTF8.GetBytes($"item {i}"), 1.0));

        Assert.Equal(200, recorder.Count);
        Assert.Equal(200, SaveAndReload(recorder).Log.Entries.Count);
    }
}
