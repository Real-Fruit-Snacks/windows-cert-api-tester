using ApiTester.Core;

namespace ApiTester.Tests;

public class HarToCollectionTests
{
    private static Har BuildHar(params (string Method, string Url, int Status, (string Name, string Value)[]? Headers, string? Body)[] entries)
    {
        var har = new Har();
        foreach (var e in entries)
        {
            var headers = (e.Headers ?? Array.Empty<(string Name, string Value)>())
                .Select(h => new HarNameValue(h.Name, h.Value)).ToList();

            var entry = new HarEntry
            {
                Request = new HarRequest
                {
                    Method = e.Method,
                    Url = e.Url,
                    Headers = headers,
                    PostData = e.Body is null ? null : new HarPostData { MimeType = "application/json", Text = e.Body }
                },
                Response = new HarResponse { Status = e.Status, StatusText = e.Status is >= 200 and < 300 ? "OK" : "Error" }
            };
            har.Log.Entries.Add(entry);
        }
        return har;
    }

    [Fact]
    public void Two_entries_with_the_same_value_at_a_candidate_position_stay_literal()
    {
        var har = BuildHar(
            ("GET", "https://api.example.com/orders/42", 200, null, null),
            ("GET", "https://api.example.com/orders/42", 200, null, null));

        var collection = HarToCollection.Group(har);

        Assert.Single(collection.Requests);
        Assert.Equal("https://api.example.com/orders/42", collection.Requests[0].Url);
    }

    public static IEnumerable<object[]> TemplatingCases => new List<object[]>
    {
        new object[] { "123", "456", true },                                     // all digits
        new object[] { "550e8400-e29b-41d4-a716-446655440000",
                       "660e8400-e29b-41d4-a716-446655440001", true },            // UUID shape
        new object[] { "0123456789abcdef0", "1123456789abcdef0", true },         // >= 16 hex chars
        new object[] { "orders", "invoices", false },                            // plain word
        new object[] { "v1", "v2", false },                                      // short alnum
        new object[] { "abc123", "def456", false },                              // short mixed
        new object[] { "0123456789abcde", "1123456789abcde", false },            // 15-char hex
    };

    [Theory]
    [MemberData(nameof(TemplatingCases))]
    public void Segment_templates_only_when_it_looks_like_an_identifier_and_varies(
        string firstValue, string secondValue, bool shouldTemplate)
    {
        var har = BuildHar(
            ("GET", $"https://api.example.com/things/{firstValue}", 200, null, null),
            ("GET", $"https://api.example.com/things/{secondValue}", 200, null, null));

        var collection = HarToCollection.Group(har);

        if (shouldTemplate)
        {
            Assert.Single(collection.Requests);
            Assert.Equal("https://api.example.com/things/{id}", collection.Requests[0].Url);
        }
        else
        {
            Assert.Equal(2, collection.Requests.Count);
            Assert.Contains(collection.Requests, r => r.Url == $"https://api.example.com/things/{firstValue}");
            Assert.Contains(collection.Requests, r => r.Url == $"https://api.example.com/things/{secondValue}");
        }
    }

    [Fact]
    public void Varying_id_segments_collapse_into_a_single_templated_request()
    {
        var har = BuildHar(
            ("GET", "https://api.example.com/orders/1", 200, null, null),
            ("GET", "https://api.example.com/orders/2", 200, null, null));

        var collection = HarToCollection.Group(har);

        Assert.Single(collection.Requests);
        Assert.Equal("https://api.example.com/orders/{id}", collection.Requests[0].Url);
    }

    [Fact]
    public void Two_varying_positions_get_distinct_positional_placeholder_names()
    {
        var har = BuildHar(
            ("GET", "https://api.example.com/users/1/roles/7", 200, null, null),
            ("GET", "https://api.example.com/users/2/roles/8", 200, null, null));

        var collection = HarToCollection.Group(har);

        Assert.Single(collection.Requests);
        Assert.Equal("https://api.example.com/users/{id}/roles/{id2}", collection.Requests[0].Url);
    }

    [Fact]
    public void TemplateIds_false_leaves_paths_literal_and_produces_separate_requests()
    {
        var har = BuildHar(
            ("GET", "https://api.example.com/orders/1", 200, null, null),
            ("GET", "https://api.example.com/orders/2", 200, null, null));

        var collection = HarToCollection.Group(har, new HarGroupOptions { TemplateIds = false });

        Assert.Equal(2, collection.Requests.Count);
        Assert.Contains(collection.Requests, r => r.Url == "https://api.example.com/orders/1");
        Assert.Contains(collection.Requests, r => r.Url == "https://api.example.com/orders/2");
    }

    [Fact]
    public void Header_kept_only_when_identical_across_every_entry_in_the_group()
    {
        var har = BuildHar(
            ("GET", "https://api.example.com/orders/1", 200,
                new[] { ("Accept", "application/json"), ("X-Only-First", "yes"), ("X-Varies", "a") }, null),
            ("GET", "https://api.example.com/orders/2", 200,
                new[] { ("Accept", "application/json"), ("X-Varies", "b") }, null));

        var collection = HarToCollection.Group(har);

        var request = Assert.Single(collection.Requests);
        Assert.Contains(request.Headers, h => h.Key == "Accept" && h.Value == "application/json");
        Assert.DoesNotContain(request.Headers, h => h.Key == "X-Only-First");
        Assert.DoesNotContain(request.Headers, h => h.Key == "X-Varies");
    }

    [Fact]
    public void Redacted_header_value_is_never_carried_into_the_merged_request()
    {
        var har = BuildHar(
            ("GET", "https://api.example.com/orders/1", 200,
                new[] { ("Authorization", "[redacted]") }, null),
            ("GET", "https://api.example.com/orders/2", 200,
                new[] { ("Authorization", "[redacted]") }, null));

        var collection = HarToCollection.Group(har);

        var request = Assert.Single(collection.Requests);
        Assert.DoesNotContain(request.Headers, h => h.Key == "Authorization");
    }

    [Fact]
    public void Entries_at_or_above_MaxStatus_are_excluded_by_default_but_included_when_raised()
    {
        var har = BuildHar(
            ("GET", "https://api.example.com/missing", 404, null, null));

        var defaultCollection = HarToCollection.Group(har);
        Assert.Empty(defaultCollection.Requests);

        var raisedCollection = HarToCollection.Group(har, new HarGroupOptions { MaxStatus = 500 });
        Assert.Single(raisedCollection.Requests);
    }

    [Fact]
    public void Host_filter_keeps_only_the_named_host_case_insensitively()
    {
        var har = BuildHar(
            ("GET", "https://api.example.com/a", 200, null, null),
            ("GET", "https://other.example.com/b", 200, null, null));

        var collection = HarToCollection.Group(har, new HarGroupOptions { Host = "API.EXAMPLE.COM" });

        var request = Assert.Single(collection.Requests);
        Assert.Equal("https://api.example.com/a", request.Url);
    }

    [Fact]
    public void Several_hosts_produce_one_folder_per_host_each_with_its_own_base_url()
    {
        var har = BuildHar(
            ("GET", "https://api.example.com/a", 200, null, null),
            ("GET", "https://other.example.com/b", 200, null, null));

        var collection = HarToCollection.Group(har);

        Assert.Equal("HAR capture", collection.Name);
        Assert.Null(collection.BaseUrl);
        Assert.Empty(collection.Requests);
        Assert.Equal(2, collection.Folders.Count);

        var first = collection.Folders.Single(f => f.Name == "api.example.com");
        Assert.Equal("https://api.example.com", first.BaseUrl);
        Assert.Single(first.Requests);

        var second = collection.Folders.Single(f => f.Name == "other.example.com");
        Assert.Equal("https://other.example.com", second.BaseUrl);
        Assert.Single(second.Requests);
    }

    [Fact]
    public void First_non_empty_body_becomes_the_example_and_query_keys_from_every_entry_are_merged()
    {
        var har = BuildHar(
            ("POST", "https://api.example.com/orders/1?source=web", 200, null, null),
            ("POST", "https://api.example.com/orders/2?locale=en-us", 200, null, "{\"item\":\"widget\"}"));

        var collection = HarToCollection.Group(har);

        var request = Assert.Single(collection.Requests);
        Assert.Equal("{\"item\":\"widget\"}", request.Body);
        Assert.Equal("application/json", request.ContentType);
        Assert.Contains("source=web", request.Url);
        Assert.Contains("locale=en-us", request.Url);
    }

    [Fact]
    public void Archive_with_every_entry_filtered_out_returns_an_empty_collection_without_throwing()
    {
        var har = BuildHar(
            ("GET", "https://api.example.com/gone", 404, null, null));

        var collection = HarToCollection.Group(har, new HarGroupOptions { Host = "nonexistent.example.com" });

        Assert.Equal("HAR capture", collection.Name);
        Assert.Empty(collection.Requests);
        Assert.Empty(collection.Folders);
    }

    [Fact]
    public void Grouped_har_survives_a_round_trip_through_the_openapi_exporter_and_importer()
    {
        var har = BuildHar(
            ("GET", "https://api.example.com/orders/1", 200, null, null),
            ("GET", "https://api.example.com/orders/2", 200, null, null),
            ("POST", "https://api.example.com/orders", 201, null, "{\"item\":\"widget\"}"));

        var collection = HarToCollection.Group(har);
        string json = OpenApiExporter.ToJson(collection);
        var imported = OpenApiImporter.Parse(json);

        var all = imported.Requests.Concat(imported.Folders.SelectMany(f => f.Requests)).ToList();
        Assert.Contains(all, r => r.Method == "GET" && r.Url == "/orders/{id}");
        Assert.Contains(all, r => r.Method == "POST" && r.Url == "/orders");
    }
}
