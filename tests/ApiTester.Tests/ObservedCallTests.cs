using ApiTester.Core;
using Xunit;

namespace ApiTester.Tests;

public class ObservedCallTests
{
    [Fact]
    public void Dedup_collapses_same_method_and_path_last_wins()
    {
        var calls = new[]
        {
            new ObservedCall("GET", "https://a.corp/api/me?t=1", 200, "application/json"),
            new ObservedCall("GET", "https://a.corp/api/me?t=2", 304, "application/json"),
            new ObservedCall("POST", "https://a.corp/api/login", 200, "application/json"),
        };
        var result = ObservedCall.Dedup(calls);
        Assert.Equal(2, result.Count);
        Assert.Equal(304, result.First(c => c.Url.Contains("/api/me")).StatusCode);
    }

    [Fact]
    public void Dedup_keeps_distinct_methods_on_same_path()
    {
        var calls = new[]
        {
            new ObservedCall("GET", "https://a.corp/api/x", 200, null),
            new ObservedCall("DELETE", "https://a.corp/api/x", 204, null),
        };
        Assert.Equal(2, ObservedCall.Dedup(calls).Count);
    }
}
