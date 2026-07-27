namespace ApiTester.Core;

/// <summary>What an imported workspace does to the one already open, decided without touching a
/// control. Import can either merge into the current workspace or replace it outright, and either
/// way the decision is arithmetic over two <see cref="AppState"/> values — it does not need a live
/// <c>ObservableCollection</c> or a window to work out.
/// <para>Every property is <c>required</c> deliberately: a property added to this plan later becomes
/// a compile error at every construction site instead of silently defaulting, so a forgotten field
/// cannot ship as a quiet bug.</para></summary>
public sealed record WorkspaceImportPlan
{
    /// <summary>Replace mode: the open tabs, collections, environments and chains are discarded first.</summary>
    public required bool ClearExisting { get; init; }

    // The four "…ToAdd" lists below are *what to add*, not *what the result is*. The window's
    // _tabs / _collections / _environments / _chains are ObservableCollections bound to live
    // controls (the tab strip, the collections tree, the environment combo, the chains list) —
    // clearing and re-adding an item that did not change on a merge would collapse the collections
    // tree and drop the user's selection. So on a merge, Plan hands back only the newly-arrived
    // items, and the caller appends them to what is already there. History and SavedBaseUrls, by
    // contrast, sit behind a list box and a combo that get rebuilt wholesale on every refresh
    // anyway, so those come back as the whole final list instead.

    public required IReadOnlyList<RequestModel> TabsToAdd { get; init; }
    public required IReadOnlyList<CollectionNode> CollectionsToAdd { get; init; }
    public required IReadOnlyList<ApiEnvironment> EnvironmentsToAdd { get; init; }
    public required IReadOnlyList<RequestChain> ChainsToAdd { get; init; }

    /// <summary>The whole history list afterwards: newest first, capped at <see cref="WorkspaceImport.HistoryLimit"/>.</summary>
    public required IReadOnlyList<HistoryEntry> History { get; init; }

    /// <summary>The whole saved-website list afterwards.</summary>
    public required IReadOnlyList<string> SavedBaseUrls { get; init; }

    /// <summary>The active environment afterwards — a merge leaves it alone.</summary>
    public required string? ActiveEnvironmentId { get; init; }

    /// <summary>The tab to select afterwards, or null to leave the selection where it is (merge).</summary>
    public required int? SelectTabIndex { get; init; }

    /// <summary>True when the import leaves no tabs at all, so the caller must open a blank one.</summary>
    public required bool NeedsBlankTab { get; init; }
}

/// <summary>Decides what an imported workspace does to the one already open. Pure: reads
/// <c>current</c> and <c>incoming</c> and returns a plan without mutating either — including not
/// sorting <c>current.History</c> in place, which is why the history and saved-website lists are
/// built from copies.</summary>
public static class WorkspaceImport
{
    /// <summary>How many history entries a workspace keeps after an import — the newest this many
    /// survive, oldest dropped first.</summary>
    public const int HistoryLimit = 30;

    public static WorkspaceImportPlan Plan(AppState current, AppState incoming, bool merge)
    {
        int resultingTabCount = (merge ? current.Tabs.Count : 0) + incoming.Tabs.Count;
        bool needsBlankTab = resultingTabCount == 0;
        int? selectTabIndex = needsBlankTab || merge
            ? null
            : Math.Clamp(incoming.ActiveTabIndex, 0, resultingTabCount - 1);

        // Environments and chains de-duplicate by Id on a merge, checked against the *growing*
        // result — current's items, plus whatever this loop has already decided to add — exactly as
        // the original code tested the live ObservableCollection as it grew. Two incoming items that
        // share an Id therefore self-deduplicate: the first is added, the second is not. Environments
        // are matched only by Id, never by name, so two environments that merely share a name are not
        // a collision at all and both survive. In replace mode there is no check whatsoever: every
        // incoming item is added, Id collisions included.
        var environmentsToAdd = new List<ApiEnvironment>();
        foreach (var env in incoming.Environments)
        {
            bool collides = merge && (current.Environments.Any(x => x.Id == env.Id) ||
                                       environmentsToAdd.Any(x => x.Id == env.Id));
            if (!collides) environmentsToAdd.Add(env);
        }

        var chainsToAdd = new List<RequestChain>();
        foreach (var chain in incoming.Chains)
        {
            bool collides = merge && (current.Chains.Any(x => x.Id == chain.Id) ||
                                       chainsToAdd.Any(x => x.Id == chain.Id));
            if (!collides) chainsToAdd.Add(chain);
        }

        // Collections carry no de-duplication whatsoever, in either mode. This differs deliberately
        // from environments and chains above: importing the same workspace twice adds its folders
        // twice today. That looks like a bug, and it may be one, but fixing it would be a behaviour
        // change, not this refactor — so it is reproduced exactly, not corrected.
        var collectionsToAdd = incoming.Collections.ToList();

        // Copy before sorting: current.History must come back re-ordered in the *plan*, never
        // re-ordered out from under the caller just because an import happened.
        var history = merge ? new List<HistoryEntry>(current.History) : new List<HistoryEntry>();
        history.AddRange(incoming.History);
        history.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
        if (history.Count > HistoryLimit) history.RemoveRange(HistoryLimit, history.Count - HistoryLimit);

        // Case-insensitive de-duplication applies in both modes — on a replace, that means the
        // incoming list is de-duplicated against itself.
        var savedBaseUrls = merge ? new List<string>(current.SavedBaseUrls) : new List<string>();
        foreach (var url in incoming.SavedBaseUrls)
            if (!savedBaseUrls.Contains(url, StringComparer.OrdinalIgnoreCase)) savedBaseUrls.Add(url);

        return new WorkspaceImportPlan
        {
            ClearExisting = !merge,
            TabsToAdd = incoming.Tabs.ToList(),
            CollectionsToAdd = collectionsToAdd,
            EnvironmentsToAdd = environmentsToAdd,
            ChainsToAdd = chainsToAdd,
            History = history,
            SavedBaseUrls = savedBaseUrls,
            ActiveEnvironmentId = merge ? current.ActiveEnvironmentId : incoming.ActiveEnvironmentId,
            SelectTabIndex = selectTabIndex,
            NeedsBlankTab = needsBlankTab
        };
    }
}
