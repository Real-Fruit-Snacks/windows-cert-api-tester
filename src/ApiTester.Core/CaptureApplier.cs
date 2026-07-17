namespace ApiTester.Core;

/// <summary>Applies a request's capture rules to a response, writing extracted values into the
/// active environment (auto-creating one named "Captured" when none is active).</summary>
public static class CaptureApplier
{
    public static IReadOnlyList<(string Variable, bool Ok, string? Error)> Apply(
        AppState state, IEnumerable<CaptureRule> rules,
        byte[] body, string? contentType, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        var results = new List<(string, bool, string?)>();
        var applicable = rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Variable)).ToList();
        if (applicable.Count == 0) return results;

        var env = ActiveEnvironment(state);
        foreach (var rule in applicable)
        {
            var (value, error) = ResponseCapture.Extract(rule.ToSpec(), body, contentType, headers);
            if (value is null) { results.Add((rule.Variable.Trim(), false, error)); continue; }
            Upsert(env, rule.Variable.Trim(), value);
            results.Add((rule.Variable.Trim(), true, null));
        }
        return results;
    }

    private static ApiEnvironment ActiveEnvironment(AppState state)
    {
        if (state.ActiveEnvironmentId is { } id &&
            state.Environments.FirstOrDefault(e => e.Id == id) is { } active)
            return active;

        var created = new ApiEnvironment { Name = "Captured" };
        state.Environments.Add(created);
        state.ActiveEnvironmentId = created.Id;
        return created;
    }

    private static void Upsert(ApiEnvironment env, string key, string value)
    {
        var existing = env.Variables.FirstOrDefault(v => v.Key == key);
        if (existing is not null) existing.Value = value;
        else env.Variables.Add(new Variable { Key = key, Value = value });
    }
}
