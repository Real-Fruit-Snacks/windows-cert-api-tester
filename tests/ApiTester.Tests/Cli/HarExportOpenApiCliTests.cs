using System.IO;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class HarExportOpenApiCliTests
{
    private static (CliServices Services, string LivePath) FreshServices()
    {
        var live = Path.Combine(Path.GetTempPath(), $"certapi-harexp-{Guid.NewGuid():N}.json");
        return (new CliServices { LiveStatePath = live }, live);
    }

    private static string Entry(string method, string url, int status) =>
        "{\"startedDateTime\":\"2026-01-01T00:00:00Z\",\"time\":1," +
        $"\"request\":{{\"method\":\"{method}\",\"url\":\"{url}\",\"headers\":[],\"queryString\":[]}}," +
        $"\"response\":{{\"status\":{status},\"statusText\":\"OK\",\"headers\":[],\"content\":{{\"size\":0,\"mimeType\":\"\",\"text\":\"\"}}}}," +
        "\"timings\":{\"send\":-1,\"wait\":1,\"receive\":-1}}";

    private static string BuildHar(params string[] entries) =>
        "{\"log\":{\"version\":\"1.2\",\"entries\":[" + string.Join(",", entries) + "]}}";

    private static string WriteTempHar(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"certapi-session-{Guid.NewGuid():N}.har");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void From_har_exports_repeated_calls_as_one_templated_openapi_operation()
    {
        var (services, live) = FreshServices();
        string har = BuildHar(
            Entry("GET", "https://api.example.com/orders/1", 200),
            Entry("GET", "https://api.example.com/orders/2", 200));
        string harPath = WriteTempHar(har);
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-out-{Guid.NewGuid():N}.json");
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "export", "openapi", "--from-har", harPath, "-o", outFile },
                new StringWriter(), se, services: services);
            Assert.Equal(0, code);

            var parsed = OpenApiImporter.Parse(File.ReadAllText(outFile));
            var all = parsed.Requests.Concat(parsed.Folders.SelectMany(f => f.Requests)).ToList();
            Assert.Contains(all, r => r.Method == "GET" && r.Url == "/orders/{id}");
            Assert.Contains("1 operation", se.ToString());
        }
        finally
        {
            File.Delete(harPath);
            File.Delete(outFile);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public void Host_filter_keeps_only_the_named_hosts_operations()
    {
        var (services, live) = FreshServices();
        string har = BuildHar(
            Entry("GET", "https://api.example.com/a", 200),
            Entry("GET", "https://other.example.com/b", 200));
        string harPath = WriteTempHar(har);
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-out-{Guid.NewGuid():N}.json");
        try
        {
            int code = CliApp.Run(
                new[] { "export", "openapi", "--from-har", harPath, "-o", outFile, "--host", "api.example.com" },
                new StringWriter(), new StringWriter(), services: services);
            Assert.Equal(0, code);

            var parsed = OpenApiImporter.Parse(File.ReadAllText(outFile));
            var all = parsed.Requests.Concat(parsed.Folders.SelectMany(f => f.Requests)).ToList();
            Assert.Single(all);
            Assert.Contains(all, r => r.Url == "/a");
        }
        finally
        {
            File.Delete(harPath);
            File.Delete(outFile);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public void No_template_ids_keeps_both_literal_paths_instead_of_collapsing_them()
    {
        var (services, live) = FreshServices();
        string har = BuildHar(
            Entry("GET", "https://api.example.com/orders/1", 200),
            Entry("GET", "https://api.example.com/orders/2", 200));
        string harPath = WriteTempHar(har);
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-out-{Guid.NewGuid():N}.json");
        try
        {
            int code = CliApp.Run(
                new[] { "export", "openapi", "--from-har", harPath, "-o", outFile, "--no-template-ids" },
                new StringWriter(), new StringWriter(), services: services);
            Assert.Equal(0, code);

            var parsed = OpenApiImporter.Parse(File.ReadAllText(outFile));
            var all = parsed.Requests.Concat(parsed.Folders.SelectMany(f => f.Requests)).ToList();
            Assert.Contains(all, r => r.Url == "/orders/1");
            Assert.Contains(all, r => r.Url == "/orders/2");
            Assert.DoesNotContain(all, r => r.Url == "/orders/{id}");
        }
        finally
        {
            File.Delete(harPath);
            File.Delete(outFile);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public void Archive_with_everything_filtered_out_is_a_data_error_naming_the_status_filter()
    {
        var (services, live) = FreshServices();
        string har = BuildHar(Entry("GET", "https://api.example.com/missing", 404));
        string harPath = WriteTempHar(har);
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-out-{Guid.NewGuid():N}.json");
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "export", "openapi", "--from-har", harPath, "-o", outFile },
                new StringWriter(), se, services: services);
            Assert.Equal(3, code);
            Assert.Contains("400", se.ToString());
            Assert.DoesNotContain("   at ", se.ToString());
            Assert.False(File.Exists(outFile));
        }
        finally
        {
            File.Delete(harPath);
            if (File.Exists(outFile)) File.Delete(outFile);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public void Malformed_archive_is_a_data_error_without_a_stack_trace()
    {
        var (services, live) = FreshServices();
        var harPath = WriteTempHar("{ not json ");
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-out-{Guid.NewGuid():N}.json");
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "export", "openapi", "--from-har", harPath, "-o", outFile },
                new StringWriter(), se, services: services);
            Assert.Equal(3, code);
            Assert.NotEmpty(se.ToString());
            Assert.DoesNotContain("   at ", se.ToString());
        }
        finally
        {
            File.Delete(harPath);
            if (File.Exists(outFile)) File.Delete(outFile);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public void Missing_har_file_is_a_data_error()
    {
        var (services, live) = FreshServices();
        var harPath = Path.Combine(Path.GetTempPath(), $"certapi-nope-{Guid.NewGuid():N}.har");
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-out-{Guid.NewGuid():N}.json");
        try
        {
            var se = new StringWriter();
            int code = CliApp.Run(
                new[] { "export", "openapi", "--from-har", harPath, "-o", outFile },
                new StringWriter(), se, services: services);
            Assert.Equal(3, code);
        }
        finally
        {
            if (File.Exists(outFile)) File.Delete(outFile);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public void Host_option_without_from_har_is_a_usage_error()
    {
        var (services, live) = FreshServices();
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-out-{Guid.NewGuid():N}.json");
        try
        {
            int code = CliApp.Run(
                new[] { "export", "openapi", "-o", outFile, "--host", "api.example.com" },
                new StringWriter(), new StringWriter(), services: services);
            Assert.Equal(2, code);
        }
        finally
        {
            if (File.Exists(outFile)) File.Delete(outFile);
            if (File.Exists(live)) File.Delete(live);
        }
    }

    [Fact]
    public void Folder_positional_together_with_from_har_is_a_usage_error()
    {
        var (services, live) = FreshServices();
        string har = BuildHar(Entry("GET", "https://api.example.com/a", 200));
        string harPath = WriteTempHar(har);
        var outFile = Path.Combine(Path.GetTempPath(), $"certapi-out-{Guid.NewGuid():N}.json");
        try
        {
            int code = CliApp.Run(
                new[] { "export", "openapi", "some-folder", "--from-har", harPath, "-o", outFile },
                new StringWriter(), new StringWriter(), services: services);
            Assert.Equal(2, code);
        }
        finally
        {
            File.Delete(harPath);
            if (File.Exists(outFile)) File.Delete(outFile);
            if (File.Exists(live)) File.Delete(live);
        }
    }
}
