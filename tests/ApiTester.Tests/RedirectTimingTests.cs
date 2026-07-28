using System.Net.Http;
using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>A redirect hop's own duration: measured by the client, reported in the hop chain, and
/// written into the HTTP Archive as a real `time` rather than a zero. A zero draws an instant hop
/// in every HAR viewer, which hides the case this data exists to expose — a chain of slow hops
/// rather than a slow destination.</summary>
public class RedirectTimingTests
{
    [Fact]
    public async Task Each_recorded_hop_carries_its_own_elapsed_time()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartRedirectChainAsync(serverCert, clientCert.Thumbprint!, hops: 2);

        var response = await new ApiClient().SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl },
            clientCert,
            transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        Assert.Equal(2, response.Redirects.Count);
        // Measured, not claimed: every hop took SOME time, and no hop can have taken longer than
        // the whole exchange. Asserting a range rather than a value keeps this off the clock.
        foreach (var hop in response.Redirects)
        {
            Assert.True(hop.Elapsed > TimeSpan.Zero, "a hop that ran should report a duration");
            Assert.True(hop.Elapsed <= response.Elapsed, "a hop cannot outlast the whole exchange");
        }
    }

    [Fact]
    public async Task Har_entries_for_hops_carry_the_measured_time_not_zero()
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("Client", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartRedirectChainAsync(serverCert, clientCert.Thumbprint!, hops: 1);

        var request = new ApiRequest { Method = HttpMethod.Get, Url = server.BaseUrl };
        var response = await new ApiClient().SendAsync(
            request, clientCert, transport: new TransportOptions { IgnoreServerCertificateErrors = true });

        var entries = HarWriter.FromExchangeWithRedirects(request, response, includeSecrets: false);

        Assert.Equal(2, entries.Count);          // one hop + the final response
        Assert.True(entries[0].Time > 0, "the hop entry should report the time it took");
        Assert.True(entries[0].Timings.Wait > 0, "the hop's wait timing should be the measured time");
    }

    [Fact]
    public void An_unmeasured_hop_reports_har_s_own_not_applicable_rather_than_zero()
    {
        // A hop constructed without a measurement (every pre-existing call site) must not claim a
        // duration: HAR spells "unknown" as -1, and 0 would read as instant.
        var request = new ApiRequest { Method = HttpMethod.Get, Url = "https://start.test/" };
        var response = new ApiResponse
        {
            StatusCode = 200,
            Redirects = new[] { new RedirectHop(302, "https://start.test/", "https://end.test/", false, false) }
        };

        var entries = HarWriter.FromExchangeWithRedirects(request, response, includeSecrets: false);

        Assert.Equal(0, entries[0].Time);
        Assert.Equal(-1, entries[0].Timings.Wait);
    }

    [Fact]
    public void The_hop_report_prints_a_duration_only_when_one_was_measured()
    {
        string measured = RedirectReport.Lines(new[]
        {
            new RedirectHop(302, "https://a.test/", "https://b.test/", false, false)
                { Elapsed = TimeSpan.FromMilliseconds(42) }
        });
        Assert.Contains("42 ms", measured);

        string unmeasured = RedirectReport.Lines(new[]
        {
            new RedirectHop(302, "https://a.test/", "https://b.test/", false, false)
        });
        Assert.DoesNotContain("ms", unmeasured);
        // The facts that were always there survive the addition.
        Assert.Contains("302", unmeasured);
        Assert.Contains("https://b.test/", unmeasured);
    }
}
