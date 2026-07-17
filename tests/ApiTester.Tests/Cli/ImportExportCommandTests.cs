using System.IO;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class ImportExportCommandTests
{
    private static (CliServices Services, string LivePath) FreshServices(bool guiRunning = false)
    {
        var live = Path.Combine(Path.GetTempPath(), $"certapi-ie-{Guid.NewGuid():N}.json");
        return (new CliServices { LiveStatePath = live, IsGuiRunning = () => guiRunning }, live);
    }

    [Fact]
    public void Import_curl_saves_a_request_into_a_folder()
    {
        var (services, live) = FreshServices();
        try
        {
            int code = CliApp.Run(
                new[] { "import", "curl", "curl -X POST https://h/api -H 'X: 1' -d '{}'", "--into", "imported" },
                new StringWriter(), new StringWriter(), services: services);
            Assert.Equal(0, code);

            var state = AppState.LoadFrom(live);
            var folder = Assert.Single(state.Collections);
            Assert.Equal("imported", folder.Name);
            var leaf = Assert.Single(folder.Children);
            Assert.Equal("POST", leaf.Request!.Method);
        }
        finally { File.Delete(live); }
    }

    [Fact]
    public void Import_into_live_state_while_gui_runs_is_a_data_error()
    {
        var (services, live) = FreshServices(guiRunning: true);
        var se = new StringWriter();
        int code = CliApp.Run(new[] { "import", "curl", "curl https://h/" }, new StringWriter(), se, services: services);
        Assert.Equal(3, code);
        Assert.Contains("--workspace", se.ToString());
    }

    [Fact]
    public void Import_openapi_and_export_openapi_round_trip()
    {
        var (services, live) = FreshServices();
        var spec = Path.Combine(Path.GetTempPath(), $"certapi-oas-{Guid.NewGuid():N}.json");
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-out-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(spec, """
                { "openapi": "3.0.3", "info": { "title": "Round", "version": "1" },
                  "servers": [ { "url": "https://api" } ],
                  "paths": { "/health": { "get": { "summary": "Health" } } } }
                """);
            Assert.Equal(0, CliApp.Run(new[] { "import", "openapi", spec }, new StringWriter(), new StringWriter(), services: services));
            Assert.Equal(0, CliApp.Run(new[] { "export", "openapi", "-o", outFile }, new StringWriter(), new StringWriter(), services: services));

            var back = OpenApiImporter.Parse(File.ReadAllText(outFile));
            Assert.Contains(back.Folders.SelectMany(f => f.Requests).Concat(back.Requests),
                            r => r.Url == "/health" && r.Method == "GET");
        }
        finally { File.Delete(live); File.Delete(spec); File.Delete(outFile); }
    }

    [Fact]
    public void Export_workspace_writes_a_loadable_file_without_window_geometry()
    {
        var (services, live) = FreshServices();
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-ws-{Guid.NewGuid():N}.json");
        try
        {
            var state = new AppState { WindowWidth = 1000 };
            state.Collections.Add(new CollectionNode { Name = "keep", IsFolder = true });
            state.SaveTo(live);

            Assert.Equal(0, CliApp.Run(new[] { "export", "workspace", "-o", outFile }, new StringWriter(), new StringWriter(), services: services));
            var exported = AppState.LoadFrom(outFile);
            Assert.Null(exported.WindowWidth);
            Assert.Equal("keep", Assert.Single(exported.Collections).Name);
        }
        finally { File.Delete(live); File.Delete(outFile); }
    }
}
