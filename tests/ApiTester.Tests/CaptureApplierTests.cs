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
        Assert.Single(env.Variables, v => v.Key == "token");
        Assert.Contains(outcome, o => o.Variable == "sid" && !o.Ok);
        Assert.DoesNotContain(outcome, o => o.Variable == "");                     // blank-name rule not reported
    }

    [Fact]
    public void Does_not_create_an_environment_when_every_rule_fails()
    {
        var state = new AppState();
        var rules = new[] { new CaptureRule { Variable = "sid", Source = CaptureSource.Header, Path = "X-Missing" } };
        var outcome = CaptureApplier.Apply(state, rules, B("""{"a":1}"""), null, NoHeaders);
        Assert.False(outcome[0].Ok);
        Assert.Empty(state.Environments);              // nothing created
        Assert.Null(state.ActiveEnvironmentId);
    }

    [Fact]
    public void A_capture_that_creates_a_new_variable_marks_it_secret()
    {
        var state = new AppState();
        var rules = new[] { new CaptureRule { Variable = "token", Source = CaptureSource.Body, Path = "access_token" } };

        CaptureApplier.Apply(state, rules, B("""{"access_token":"xyz"}"""), null, NoHeaders);

        var env = state.Environments.Single();
        Assert.True(env.Variables.Single(v => v.Key == "token").Secret);
    }

    [Fact]
    public void A_capture_that_overwrites_an_existing_non_secret_variable_marks_it_secret()
    {
        var state = new AppState();
        var env = new ApiEnvironment { Id = "e1", Name = "Dev", Variables = { new Variable { Key = "token", Value = "old", Secret = false } } };
        state.Environments.Add(env);
        state.ActiveEnvironmentId = "e1";

        var rules = new[] { new CaptureRule { Variable = "token", Source = CaptureSource.Body, Path = "access_token" } };
        CaptureApplier.Apply(state, rules, B("""{"access_token":"new"}"""), null, NoHeaders);

        var variable = env.Variables.Single(v => v.Key == "token");
        Assert.Equal("new", variable.Value);
        Assert.True(variable.Secret);
    }

    [Fact]
    public void A_hand_added_variable_stays_non_secret_when_a_capture_writes_a_different_key()
    {
        var state = new AppState();
        var env = new ApiEnvironment { Id = "e1", Name = "Dev", Variables = { new Variable { Key = "host", Value = "dev.local" } } };
        state.Environments.Add(env);
        state.ActiveEnvironmentId = "e1";

        var handAdded = env.Variables.Single(v => v.Key == "host");
        Assert.False(handAdded.Secret);

        var rules = new[] { new CaptureRule { Variable = "token", Source = CaptureSource.Body, Path = "access_token" } };
        CaptureApplier.Apply(state, rules, B("""{"access_token":"xyz"}"""), null, NoHeaders);

        Assert.False(handAdded.Secret);                                     // flag follows the captured value, not the environment
        Assert.True(env.Variables.Single(v => v.Key == "token").Secret);
    }
}
