namespace ApiTester.Core;

/// <summary>A chain could not be run as named: it does not exist, has no steps, or names a saved
/// request that no longer does. Raised before any network call — the front end turns it into
/// whatever it reports errors as.</summary>
public sealed class ChainRunException : Exception
{
    public ChainRunException(string message) : base(message) { }
}

/// <summary>One step of a chain with its saved request already found, and the label the front end
/// reports it under. The step number is what the user can act on; the node name alone would be
/// ambiguous in a chain that runs the same request twice.</summary>
public sealed record ResolvedChainStep(int Number, string Label, CollectionNode Node, ChainStep Step);

/// <summary>A step just finished. Reported as it happens so a front end can render the chain
/// progressing rather than waiting for the whole thing.</summary>
public sealed record ChainRunProgress(int Number, int Total, RequestOutcome Outcome);

/// <summary>What the chain did: the steps that ran, in order, and the labels of the steps that never
/// did. Skipped steps are reported rather than dropped — an output that just stops leaves the reader
/// guessing whether the rest passed.</summary>
public sealed record ChainRunResult(
    IReadOnlyList<RequestOutcome> Steps, IReadOnlyList<string> SkippedLabels)
{
    public int Passed => Steps.Count(s => s.Passed);
    public int Failed => Steps.Count - Passed;
    public bool CapturedValues => Steps.Any(s => s.CapturedValues);
    public bool CapturedTokens => Steps.Any(s => s.CapturedTokens);
}

/// <summary>Runs a saved <see cref="RequestChain"/>: its requests, in the order the chain names
/// them, as one unit — so a token captured by one step is usable by the next through its
/// <c>{{variable}}</c>. UI-free and cancellable, so both the command line and the desktop
/// application's chain window funnel through the exact same engine.</summary>
public static class ChainRunner
{
    /// <summary>The saved chain of that name. Throws <see cref="ChainRunException"/> naming the
    /// chains that do exist, because "no such chain" without the list is a dead end.</summary>
    public static RequestChain Find(AppState state, string name) =>
        state.Chains.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ChainRunException(
                $"No chain named '{name}'. Available: " +
                (state.Chains.Count == 0 ? "(none)" : string.Join(", ", state.Chains.Select(c => c.Name))) + ".");

    /// <summary>Resolve every step up front — a chain naming a request that no longer exists must
    /// fail before it makes a network call, not half-way through with a login already sent.</summary>
    public static IReadOnlyList<ResolvedChainStep> Resolve(AppState state, RequestChain chain)
    {
        if (chain.Steps.Count == 0)
            throw new ChainRunException($"Chain '{chain.Name}' has no steps to run.");

        var steps = new List<ResolvedChainStep>();
        for (int i = 0; i < chain.Steps.Count; i++)
        {
            var step = chain.Steps[i];
            var node = FindRequest(state.Collections, step.RequestId)
                ?? throw new ChainRunException(
                    $"Chain '{chain.Name}' step {i + 1} references a request that no longer exists (id {step.RequestId}).");
            // The step number is what the user can act on; the node name alone would be ambiguous
            // in a chain that runs the same request twice.
            steps.Add(new ResolvedChainStep(i + 1, $"{chain.Name}/{i + 1}. {node.Name}", node, step));
        }
        return steps;
    }

    /// <summary>The environment this chain's captures write into, created on first use, made active
    /// so a value captured by step one has somewhere step two can read it from. Returns its name, or
    /// null when the chain names none. A caller with a more specific instruction (a typed --env)
    /// should not call this.</summary>
    public static string? PrepareCaptureEnvironment(AppState state, RequestChain chain)
    {
        if (chain.EnvironmentName is not { } chainEnvName) return null;
        var chainEnv = state.Environments.FirstOrDefault(
            e => e.Name.Equals(chainEnvName, StringComparison.OrdinalIgnoreCase));
        if (chainEnv is null)
        {
            chainEnv = new ApiEnvironment { Name = chainEnvName };
            state.Environments.Add(chainEnv);
        }
        state.ActiveEnvironmentId = chainEnv.Id;
        return chainEnvName;   // the caller's BuildIterVars reads it back out for the next step
    }

    /// <summary>Run the resolved steps in order, one at a time, each seeing what the ones before it
    /// captured. A failing step stops the chain unless it is marked to carry on.</summary>
    public static async Task<ChainRunResult> RunAsync(
        IReadOnlyList<ResolvedChainStep> steps, RequestRunContext ctx, RunVariables vars,
        IProgress<ChainRunProgress>? progress, CancellationToken ct)
    {
        var outcomes = new List<RequestOutcome>();
        var skipped = new List<string>();
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            // Between steps only: a Stop button between two sends ends the run here rather than
            // waiting for whichever one is next to fail on its own terms.
            ct.ThrowIfCancellationRequested();
            var outcome = await RequestRunner.RunAsync(step.Label, step.Node, ctx, vars, ct).ConfigureAwait(false);
            outcomes.Add(outcome);
            progress?.Report(new ChainRunProgress(step.Number, steps.Count, outcome));
            if (outcome.Passed || !step.Step.StopOnFailure) continue;
            ctx.Note?.Invoke($"{step.Label}: step failed — stopping the chain (the later steps would only " +
                             "report the consequences).");
            for (int j = i + 1; j < steps.Count; j++) skipped.Add(steps[j].Label);
            break;
        }
        return new ChainRunResult(outcomes, skipped);
    }

    /// <summary>The saved request a chain step names, searched depth-first through the collections
    /// tree. A folder or an empty node is not runnable, so neither answers here — a step whose id
    /// resolves to one of those is as broken as a step whose id resolves to nothing.</summary>
    private static CollectionNode? FindRequest(IEnumerable<CollectionNode> nodes, string id)
    {
        foreach (var n in nodes)
        {
            // Ids are Guid hex, written "N" by the collections tree and "n" by a chain, so the two
            // spellings of the same id must still find each other.
            if (!n.IsFolder && n.Request is not null && string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase))
                return n;
            if (FindRequest(n.Children, id) is { } found) return found;
        }
        return null;
    }
}
