using System.IO;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class HarImportCliTests
{
    private const string ValidHar = """
        {"log":{"version":"1.2","entries":[{"startedDateTime":"2026-01-01T00:00:00Z","time":1,"request":{"method":"GET","url":"https://api.example.com/health","headers":[],"queryString":[]},"response":{"status":200,"statusText":"OK","headers":[],"content":{"size":0,"mimeType":"","text":""}},"timings":{"send":-1,"wait":1,"receive":-1}}]}}
        """;

    private static (CliServices Services, string LivePath) FreshServices(bool guiRunning = false)
    {
        var live = Path.Combine(Path.GetTempPath(), $"certapi-har-{Guid.NewGuid():N}.json");
        return (new CliServices { LiveStatePath = live, IsGuiRunning = () => guiRunning }, live);
    }

    [Fact]
    public void Import_har_builds_a_collection_from_the_archive()
    {
        var (services, live) = FreshServices();
        var har = Path.Combine(Path.GetTempPath(), $"certapi-session-{Guid.NewGuid():N}.har");
        try
        {
            File.WriteAllText(har, ValidHar);
            int code = CliApp.Run(
                new[] { "import", "har", har },
                new StringWriter(), new StringWriter(), services: services);
            Assert.Equal(0, code);

            var state = AppState.LoadFrom(live);
            var folder = Assert.Single(state.Collections);
            Assert.Equal(Path.GetFileNameWithoutExtension(har), folder.Name);
            var leaf = Assert.Single(folder.Children);
            Assert.Equal("GET", leaf.Request!.Method);
            Assert.Contains("api.example.com/health", leaf.Request!.EffectiveUrl());
        }
        finally { File.Delete(har); if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public void Import_har_into_nests_the_archive_folder_under_the_named_folder()
    {
        var (services, live) = FreshServices();
        var har = Path.Combine(Path.GetTempPath(), $"certapi-session-{Guid.NewGuid():N}.har");
        try
        {
            File.WriteAllText(har, ValidHar);
            int code = CliApp.Run(
                new[] { "import", "har", har, "--into", "imported" },
                new StringWriter(), new StringWriter(), services: services);
            Assert.Equal(0, code);

            var state = AppState.LoadFrom(live);
            var into = Assert.Single(state.Collections);
            Assert.Equal("imported", into.Name);
            var archiveFolder = Assert.Single(into.Children);
            Assert.Equal(Path.GetFileNameWithoutExtension(har), archiveFolder.Name);
            Assert.Single(archiveFolder.Children);
        }
        finally { File.Delete(har); if (File.Exists(live)) File.Delete(live); }
    }

    [Fact]
    public void Malformed_har_file_is_a_data_error_without_a_stack_trace()
    {
        var (services, live) = FreshServices();
        var bad = Path.Combine(Path.GetTempPath(), $"certapi-bad-{Guid.NewGuid():N}.har");
        try
        {
            File.WriteAllText(bad, "{ not json ");
            var se = new StringWriter();
            int code = CliApp.Run(new[] { "import", "har", bad }, new StringWriter(), se, services: services);
            Assert.Equal(3, code);
            Assert.NotEmpty(se.ToString());
            Assert.DoesNotContain("   at ", se.ToString());
        }
        finally { File.Delete(bad); if (File.Exists(live)) File.Delete(live); }
    }
}
