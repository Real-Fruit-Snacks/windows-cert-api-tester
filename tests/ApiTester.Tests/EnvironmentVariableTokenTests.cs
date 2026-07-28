using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The `{{env:NAME}}` namespace: a secret reaches a request from the process environment
/// without ever being stored — nothing in the workspace, nothing in an export, nothing in source
/// control. Every test injects its own environment rather than mutating the process's, which is
/// global state a parallel run shares.</summary>
public class EnvironmentVariableTokenTests
{
    private static readonly Dictionary<string, string> NoVars = new();

    private static Func<string, string?> Env(params (string Name, string Value)[] entries) =>
        name => entries.FirstOrDefault(e => e.Name == name).Value;

    [Fact]
    public void An_env_token_resolves_from_the_environment()
    {
        var (result, unresolved) = VariableResolver.Resolve(
            "Bearer {{env:API_TOKEN}}", NoVars, Env(("API_TOKEN", "s3cret")));

        Assert.Equal("Bearer s3cret", result);
        Assert.Empty(unresolved);
    }

    [Fact]
    public void The_prefix_is_case_insensitive_but_the_variable_name_is_not()
    {
        // The namespace is ours, so spelling it ENV: or env: is the same token; the NAME belongs to
        // the platform, and on Windows a lookup is case-insensitive only because the OS says so —
        // this resolver passes it through untouched rather than deciding.
        Assert.Equal("v", VariableResolver.Resolve("{{ENV:NAME}}", NoVars, Env(("NAME", "v"))).Result);
        Assert.Equal("v", VariableResolver.Resolve("{{Env:NAME}}", NoVars, Env(("NAME", "v"))).Result);

        var (result, unresolved) = VariableResolver.Resolve("{{env:name}}", NoVars, Env(("NAME", "v")));
        Assert.Equal("{{env:name}}", result);          // the injected environment is exact-match
        Assert.Contains("env:name", unresolved);
    }

    [Fact]
    public void A_missing_environment_variable_is_reported_like_any_other_unresolved_token()
    {
        var (result, unresolved) = VariableResolver.Resolve(
            "{{env:NOT_SET}}", NoVars, Env(("SOMETHING_ELSE", "x")));

        Assert.Equal("{{env:NOT_SET}}", result);       // left intact, never blanked
        Assert.Contains("env:NOT_SET", unresolved);
    }

    [Fact]
    public void An_empty_environment_name_resolves_nothing_and_is_reported()
    {
        // "{{env:}}" must not expand to blank — that would silently send an empty credential.
        var (result, unresolved) = VariableResolver.Resolve("{{env:}}", NoVars, Env(("", "oops")));

        Assert.Equal("{{env:}}", result);
        Assert.Contains("env:", unresolved);
    }

    [Fact]
    public void A_workspace_variable_of_the_same_name_wins_over_the_environment()
    {
        // Someone who deliberately saved a variable spelled "env:TOKEN" is not overruled by the
        // namespace — the workspace is checked first, for every token.
        var vars = new Dictionary<string, string> { ["env:TOKEN"] = "from-workspace" };

        var (result, _) = VariableResolver.Resolve("{{env:TOKEN}}", vars, Env(("TOKEN", "from-env")));

        Assert.Equal("from-workspace", result);
    }

    [Fact]
    public void Ordinary_variables_are_unaffected_by_the_namespace()
    {
        var vars = new Dictionary<string, string> { ["base"] = "https://api.test" };

        var (result, unresolved) = VariableResolver.Resolve(
            "{{base}}/orders?k={{env:KEY}}&m={{missing}}", vars, Env(("KEY", "abc")));

        Assert.Equal("https://api.test/orders?k=abc&m={{missing}}", result);
        Assert.Equal(new[] { "missing" }, unresolved);
    }

    [Fact]
    public void Whitespace_inside_the_token_is_tolerated_the_way_it_always_was()
    {
        Assert.Equal("v", VariableResolver.Resolve("{{ env:NAME }}", NoVars, Env(("NAME", "v"))).Result);
    }

    [Fact]
    public void The_default_overload_still_resolves_ordinary_variables_unchanged()
    {
        // The two-argument overload every existing caller uses must behave exactly as before.
        var vars = new Dictionary<string, string> { ["a"] = "1" };

        var (result, unresolved) = VariableResolver.Resolve("{{a}}/{{b}}", vars);

        Assert.Equal("1/{{b}}", result);
        Assert.Equal(new[] { "b" }, unresolved);
    }

    [Fact]
    public void An_env_token_reaches_the_process_environment_when_no_lookup_is_injected()
    {
        // The one test that touches the real environment, because the default path is worth
        // proving end to end. A uniquely-named variable, set and removed in the same test, cannot
        // collide with a parallel run's own.
        string name = "CERTAPI_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "real-value");
        try
        {
            var (result, unresolved) = VariableResolver.Resolve("{{env:" + name + "}}", NoVars);

            Assert.Equal("real-value", result);
            Assert.Empty(unresolved);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }
}
