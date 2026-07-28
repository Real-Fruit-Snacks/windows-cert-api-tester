using System.IO;
using System.Net.Http;
using ApiTester.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ApiTester.Tests;

/// <summary>The pool inspector: which connection served a request, and was it reused?
///
/// <para>The two runtime facts this rests on were established by probe rather than assumed, and
/// the tests that depend on them say so: <c>RequestHeadersStart</c> carries the
/// <c>connectionId</c> its request went out on, and those identifiers are unique across origins
/// within the process (two origins were observed receiving 0 and 1, not 0 and 0).</para></summary>
public class ConnectionInspectorTests
{
    private static async Task<WebApplication> StartKeepAliveAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapGet("/k", () => "ok");
        await app.StartAsync();
        return app;
    }

    /// <summary>This inspector watches the whole process by design, and the test suite runs in
    /// parallel, so every assertion here is scoped to the origin the test itself created — exactly
    /// as the commands scope their reports. Asserting over every connection the process happened to
    /// make would pass alone and fail in the full suite, which is precisely how this was found.</summary>
    private static IReadOnlyList<ConnectionRecord> Mine(ConnectionInspector inspector, WebApplication app)
    {
        string origin = ConnectionInspector.OriginOf(new Uri(app.Urls.First()));
        return inspector.Connections.Where(c => c.Origin == origin).ToArray();
    }

    [Fact]
    public async Task Repeated_requests_share_one_connection_and_it_is_counted()
    {
        await using var app = await StartKeepAliveAsync();
        string url = app.Urls.First() + "/k";

        using var inspector = new ConnectionInspector();
        var client = new ApiClient();
        var request = new ApiRequest { Method = HttpMethod.Get, Url = url };
        for (int i = 0; i < 3; i++) Assert.True((await client.SendAsync(request, null)).IsSuccess);

        var connection = Assert.Single(Mine(inspector, app));
        Assert.Equal(3, connection.Requests);
        Assert.Equal(new Uri(url).Port, connection.Port);
        Assert.Equal("1.1", connection.Version);

        string report = inspector.Render(ConnectionInspector.OriginOf(new Uri(url)));
        Assert.Contains("3 request(s) over 1 connection(s)", report);
        Assert.Contains("Connections are being reused", report);
    }

    [Fact]
    public async Task Two_origins_get_two_connections_each_with_its_own_count()
    {
        // Guards the identifier assumption directly: if ids collided across origins, these two
        // connections would merge into one record and the counts would be wrong.
        await using var first = await StartKeepAliveAsync();
        await using var second = await StartKeepAliveAsync();

        using var inspector = new ConnectionInspector();
        var client = new ApiClient();
        foreach (var app in new[] { first, second, first })
            await client.SendAsync(new ApiRequest { Method = HttpMethod.Get, Url = app.Urls.First() + "/k" }, null);

        var mine = Mine(inspector, first).Concat(Mine(inspector, second)).ToArray();
        Assert.Equal(2, mine.Length);
        Assert.Equal(3, mine.Sum(c => c.Requests));
        Assert.Equal(2, mine.Select(c => c.Origin).Distinct().Count());
    }

    [Fact]
    public async Task The_connection_a_request_used_is_identified()
    {
        await using var app = await StartKeepAliveAsync();

        using var inspector = new ConnectionInspector();
        await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = app.Urls.First() + "/k" }, null);

        var used = Assert.Single(Mine(inspector, app));
        Assert.True(inspector.WasEstablishedHere(used.Id));    // opened during this run, not before
        Assert.Equal(1, used.Requests);
    }

    [Fact]
    public void With_nothing_observed_the_report_says_so_rather_than_printing_an_empty_table()
    {
        using var inspector = new ConnectionInspector();
        // Scoped to an origin nothing will ever connect to, so the suite's own traffic cannot
        // wander into the answer.
        Assert.Contains("No connections were opened", inspector.Render("https://nothing.invalid:443"));
    }

    [Fact]
    public async Task Connections_to_other_origins_are_excluded_but_still_acknowledged()
    {
        // The listener is process-wide; a report narrowed to one origin must not pretend the rest
        // of the process was idle.
        await using var mine = await StartKeepAliveAsync();
        await using var other = await StartKeepAliveAsync();

        using var inspector = new ConnectionInspector();
        var client = new ApiClient();
        await client.SendAsync(new ApiRequest { Method = HttpMethod.Get, Url = mine.Urls.First() + "/k" }, null);
        await client.SendAsync(new ApiRequest { Method = HttpMethod.Get, Url = other.Urls.First() + "/k" }, null);

        string report = inspector.Render(ConnectionInspector.OriginOf(new Uri(mine.Urls.First())));

        Assert.Contains("1 request(s) over 1 connection(s)", report);
        Assert.DoesNotContain(new Uri(other.Urls.First()).Port.ToString(), report);
        Assert.Contains("to other origins were open in this process", report);
    }

    // The renderer is pure over records, so the verdicts are tested as data — no socket can make
    // a "one connection per request" case appear on demand, but the report must handle it.

    [Fact]
    public void Reuse_and_no_reuse_get_different_verdicts()
    {
        var reused = new ConnectionRecord(1, "https", "api.example.com", 443,
            TimeSpan.FromMilliseconds(10), "1.1", "203.0.113.7") { Requests = 5 };
        Assert.Contains("Connections are being reused", ConnectionInspector.Render(new[] { reused }));

        var one = new ConnectionRecord(1, "https", "a", 443, TimeSpan.Zero, "1.1", "") { Requests = 1 };
        var two = new ConnectionRecord(2, "https", "a", 443, TimeSpan.Zero, "1.1", "") { Requests = 1 };
        string report = ConnectionInspector.Render(new[] { one, two });

        Assert.Contains("nothing is being reused", report);
        Assert.Contains("Connection: close", report);      // and why that happens
    }

    [Fact]
    public void A_single_request_is_not_called_a_reuse_failure()
    {
        // One request over one connection is the normal, correct shape of a single send. Scolding
        // the user about reuse there would be noise, and wrong.
        var only = new ConnectionRecord(1, "https", "a", 443, TimeSpan.Zero, "1.1", "") { Requests = 1 };
        string report = ConnectionInspector.Render(new[] { only });

        Assert.Contains("1 request(s) over 1 connection(s)", report);
        Assert.DoesNotContain("nothing is being reused", report);
    }

    [Fact]
    public void The_report_names_the_origin_version_and_peer()
    {
        var record = new ConnectionRecord(7, "https", "api.example.com", 8443,
            TimeSpan.FromMilliseconds(12.34), "2.0", "203.0.113.7") { Requests = 2 };
        string report = ConnectionInspector.Render(new[] { record });

        Assert.Contains("connection 7", report);
        Assert.Contains("https://api.example.com:8443", report);
        Assert.Contains("HTTP/2.0", report);
        Assert.Contains("203.0.113.7", report);
        Assert.Contains("requests 2", report);
    }
}
