using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>The markdown catalogue. <see cref="MarkdownExport.Build"/> is pure, so every layout,
/// escaping and redaction rule is checked as data — no folder is written by anything here.</summary>
public class MarkdownExportTests
{
    private static AppState StateWith(params CollectionNode[] nodes)
    {
        var state = new AppState();
        foreach (var node in nodes) state.Collections.Add(node);
        return state;
    }

    private static CollectionNode Folder(string name, params CollectionNode[] children)
    {
        var folder = new CollectionNode { Name = name, IsFolder = true };
        foreach (var child in children) folder.Children.Add(child);
        return folder;
    }

    private static CollectionNode Request(string name, string method = "GET",
                                          string url = "https://api.internal/orders",
                                          Action<RequestModel>? configure = null)
    {
        var model = new RequestModel { Method = method, BaseUrl = url, Path = "" };
        configure?.Invoke(model);
        return new CollectionNode { Name = name, IsFolder = false, Request = model };
    }

    private static string Content(IReadOnlyList<MarkdownFile> files, string endsWith) =>
        files.Single(f => f.RelativePath.EndsWith(endsWith, StringComparison.Ordinal)).Content;

    // ---------------------------------------------------------------- layout

    [Fact]
    public void The_collection_tree_becomes_the_folder_tree()
    {
        var state = StateWith(Folder("Orders", Request("Get orders"), Request("Create order")));

        var files = MarkdownExport.Build(state, new MarkdownExportOptions());

        Assert.Contains(files, f => f.RelativePath == "certapi/Orders/Get orders.md");
        Assert.Contains(files, f => f.RelativePath == "certapi/Orders/Create order.md");
    }

    [Fact]
    public void Nested_folders_nest()
    {
        var state = StateWith(Folder("API", Folder("v2", Request("Get orders"))));

        var files = MarkdownExport.Build(state, new MarkdownExportOptions());

        Assert.Contains(files, f => f.RelativePath == "certapi/API/v2/Get orders.md");
    }

    [Fact]
    public void The_output_subfolder_is_configurable_so_the_tree_is_its_own_island()
    {
        var state = StateWith(Request("Get orders"));

        var files = MarkdownExport.Build(state, new MarkdownExportOptions { Into = "Reference/APIs" });

        // The separator in --into is sanitised away: this is one folder name, not a path, because a
        // path would let an export escape the island it is meant to stay inside.
        Assert.Contains(files, f => f.RelativePath.StartsWith("Reference-APIs/", StringComparison.Ordinal));
    }

    [Fact]
    public void Environments_and_chains_get_their_own_folders()
    {
        var state = StateWith(Request("Get orders"));
        state.Environments.Add(new ApiEnvironment { Name = "Staging" });
        state.Chains.Add(new RequestChain { Name = "Login then fetch" });

        var files = MarkdownExport.Build(state, new MarkdownExportOptions());

        Assert.Contains(files, f => f.RelativePath == "certapi/environments/Staging.md");
        Assert.Contains(files, f => f.RelativePath == "certapi/chains/Login then fetch.md");
    }

    [Fact]
    public void A_leaf_with_no_request_is_skipped_rather_than_written_empty()
    {
        var state = StateWith(new CollectionNode { Name = "Orphan", IsFolder = false, Request = null });

        Assert.Empty(MarkdownExport.Build(state, new MarkdownExportOptions()));
    }

    [Fact]
    public void The_index_is_opt_in_and_lists_every_request()
    {
        var state = StateWith(Folder("Orders", Request("Get orders"), Request("Create order")));

        Assert.DoesNotContain(MarkdownExport.Build(state, new MarkdownExportOptions()),
                              f => f.RelativePath.EndsWith("index.md", StringComparison.Ordinal));

        string index = Content(MarkdownExport.Build(state, new MarkdownExportOptions { Index = true }), "index.md");
        Assert.Contains("[[Get orders]]", index);
        Assert.Contains("[[Create order]]", index);
        Assert.Contains("requests: 2", index);
    }

    // ---------------------------------------------------------------- the note itself

    [Fact]
    public void A_request_note_carries_frontmatter_a_vault_can_query()
    {
        var node = Request("Get orders", "GET", "https://api.internal/orders");
        node.LastStatusCode = 200;
        node.LastCheckedUtc = new DateTime(2026, 7, 28, 9, 14, 0, DateTimeKind.Utc);
        var state = StateWith(Folder("Orders", node));

        string note = Content(MarkdownExport.Build(state, new MarkdownExportOptions()), "Get orders.md");

        Assert.StartsWith("---\n", note.Replace("\r\n", "\n"));
        Assert.Contains("tags: [certapi/request, certapi/collection/Orders]", note);
        Assert.Contains("method: GET", note);
        Assert.Contains("host: api.internal", note);
        Assert.Contains("lastStatus: 200", note);
        Assert.Contains("lastChecked: 2026-07-28T09:14:00Z", note);
    }

    [Fact]
    public void A_request_links_to_its_collection_and_to_any_chain_that_uses_it()
    {
        var node = Request("Get orders");
        var state = StateWith(Folder("Orders", node));
        state.Chains.Add(new RequestChain
        {
            Name = "Login then fetch",
            Steps = { new ChainStep { RequestId = node.Id } }
        });

        string note = Content(MarkdownExport.Build(state, new MarkdownExportOptions()), "Get orders.md");

        Assert.Contains("Collection: [[Orders]]", note);
        Assert.Contains("Used by: [[Login then fetch]]", note);
    }

    [Fact]
    public void Headers_assertions_and_captures_are_rendered()
    {
        var node = Request("Get orders", configure: model =>
        {
            model.Headers.Add(new HeaderRow { Enabled = true, Name = "Accept", Value = "application/json" });
            model.Headers.Add(new HeaderRow { Enabled = false, Name = "X-Off", Value = "no" });
            model.Assertions.Add(new AssertionRule
            {
                Enabled = true, Target = AssertTarget.Status, Op = AssertOp.Equals, Value = "200"
            });
            model.Assertions.Add(new AssertionRule
            {
                Enabled = true, Target = AssertTarget.Body, Path = "orders", Op = AssertOp.Exists
            });
            model.Captures.Add(new CaptureRule
            {
                Enabled = true, Variable = "orderId", Source = CaptureSource.Body, Path = "id"
            });
        });

        string note = Content(MarkdownExport.Build(StateWith(node), new MarkdownExportOptions()), "Get orders.md");

        Assert.Contains("| Accept | application/json |", note);
        Assert.DoesNotContain("X-Off", note);          // a disabled header is not part of the request
        Assert.Contains("- status == 200", note);
        Assert.Contains("- body.orders exists", note);
        Assert.Contains("`{{orderId}}`", note);
    }

    [Fact]
    public void A_chain_note_lists_its_steps_in_order_and_links_them()
    {
        var first = Request("Log in", "POST");
        var second = Request("Get orders");
        var state = StateWith(Folder("Orders", first, second));
        state.Chains.Add(new RequestChain
        {
            Name = "Login then fetch",
            EnvironmentName = "Staging",
            Steps =
            {
                new ChainStep { RequestId = first.Id },
                new ChainStep { RequestId = second.Id, StopOnFailure = false },
            }
        });

        string note = Content(MarkdownExport.Build(state, new MarkdownExportOptions()), "Login then fetch.md");

        Assert.Contains("1. [[Log in]]", note);
        Assert.Contains("2. [[Get orders]] — continues on failure", note);
        Assert.Contains("Environment: [[Staging]]", note);
    }

    [Fact]
    public void A_chain_step_whose_request_was_deleted_says_so_instead_of_linking_nowhere()
    {
        // A wikilink to a note that does not exist reads as an export bug. It is a data problem,
        // and the note should say which.
        var state = StateWith(Request("Get orders"));
        state.Chains.Add(new RequestChain
        {
            Name = "Broken", Steps = { new ChainStep { RequestId = "gone" } }
        });

        string note = Content(MarkdownExport.Build(state, new MarkdownExportOptions()), "Broken.md");

        Assert.Contains("_(missing request)_", note);
        Assert.DoesNotContain("[[gone]]", note);
    }

    // ---------------------------------------------------------------- secrets

    [Fact]
    public void A_credential_header_is_redacted_but_its_presence_is_still_recorded()
    {
        var node = Request("Get orders", configure: model =>
        {
            model.Headers.Add(new HeaderRow { Enabled = true, Name = "Authorization", Value = "Bearer sekrit-42" });
            model.Headers.Add(new HeaderRow { Enabled = true, Name = "Cookie", Value = "session=abc" });
        });
        var state = StateWith(node);

        string note = Content(MarkdownExport.Build(state, new MarkdownExportOptions()), "Get orders.md");

        Assert.DoesNotContain("sekrit-42", note);
        Assert.DoesNotContain("session=abc", note);
        Assert.Contains("| Authorization |", note);     // that it IS sent is the catalogue's business
        Assert.Contains("redacted", note);

        string opened = Content(
            MarkdownExport.Build(state, new MarkdownExportOptions { IncludeSecrets = true }), "Get orders.md");
        Assert.Contains("Bearer sekrit-42", opened);
    }

    [Fact]
    public void An_auth_secret_never_reaches_the_note_by_default()
    {
        var node = Request("Get orders", configure: model =>
        {
            model.AuthType = "Bearer";
            model.AuthUser = "svc-account";
            model.AuthSecret = "sekrit-42";
        });

        string note = Content(MarkdownExport.Build(StateWith(node), new MarkdownExportOptions()), "Get orders.md");

        Assert.DoesNotContain("sekrit-42", note);
        Assert.Contains("svc-account", note);           // the user name is not the secret
        Assert.Contains("Secret: *(redacted)*", note);
    }

    [Fact]
    public void A_secret_environment_variable_is_redacted_on_its_own_say_so()
    {
        var state = new AppState();
        var environment = new ApiEnvironment { Name = "Staging" };
        environment.Variables.Add(new Variable { Key = "baseUrl", Value = "https://api.internal" });
        environment.Variables.Add(new Variable { Key = "token", Value = "sekrit-42", Secret = true });
        state.Environments.Add(environment);

        string note = Content(MarkdownExport.Build(state, new MarkdownExportOptions()), "Staging.md");

        Assert.DoesNotContain("sekrit-42", note);
        Assert.Contains("https://api.internal", note);  // a non-secret variable is the point of the note
        Assert.Contains("| token |", note);
    }

    // ---------------------------------------------------------------- escaping

    [Theory]
    [InlineData("Orders / v2", "Orders - v2")]
    [InlineData("what:now?", "what-now-")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("", "fallback")]
    [InlineData("CON", "CON-note")]                     // a real Windows device name
    [InlineData("dots...", "dots")]
    public void Filenames_are_safe_on_windows_and_stable(string name, string expected)
    {
        Assert.Equal(expected, MarkdownExport.Sanitize(name, "fallback"));
    }

    [Fact]
    public void A_name_with_markdown_in_it_is_escaped_in_prose_and_sanitised_in_the_filename()
    {
        // The spec's own example: this must not render as italics, and must not break the link.
        var state = StateWith(Folder("Orders", Request("Orders / v2 *beta*")));

        var files = MarkdownExport.Build(state, new MarkdownExportOptions { Index = true });
        var note = files.Single(f => f.RelativePath.Contains("Orders - v2"));

        // Two different jobs, and they must not be confused: the FILENAME is sanitised (an asterisk
        // is illegal on Windows, so it becomes a dash), while the PROSE keeps the name the user
        // chose and escapes it so it does not render as italics.
        Assert.Equal("certapi/Orders/Orders - v2 -beta-.md", note.RelativePath);
        Assert.Contains("# Orders / v2 \\*beta\\*", note.Content);

        // And the index's wikilink must name the same note the file was written as.
        string index = Content(files, "index.md");
        Assert.Contains("[[Orders - v2 -beta-]]", index);
    }

    [Fact]
    public void A_pipe_in_a_value_cannot_break_the_table_it_sits_in()
    {
        var node = Request("Get orders", configure: model =>
            model.Headers.Add(new HeaderRow { Enabled = true, Name = "Accept", Value = "a|b" }));

        string note = Content(MarkdownExport.Build(StateWith(node), new MarkdownExportOptions()), "Get orders.md");

        Assert.Contains(@"| Accept | a\|b |", note);
    }

    [Fact]
    public void A_body_containing_a_fence_does_not_end_its_own_code_block()
    {
        // Otherwise the rest of the body spills into the note as prose — which is also how a
        // document that looks redacted quietly stops being one.
        string fenced = MarkdownExport.Fence("before\n```\nafter", "application/json");

        Assert.StartsWith("````json", fenced);
        Assert.EndsWith("````", fenced);
        Assert.Contains("after", fenced);
    }

    [Fact]
    public void Two_requests_with_the_same_name_get_distinct_notes()
    {
        // Wikilinks resolve on the title, so identical titles would point at one note and the
        // second file would overwrite the first.
        var state = StateWith(
            Folder("Orders", Request("Get")),
            Folder("Users", Request("Get")));

        var files = MarkdownExport.Build(state, new MarkdownExportOptions());

        Assert.Equal(2, files.Count);
        Assert.Equal(2, files.Select(f => f.RelativePath).Distinct().Count());
        Assert.Contains(files, f => f.RelativePath == "certapi/Users/Get (Users).md");
    }

    [Fact]
    public void Re_exporting_the_same_workspace_produces_exactly_the_same_files()
    {
        // Idempotence is the property that makes a vault re-export safe: same paths, same content,
        // no "Orders 2.md" accumulating on every run.
        var state = StateWith(Folder("Orders", Request("Get orders"), Request("Create order")));
        state.Environments.Add(new ApiEnvironment { Name = "Staging" });

        var first = MarkdownExport.Build(state, new MarkdownExportOptions { Index = true });
        var second = MarkdownExport.Build(state, new MarkdownExportOptions { Index = true });

        Assert.Equal(first.Select(f => f.RelativePath), second.Select(f => f.RelativePath));
        Assert.Equal(first.Select(f => f.Content), second.Select(f => f.Content));
    }
}
