namespace ApiTester.Core;

/// <summary>An ordered sequence of saved requests run as one unit, each step able to use variables
/// captured by earlier steps. Persisted on AppState, additive.
/// <para>The capture machinery already carries a token from one request to the next when they happen
/// to sit in the same collection; a chain makes that dependency explicit rather than incidental to
/// collection order, so "log in, then call the API" is a thing you run instead of a convention you
/// remember.</para></summary>
public sealed class RequestChain
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    public List<ChainStep> Steps { get; set; } = new();
    /// <summary>Environment the chain's captures write into; created on first use if absent.</summary>
    public string? EnvironmentName { get; set; }
}

/// <summary>One request of a chain, named by id so renaming or moving the saved request in the
/// collections tree does not silently repoint the step at something else.</summary>
public sealed class ChainStep
{
    /// <summary>Id of the saved request (CollectionNode) this step runs.</summary>
    public string RequestId { get; set; } = "";
    /// <summary>Stop the chain when this step fails (assertions or transport). Default true —
    /// calling the API after the login failed produces a confusing 401, not a useful result.</summary>
    public bool StopOnFailure { get; set; } = true;
}
