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
