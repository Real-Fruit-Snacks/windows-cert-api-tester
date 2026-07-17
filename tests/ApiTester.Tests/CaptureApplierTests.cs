using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

public class CaptureApplierTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoHeaders = System.Array.Empty<KeyValuePair<string, string>>();

    [Fact]
    public void Writes_into_the_active_environment()
    {
        var state = new AppState();
        var env = new ApiEnvironment { Id = "e1", Name = "Dev" };
        state.Environments.Add(env);
        state.ActiveEnvironmentId = "e1";

        var rules = new[] { new CaptureRule { Variable = "token", Source = CaptureSource.Body, Path = "access_token" } };
        var outcome = CaptureApplier.Apply(state, rules, B("""{"access_token":"abc"}"""), "application/json", NoHeaders);

        Assert.True(outcome[0].Ok);
        Assert.Equal("abc", env.Variables.Single(v => v.Key == "token").Value);
    }

    [Fact]
    public void Auto_creates_a_Captured_environment_when_none_is_active()
    {
        var state = new AppState();
        var rules = new[] { new CaptureRule { Variable = "token", Source = CaptureSource.Body, Path = "access_token" } };

        CaptureApplier.Apply(state, rules, B("""{"access_token":"xyz"}"""), null, NoHeaders);

        var env = state.Environments.Single();
        Assert.Equal("Captured", env.Name);
        Assert.Equal(env.Id, state.ActiveEnvironmentId);
        Assert.Equal("xyz", env.Variables.Single(v => v.Key == "token").Value);
    }

    [Fact]
    public void Upserts_existing_variables_and_reports_failures()
    {
        var state = new AppState();
        var env = new ApiEnvironment { Id = "e1", Name = "Dev", Variables = { new Variable { Key = "token", Value = "old" } } };
        state.Environments.Add(env);
        state.ActiveEnvironmentId = "e1";

        var rules = new[]
        {
            new CaptureRule { Variable = "token", Source = CaptureSource.Body, Path = "access_token" },
            new CaptureRule { Variable = "sid", Source = CaptureSource.Header, Path = "X-Missing" },
            new CaptureRule { Variable = "", Source = CaptureSource.Body, Path = "ignored" }   // blank name skipped
        };
        var outcome = CaptureApplier.Apply(state, rules, B("""{"access_token":"new"}"""), null, NoHeaders);

        Assert.Equal("new", env.Variables.Single(v => v.Key == "token").Value);   // upserted, not duplicated
        Assert.Single(env.Variables.Where(v => v.Key == "token"));
        Assert.Contains(outcome, o => o.Variable == "sid" && !o.Ok);
        Assert.DoesNotContain(outcome, o => o.Variable == "");                     // blank-name rule not reported
    }
}
