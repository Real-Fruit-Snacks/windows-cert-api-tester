using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Importing a workspace decides which of a user's collections, environments, saved
/// websites, chains and history survive an import, and which get overwritten — get it wrong and the
/// user's saved work is gone. <see cref="WorkspaceImport.Plan"/> is the pure decision, extracted out
/// of <c>MainWindow.ApplyWorkspace</c> where nothing tested it until now.</summary>
public class WorkspaceImportTests
{
    // Helpers keep each test's AppState construction short; every timestamp is explicit and UTC —
    // HistoryEntry.Timestamp defaults to DateTime.Now, and List.Sort is unstable, so any test that
    // let two entries share a clock-derived timestamp would have no defined order.
    private static HistoryEntry Hist(DateTime ts, string tag) => new() { Timestamp = ts, Url = tag };

    // ---------- replace mode ----------

    [Fact]
    public void Replace_substitutes_wholesale()
    {
        // current is deliberately rich, so anything leaking from it into the plan would be obvious.
        var current = new AppState
        {
            Tabs = { new RequestModel { Method = "GET", Path = "/current" } },
            Collections = { new CollectionNode { Id = "cur-col", Name = "Current folder", IsFolder = true } },
            Environments = { new ApiEnvironment { Id = "cur-env", Name = "CurrentEnv" } },
            Chains = { new RequestChain { Id = "cur-chain", Name = "Current chain" } },
            History = { Hist(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "old") },
            SavedBaseUrls = { "https://current.example" },
            ActiveEnvironmentId = "cur-env"
        };
        var incoming = new AppState
        {
            Tabs = { new RequestModel { Method = "POST", Path = "/incoming" } },
            Collections = { new CollectionNode { Id = "inc-col", Name = "Incoming folder", IsFolder = true } },
            Environments = { new ApiEnvironment { Id = "inc-env", Name = "IncomingEnv" } },
            Chains = { new RequestChain { Id = "inc-chain", Name = "Incoming chain" } },
            History = { Hist(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), "new") },
            SavedBaseUrls = { "https://incoming.example" },
            ActiveEnvironmentId = "inc-env"
        };

        var plan = WorkspaceImport.Plan(current, incoming, merge: false);

        Assert.True(plan.ClearExisting);
        Assert.Same(incoming.Tabs[0], Assert.Single(plan.TabsToAdd));
        Assert.Same(incoming.Collections[0], Assert.Single(plan.CollectionsToAdd));
        Assert.Same(incoming.Environments[0], Assert.Single(plan.EnvironmentsToAdd));
        Assert.Same(incoming.Chains[0], Assert.Single(plan.ChainsToAdd));
        Assert.Equal("new", Assert.Single(plan.History).Url);
        Assert.Equal("https://incoming.example", Assert.Single(plan.SavedBaseUrls));
        Assert.Equal("inc-env", plan.ActiveEnvironmentId);
    }

    [Fact]
    public void Replace_sets_ActiveEnvironmentId_even_when_the_incoming_value_is_null()
    {
        // A workspace with no active environment must be able to clear one, not just set one.
        var current = new AppState { ActiveEnvironmentId = "cur-env" };
        var incoming = new AppState { ActiveEnvironmentId = null };

        var plan = WorkspaceImport.Plan(current, incoming, merge: false);

        Assert.Null(plan.ActiveEnvironmentId);
    }

    [Fact]
    public void Replace_does_not_deduplicate_environments_by_id()
    {
        // Pinned as today's behaviour: in replace mode there is no de-duplication check at all, so
        // two incoming environments that share an Id both arrive.
        var current = new AppState();
        var incoming = new AppState
        {
            Environments =
            {
                new ApiEnvironment { Id = "dup", Name = "First" },
                new ApiEnvironment { Id = "dup", Name = "Second" }
            }
        };

        var plan = WorkspaceImport.Plan(current, incoming, merge: false);

        Assert.Equal(2, plan.EnvironmentsToAdd.Count);
        Assert.Equal("First", plan.EnvironmentsToAdd[0].Name);
        Assert.Equal("Second", plan.EnvironmentsToAdd[1].Name);
    }

    [Fact]
    public void SelectTabIndex_is_the_incoming_active_tab_index_clamped_to_the_resulting_range()
    {
        var current = new AppState();
        AppState IncomingWithActiveIndex(int activeIndex) => new()
        {
            Tabs =
            {
                new RequestModel { Path = "/0" }, new RequestModel { Path = "/1" },
                new RequestModel { Path = "/2" }, new RequestModel { Path = "/3" },
                new RequestModel { Path = "/4" }
            },
            ActiveTabIndex = activeIndex
        };

        Assert.Equal(2, WorkspaceImport.Plan(current, IncomingWithActiveIndex(2), merge: false).SelectTabIndex);
        Assert.Equal(4, WorkspaceImport.Plan(current, IncomingWithActiveIndex(99), merge: false).SelectTabIndex);
        Assert.Equal(0, WorkspaceImport.Plan(current, IncomingWithActiveIndex(-3), merge: false).SelectTabIndex);
    }

    // ---------- merge mode ----------

    [Fact]
    public void Merge_preserves_existing_items_and_adds_only_what_is_new()
    {
        var current = new AppState
        {
            Tabs = { new RequestModel { Path = "/current" } },
            Collections = { new CollectionNode { Id = "colA", Name = "A", IsFolder = true } },
            Environments = { new ApiEnvironment { Id = "envA", Name = "A" } },
            Chains = { new RequestChain { Id = "chainA", Name = "A" } },
            History = { Hist(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), "existing") },
            SavedBaseUrls = { "https://a.example" }
        };
        var incoming = new AppState
        {
            Tabs = { new RequestModel { Path = "/incoming" } },
            Collections = { new CollectionNode { Id = "colB", Name = "B", IsFolder = true } },
            Environments = { new ApiEnvironment { Id = "envB", Name = "B" } },
            Chains = { new RequestChain { Id = "chainB", Name = "B" } },
            History = { Hist(new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc), "brought") },
            SavedBaseUrls = { "https://b.example" }
        };

        var plan = WorkspaceImport.Plan(current, incoming, merge: true);

        Assert.False(plan.ClearExisting);
        // "ToAdd" is what's genuinely new — the window already holds the current items, so re-handing
        // them back would duplicate them on screen.
        Assert.Same(incoming.Tabs[0], Assert.Single(plan.TabsToAdd));
        Assert.Same(incoming.Collections[0], Assert.Single(plan.CollectionsToAdd));
        Assert.Same(incoming.Environments[0], Assert.Single(plan.EnvironmentsToAdd));
        Assert.Same(incoming.Chains[0], Assert.Single(plan.ChainsToAdd));
        Assert.Equal(new[] { "brought", "existing" }, plan.History.Select(h => h.Url));
        Assert.Equal(new[] { "https://a.example", "https://b.example" }, plan.SavedBaseUrls);
    }

    [Fact]
    public void Merge_leaves_ActiveEnvironmentId_alone_even_when_incoming_names_a_different_one()
    {
        var current = new AppState { ActiveEnvironmentId = "cur-env" };
        var incoming = new AppState { ActiveEnvironmentId = "other-env" };

        var plan = WorkspaceImport.Plan(current, incoming, merge: true);

        Assert.Equal("cur-env", plan.ActiveEnvironmentId);
    }

    [Fact]
    public void SelectTabIndex_is_null_on_merge_whatever_the_incoming_active_tab_index_says()
    {
        // A merge must not yank the user off the tab they were on.
        var current = new AppState { Tabs = { new RequestModel { Path = "/current" } } };
        var incomingLowIndex = new AppState { Tabs = { new RequestModel { Path = "/new" } }, ActiveTabIndex = 0 };
        var incomingHighIndex = new AppState { Tabs = { new RequestModel { Path = "/new" } }, ActiveTabIndex = 99 };

        Assert.Null(WorkspaceImport.Plan(current, incomingLowIndex, merge: true).SelectTabIndex);
        Assert.Null(WorkspaceImport.Plan(current, incomingHighIndex, merge: true).SelectTabIndex);
    }

    // ---------- environments — the case that motivated the slice ----------

    [Fact]
    public void Imported_environment_with_a_colliding_name_but_a_different_id_is_added_alongside_the_existing_one()
    {
        // Environments are identified by Id, never by name — so a name collision is not a collision
        // at all. This is today's behaviour: the user ends up with two environments both called
        // "Dev", one theirs and one imported, and that is deliberately not fixed here.
        var existing = new ApiEnvironment
        {
            Id = "env-existing", Name = "Dev",
            Variables = { new Variable { Key = "host", Value = "existing.local" } }
        };
        var imported = new ApiEnvironment
        {
            Id = "env-imported", Name = "Dev",
            Variables = { new Variable { Key = "host", Value = "imported.local" } }
        };
        var current = new AppState { Environments = { existing } };
        var incoming = new AppState { Environments = { imported } };

        var plan = WorkspaceImport.Plan(current, incoming, merge: true);

        Assert.Same(imported, Assert.Single(plan.EnvironmentsToAdd));
        Assert.Equal("imported.local", Assert.Single(imported.Variables).Value);
        // The existing environment is untouched — same instance, same variable value.
        Assert.Same(existing, Assert.Single(current.Environments));
        Assert.Equal("existing.local", Assert.Single(existing.Variables).Value);
    }

    [Fact]
    public void Imported_environment_with_a_colliding_id_is_skipped_on_merge_without_overwriting_existing_variables()
    {
        var existing = new ApiEnvironment
        {
            Id = "shared-id", Name = "Dev",
            Variables = { new Variable { Key = "host", Value = "existing.local" } }
        };
        var imported = new ApiEnvironment
        {
            Id = "shared-id", Name = "Dev (imported)",
            Variables = { new Variable { Key = "host", Value = "imported.local" } }
        };
        var current = new AppState { Environments = { existing } };
        var incoming = new AppState { Environments = { imported } };

        var plan = WorkspaceImport.Plan(current, incoming, merge: true);

        Assert.Empty(plan.EnvironmentsToAdd);
        // Neither duplicated nor silently overwritten: the existing values are exactly as before.
        Assert.Equal("existing.local", Assert.Single(existing.Variables).Value);
        Assert.Equal("Dev", existing.Name);
    }

    [Fact]
    public void Two_incoming_environments_sharing_an_id_self_deduplicate_on_merge()
    {
        // The check runs against the collection as it grows: the first of a pair sharing an Id is
        // added, and the second then finds a match already "seen" and is skipped.
        var current = new AppState();
        var incoming = new AppState
        {
            Environments =
            {
                new ApiEnvironment { Id = "dup", Name = "First" },
                new ApiEnvironment { Id = "dup", Name = "Second" }
            }
        };

        var plan = WorkspaceImport.Plan(current, incoming, merge: true);

        Assert.Equal("First", Assert.Single(plan.EnvironmentsToAdd).Name);
    }

    // ---------- chains ----------

    [Fact]
    public void Merge_deduplicates_chains_by_id_and_a_new_chains_steps_and_order_survive()
    {
        var current = new AppState { Chains = { new RequestChain { Id = "chain-existing", Name = "Existing" } } };
        var incoming = new AppState
        {
            Chains =
            {
                new RequestChain
                {
                    Id = "chain-new", Name = "New chain",
                    Steps = { new ChainStep { RequestId = "r1" }, new ChainStep { RequestId = "r2", StopOnFailure = false } }
                }
            }
        };

        var plan = WorkspaceImport.Plan(current, incoming, merge: true);

        var chain = Assert.Single(plan.ChainsToAdd);
        Assert.Equal("chain-new", chain.Id);
        Assert.Equal(2, chain.Steps.Count);
        Assert.Equal("r1", chain.Steps[0].RequestId);
        Assert.True(chain.Steps[0].StopOnFailure);
        Assert.Equal("r2", chain.Steps[1].RequestId);
        Assert.False(chain.Steps[1].StopOnFailure);
    }

    [Fact]
    public void Replace_adds_every_incoming_chain_including_id_collisions()
    {
        var current = new AppState();
        var incoming = new AppState
        {
            Chains =
            {
                new RequestChain { Id = "dup", Name = "First" },
                new RequestChain { Id = "dup", Name = "Second" }
            }
        };

        var plan = WorkspaceImport.Plan(current, incoming, merge: false);

        Assert.Equal(2, plan.ChainsToAdd.Count);
    }

    // ---------- collections ----------

    [Fact]
    public void Collections_are_added_with_no_deduplication_in_either_mode()
    {
        // Unlike environments and chains above, collections carry no de-duplication at all: this is
        // today's behaviour, not a defect this slice fixes. Importing the same workspace twice
        // duplicates the user's saved requests, in merge and in replace alike — changing that would
        // be a behaviour change, not a refactor.
        var existing = new CollectionNode { Id = "shared-col", Name = "Saved requests", IsFolder = true };
        var imported = new CollectionNode { Id = "shared-col", Name = "Saved requests", IsFolder = true };
        var current = new AppState { Collections = { existing } };
        var incoming = new AppState { Collections = { imported } };

        var mergePlan = WorkspaceImport.Plan(current, incoming, merge: true);
        Assert.Same(imported, Assert.Single(mergePlan.CollectionsToAdd));
        // Applying this plan would leave `current.Collections` (still holding `existing`) plus the
        // one node handed back here — two nodes sharing the same Id.
        Assert.Same(existing, Assert.Single(current.Collections));

        var replacePlan = WorkspaceImport.Plan(current, incoming, merge: false);
        Assert.Same(imported, Assert.Single(replacePlan.CollectionsToAdd));
    }

    // ---------- history ----------

    [Fact]
    public void Merge_appends_incoming_history_to_existing_and_returns_it_newest_first()
    {
        var current = new AppState
        {
            History =
            {
                Hist(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), "existing-old"),
                Hist(new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc), "existing-new")
            }
        };
        var incoming = new AppState
        {
            History = { Hist(new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc), "incoming-mid") }
        };

        var plan = WorkspaceImport.Plan(current, incoming, merge: true);

        Assert.Equal(new[] { "existing-new", "incoming-mid", "existing-old" }, plan.History.Select(h => h.Url));
    }

    [Fact]
    public void History_beyond_the_limit_is_capped_keeping_only_the_newest_entries()
    {
        var current = new AppState();
        var incoming = new AppState();
        // Entry "h{n}" is n seconds into the same minute, so higher n = newer. 35 entries total,
        // split across both sources, comfortably over WorkspaceImport.HistoryLimit (30).
        for (int n = 1; n <= 15; n++)
            current.History.Add(Hist(new DateTime(2026, 4, 1, 0, 0, n, DateTimeKind.Utc), $"h{n}"));
        for (int n = 16; n <= 35; n++)
            incoming.History.Add(Hist(new DateTime(2026, 4, 1, 0, 0, n, DateTimeKind.Utc), $"h{n}"));

        var plan = WorkspaceImport.Plan(current, incoming, merge: true);

        Assert.Equal(WorkspaceImport.HistoryLimit, plan.History.Count);
        Assert.Equal("h35", plan.History[0].Url);   // newest survives, first
        Assert.Equal("h6", plan.History[^1].Url);   // the 30th-newest survives, last
        Assert.DoesNotContain(plan.History, h => h.Url is "h1" or "h2" or "h3" or "h4" or "h5");
    }

    [Fact]
    public void Replace_discards_existing_history_entirely()
    {
        var current = new AppState { History = { Hist(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), "old") } };
        var incoming = new AppState();

        var plan = WorkspaceImport.Plan(current, incoming, merge: false);

        Assert.Empty(plan.History);
    }

    // ---------- saved websites ----------

    [Fact]
    public void Case_insensitive_deduplication_keeps_existing_casing_and_order_and_appends_genuinely_new_entries_on_merge()
    {
        var current = new AppState { SavedBaseUrls = { "https://api.example", "https://other.example" } };
        var incoming = new AppState { SavedBaseUrls = { "HTTPS://API.EXAMPLE", "https://new.example" } };

        var plan = WorkspaceImport.Plan(current, incoming, merge: true);

        Assert.Equal(
            new[] { "https://api.example", "https://other.example", "https://new.example" },
            plan.SavedBaseUrls);
    }

    [Fact]
    public void Case_insensitive_deduplication_applies_within_the_incoming_list_on_replace()
    {
        var current = new AppState { SavedBaseUrls = { "https://ignored.example" } };
        var incoming = new AppState
        {
            SavedBaseUrls = { "https://API.example", "HTTPS://api.EXAMPLE", "https://other.example" }
        };

        var plan = WorkspaceImport.Plan(current, incoming, merge: false);

        Assert.Equal(new[] { "https://API.example", "https://other.example" }, plan.SavedBaseUrls);
    }

    // ---------- tabs ----------

    [Fact]
    public void NeedsBlankTab_is_true_only_when_the_import_leaves_no_tabs_at_all()
    {
        var currentWithTabs = new AppState { Tabs = { new RequestModel { Path = "/current" } } };
        var currentEmpty = new AppState();
        var incomingEmpty = new AppState();
        var incomingWithTabs = new AppState { Tabs = { new RequestModel { Path = "/incoming" } } };

        // Replace with no incoming tabs: the existing tabs are discarded, so the result is empty.
        Assert.True(WorkspaceImport.Plan(currentWithTabs, incomingEmpty, merge: false).NeedsBlankTab);
        // Merge onto an existing tab with nothing incoming: one tab remains.
        Assert.False(WorkspaceImport.Plan(currentWithTabs, incomingEmpty, merge: true).NeedsBlankTab);
        // Replace that brings tabs: the result is non-empty even though current is discarded.
        Assert.False(WorkspaceImport.Plan(currentEmpty, incomingWithTabs, merge: false).NeedsBlankTab);
    }

    // ---------- purity ----------

    [Fact]
    public void Plan_mutates_neither_argument()
    {
        // Deliberately unsorted, so an in-place sort of current.History would be observable.
        var current = new AppState
        {
            Tabs = { new RequestModel { Path = "/current" } },
            Collections = { new CollectionNode { Id = "cur-col", Name = "Cur", IsFolder = true } },
            Environments = { new ApiEnvironment { Id = "cur-env", Name = "Cur" } },
            Chains = { new RequestChain { Id = "cur-chain", Name = "Cur" } },
            History =
            {
                Hist(new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc), "mid"),
                Hist(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), "oldest"),
                Hist(new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc), "newest")
            },
            SavedBaseUrls = { "https://current.example" }
        };
        var incoming = new AppState
        {
            Tabs = { new RequestModel { Path = "/incoming" } },
            Collections = { new CollectionNode { Id = "inc-col", Name = "Inc", IsFolder = true } },
            Environments = { new ApiEnvironment { Id = "inc-env", Name = "Inc" } },
            Chains = { new RequestChain { Id = "inc-chain", Name = "Inc" } },
            History = { Hist(new DateTime(2026, 6, 7, 0, 0, 0, DateTimeKind.Utc), "incoming-entry") },
            SavedBaseUrls = { "https://incoming.example" }
        };

        WorkspaceImport.Plan(current, incoming, merge: true);

        Assert.Equal(new[] { "mid", "oldest", "newest" }, current.History.Select(h => h.Url));
        Assert.Single(current.Tabs);
        Assert.Single(current.Collections);
        Assert.Single(current.Environments);
        Assert.Single(current.Chains);
        Assert.Single(current.SavedBaseUrls);

        Assert.Single(incoming.Tabs);
        Assert.Single(incoming.Collections);
        Assert.Single(incoming.Environments);
        Assert.Single(incoming.Chains);
        Assert.Single(incoming.History);
        Assert.Single(incoming.SavedBaseUrls);

        var plan = WorkspaceImport.Plan(current, incoming, merge: true);
        Assert.NotSame(current.History, plan.History);
    }
}
