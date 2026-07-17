using System.IO;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class CliWorkspaceTests
{
    private static AppState Sample()
    {
        var state = new AppState();
        var folder = new CollectionNode { Name = "api", IsFolder = true };
        folder.Children.Add(new CollectionNode { Name = "Get todo", Request = new RequestModel { Method = "GET", Path = "https://h/todo" } });
        folder.Children.Add(new CollectionNode { Name = "Health", Request = new RequestModel { Method = "GET", Path = "https://h/health" } });
        state.Collections.Add(folder);
        state.Environments.Add(new ApiEnvironment { Name = "Dev", Variables = { new Variable { Key = "host", Value = "dev.local" } } });
        return state;
    }

    [Fact]
    public void Missing_live_state_is_an_empty_workspace_but_missing_explicit_file_is_an_error()
    {
        var nowhere = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json");
        var state = CliWorkspace.Load(null, liveStatePath: nowhere);
        Assert.Empty(state.Collections);

        Assert.Throws<CliDataException>(() => CliWorkspace.Load(nowhere, liveStatePath: nowhere));
    }

    [Fact]
    public void Vars_come_from_the_named_env_and_overrides_win()
    {
        var vars = CliWorkspace.BuildVars(Sample(), "dev", new[] { "host=ci.local", "extra=1" });
        Assert.Equal("ci.local", vars["host"]);
        Assert.Equal("1", vars["extra"]);
        Assert.Throws<CliDataException>(() => CliWorkspace.BuildVars(Sample(), "prod", Array.Empty<string>()));
        Assert.Throws<CliUsageException>(() => CliWorkspace.BuildVars(Sample(), null, new[] { "novalue" }));
    }

    [Fact]
    public void Corrupt_workspace_file_is_a_data_error()
    {
        var path = Path.Combine(Path.GetTempPath(), $"certapi-corrupt-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ this is not json ");
            var ex = Assert.Throws<CliDataException>(() => CliWorkspace.Load(path, liveStatePath: path));
            Assert.Contains("Could not read", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Var_override_with_a_blank_key_is_a_usage_error()
    {
        Assert.Throws<CliUsageException>(() =>
            CliWorkspace.BuildVars(new AppState(), null, new[] { " =value" }));
        Assert.Throws<CliUsageException>(() =>
            CliWorkspace.BuildVars(new AppState(), null, new[] { "=value" }));
    }

    [Fact]
    public void Ambiguous_segment_lists_the_candidates()
    {
        var state = new AppState();
        state.Collections.Add(new CollectionNode { Name = "api", IsFolder = true });
        state.Collections.Add(new CollectionNode
        {
            Name = "api",
            IsFolder = false,
            Request = new RequestModel { Method = "GET", Path = "https://h/" }
        });

        var ex = Assert.Throws<CliDataException>(() => CliWorkspace.ResolveTargets(state, "api", all: false));
        Assert.Contains("(folder)", ex.Message);
        Assert.Contains("(request)", ex.Message);
    }

    [Fact]
    public void A_request_entry_without_a_request_is_a_data_error()
    {
        var state = new AppState();
        state.Collections.Add(new CollectionNode { Name = "broken", IsFolder = false, Request = null });

        var ex = Assert.Throws<CliDataException>(() => CliWorkspace.ResolveTargets(state, "broken", all: false));
        Assert.Contains("no runnable request", ex.Message);
    }

    [Fact]
    public void Targets_resolve_by_path_folder_or_all()
    {
        var one = CliWorkspace.ResolveTargets(Sample(), "api/Get todo", all: false);
        Assert.Equal("api/Get todo", Assert.Single(one).Path);

        var suite = CliWorkspace.ResolveTargets(Sample(), "API", all: false);
        Assert.Equal(2, suite.Count);

        var everything = CliWorkspace.ResolveTargets(Sample(), null, all: true);
        Assert.Equal(2, everything.Count);

        Assert.Throws<CliDataException>(() => CliWorkspace.ResolveTargets(Sample(), "api/nope", all: false));
        Assert.Throws<CliDataException>(() => CliWorkspace.ResolveTargets(new AppState(), null, all: true));
    }
}
