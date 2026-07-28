using System.Net;
using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Drives the RFC 8628 device-code flow against a scripted plain-HTTP loopback: the
/// device endpoint hands out codes, the token endpoint answers from a queue, and the injected
/// delay records what the client would have waited — so the interval arithmetic is asserted as
/// data and no test ever races a clock.</summary>
public class DeviceFlowTests : IAsyncLifetime
{
    private HttpListener _listener = null!;
    private string _base = null!;
    private readonly Queue<string> _tokenResponses = new();
    private int _devicePayloadInterval;
    private int _devicePayloadExpires = 300;
    private int _tokenPolls;
    private Task? _serving;

    public Task InitializeAsync()
    {
        int port = Cli.ServeFixture.FreePort();
        _base = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_base);
        _listener.Start();
        _serving = ServeAsync();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _listener.Stop();
        try { if (_serving is not null) await _serving; } catch { /* listener stopped */ }
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; }   // listener stopped

            string body = ctx.Request.Url!.AbsolutePath.EndsWith("/device", StringComparison.Ordinal)
                ? $$"""
                    {"device_code":"dev-123","user_code":"WDJB-MJHT",
                     "verification_uri":"https://example.test/activate",
                     "verification_uri_complete":"https://example.test/activate?user_code=WDJB-MJHT",
                     "interval":{{_devicePayloadInterval}},"expires_in":{{_devicePayloadExpires}}}
                    """
                : NextTokenResponse();
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private string NextTokenResponse()
    {
        Interlocked.Increment(ref _tokenPolls);
        return _tokenResponses.Count > 0
            ? _tokenResponses.Dequeue()
            : """{"error":"authorization_pending"}""";
    }

    private OAuthRequest Request() => new()
    {
        Grant = OAuthGrant.DeviceCode,
        TokenEndpoint = _base + "token",
        DeviceAuthorizationEndpoint = _base + "device",
        ClientId = "cid",
        Scope = "api.read"
    };

    private static Func<TimeSpan, CancellationToken, Task> Recording(List<TimeSpan> into) =>
        (delay, _) => { into.Add(delay); return Task.CompletedTask; };

    [Fact]
    public async Task Pending_then_approved_yields_the_token_and_the_prompt_was_delivered()
    {
        _tokenResponses.Enqueue("""{"error":"authorization_pending"}""");
        _tokenResponses.Enqueue("""{"error":"authorization_pending"}""");
        _tokenResponses.Enqueue("""{"access_token":"tok-999","token_type":"Bearer","expires_in":3600}""");

        OAuthDevicePrompt? prompt = null;
        var delays = new List<TimeSpan>();
        var result = await OAuthClient.RequestDeviceTokenAsync(
            Request(), p => prompt = p, delay: Recording(delays));

        Assert.True(result.Success, result.FailureMessage);
        Assert.Equal("tok-999", result.AccessToken);
        Assert.NotNull(prompt);
        Assert.Equal("WDJB-MJHT", prompt!.UserCode);
        Assert.Equal("https://example.test/activate", prompt.VerificationUri);
        Assert.Contains("user_code=WDJB-MJHT", prompt.VerificationUriComplete);
        Assert.Equal(3, _tokenPolls);
    }

    [Fact]
    public async Task Slow_down_raises_the_polling_interval_by_five_seconds_per_the_rfc()
    {
        _devicePayloadInterval = 1;
        _tokenResponses.Enqueue("""{"error":"slow_down"}""");
        _tokenResponses.Enqueue("""{"error":"slow_down"}""");
        _tokenResponses.Enqueue("""{"access_token":"tok-1"}""");

        var delays = new List<TimeSpan>();
        var result = await OAuthClient.RequestDeviceTokenAsync(
            Request(), _ => { }, delay: Recording(delays));

        Assert.True(result.Success, result.FailureMessage);
        // 1s before the first poll; +5 after each slow_down: 6s, then 11s.
        Assert.Equal(new[] { 1.0, 6.0, 11.0 }, delays.Select(d => d.TotalSeconds));
    }

    [Fact]
    public async Task Access_denied_stops_the_flow_with_that_answer()
    {
        _tokenResponses.Enqueue("""{"error":"access_denied","error_description":"the user said no"}""");

        var result = await OAuthClient.RequestDeviceTokenAsync(
            Request(), _ => { }, delay: Recording(new List<TimeSpan>()));

        Assert.False(result.Success);
        Assert.Equal("access_denied", result.Error);
        Assert.Equal(1, _tokenPolls);
    }

    [Fact]
    public async Task An_expired_code_stops_the_flow_without_polling_forever()
    {
        _devicePayloadInterval = 10;
        _devicePayloadExpires = 25;   // room for two polls (10s + 10s), never a third

        var result = await OAuthClient.RequestDeviceTokenAsync(
            Request(), _ => { }, delay: Recording(new List<TimeSpan>()));

        Assert.False(result.Success);
        Assert.Equal("expired_token", result.Error);
        Assert.Equal(3, _tokenPolls);   // 25s at 10s intervals: polls after 10, 20, 30 — then out of time
    }

    [Fact]
    public async Task A_device_endpoint_error_is_the_answer_and_the_token_endpoint_is_never_polled()
    {
        // Reusing the token queue for the device endpoint is not possible — it answers /device —
        // so the fixture's device payload is overridden by pointing the request at /token, whose
        // scripted error response stands in for a refusing device endpoint.
        _tokenResponses.Enqueue("""{"error":"invalid_client","error_description":"unknown client"}""");
        var request = Request() with { DeviceAuthorizationEndpoint = _base + "token" };

        var result = await OAuthClient.RequestDeviceTokenAsync(
            request, _ => { }, delay: Recording(new List<TimeSpan>()));

        Assert.False(result.Success);
        Assert.Equal("invalid_client", result.Error);
        Assert.Equal(1, _tokenPolls);   // the one scripted answer; no polling followed it
    }
}
