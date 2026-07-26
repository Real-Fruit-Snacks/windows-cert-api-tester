using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

public class ResponseDiffTests
{
    private static ResponseSnapshot Snap(int status = 200, string body = "",
        string? contentType = "application/json", params (string, string)[] headers) =>
        new(status,
            headers.Select(h => new KeyValuePair<string, string>(h.Item1, h.Item2)).ToList(),
            Encoding.UTF8.GetBytes(body),
            contentType);

    private static ResponseSnapshot Bytes(byte[] body, int status = 200) =>
        new(status, Array.Empty<KeyValuePair<string, string>>(), body, "application/octet-stream");

    [Fact]
    public void Identical_responses_report_no_differences()
    {
        var a = Snap(200, """{"id":1}""", headers: ("Content-Type", "application/json"));
        var b = Snap(200, """{"id":1}""", headers: ("Content-Type", "application/json"));

        var diff = ResponseDiff.Compare(a, b);

        Assert.True(diff.Identical);
        Assert.Empty(diff.Headers);
        Assert.Empty(diff.Body);
    }

    [Fact]
    public void Status_fields_carry_both_statuses_even_when_they_match()
    {
        var diff = ResponseDiff.Compare(Snap(200), Snap(200));

        Assert.Equal(200, diff.StatusBefore);
        Assert.Equal(200, diff.StatusAfter);
    }

    [Fact]
    public void A_changed_status_is_a_difference()
    {
        var diff = ResponseDiff.Compare(Snap(200), Snap(503));

        Assert.False(diff.Identical);
        Assert.Equal(200, diff.StatusBefore);
        Assert.Equal(503, diff.StatusAfter);
    }

    [Fact]
    public void A_header_only_in_the_actual_response_is_added()
    {
        var diff = ResponseDiff.Compare(Snap(), Snap(headers: ("X-New", "yes")));

        var h = Assert.Single(diff.Headers);
        Assert.Equal("X-New", h.Name);
        Assert.Null(h.Before);
        Assert.Equal("yes", h.After);
        Assert.False(diff.Identical);
    }

    [Fact]
    public void A_header_only_in_the_baseline_is_removed()
    {
        var diff = ResponseDiff.Compare(Snap(headers: ("X-Gone", "was")), Snap());

        var h = Assert.Single(diff.Headers);
        Assert.Equal("X-Gone", h.Name);
        Assert.Equal("was", h.Before);
        Assert.Null(h.After);
    }

    [Fact]
    public void A_header_present_on_both_sides_with_a_different_value_is_changed()
    {
        var diff = ResponseDiff.Compare(
            Snap(headers: ("Content-Type", "application/json")),
            Snap(headers: ("content-type", "text/html")));   // name match is case-insensitive

        var h = Assert.Single(diff.Headers);
        Assert.Equal("application/json", h.Before);
        Assert.Equal("text/html", h.After);
    }

    [Fact]
    public void A_repeated_header_is_joined_into_one_comparable_value()
    {
        var baseline = Snap(headers: new[] { ("Vary", "Accept"), ("Vary", "Origin") });
        var actual = Snap(headers: ("Vary", "Accept, Origin"));

        Assert.Empty(ResponseDiff.Compare(baseline, actual).Headers);

        var changed = ResponseDiff.Compare(baseline, Snap(headers: ("Vary", "Accept")));
        var h = Assert.Single(changed.Headers);
        Assert.Equal("Accept, Origin", h.Before);
        Assert.Equal("Accept", h.After);
    }

    [Fact]
    public void CompareHeaderValues_false_hides_a_changed_value_but_not_a_missing_header()
    {
        var options = new DiffOptions { CompareHeaderValues = false };
        var baseline = Snap(headers: new[] { ("X-Same", "one"), ("X-Gone", "here") });
        var actual = Snap(headers: new[] { ("X-Same", "two"), ("X-New", "there") });

        var diff = ResponseDiff.Compare(baseline, actual, options);

        Assert.Equal(new[] { "X-Gone", "X-New" }, diff.Headers.Select(h => h.Name));
    }

    [Fact]
    public void Volatile_headers_are_ignored_by_default()
    {
        var baseline = Snap(headers: new[] { ("Date", "Mon, 01 Jan 2024 00:00:00 GMT"), ("ETag", "\"a\"") });
        var actual = Snap(headers: new[] { ("Date", "Tue, 02 Jan 2024 00:00:00 GMT"), ("ETag", "\"b\"") });

        var diff = ResponseDiff.Compare(baseline, actual);

        Assert.Empty(diff.Headers);
        Assert.True(diff.Identical);
    }

    [Fact]
    public void Header_diffs_come_back_ordered_by_name()
    {
        var actual = Snap(headers: new[] { ("X-Zulu", "z"), ("X-Alpha", "a"), ("X-Mike", "m") });

        var diff = ResponseDiff.Compare(Snap(), actual);

        Assert.Equal(new[] { "X-Alpha", "X-Mike", "X-Zulu" }, diff.Headers.Select(h => h.Name));
    }

    [Fact]
    public void A_nested_property_change_is_reported_at_its_dotted_path()
    {
        var diff = ResponseDiff.Compare(
            Snap(body: """{"data":{"user":{"name":"Ada","id":1}}}"""),
            Snap(body: """{"data":{"user":{"name":"Grace","id":1}}}"""));

        var d = Assert.Single(diff.Body);
        Assert.Equal("data.user.name", d.Path);
        Assert.Equal(DiffKind.Changed, d.Kind);
        Assert.Equal("\"Ada\"", d.Before);
        Assert.Equal("\"Grace\"", d.After);
        Assert.False(diff.Identical);
    }

    [Fact]
    public void A_nested_property_added_and_removed_are_reported_separately()
    {
        var diff = ResponseDiff.Compare(
            Snap(body: """{"data":{"user":{"name":"Ada","legacy":true}}}"""),
            Snap(body: """{"data":{"user":{"name":"Ada","email":"a@b.c"}}}"""));

        Assert.Equal(2, diff.Body.Count);
        var removed = diff.Body.Single(d => d.Kind == DiffKind.Removed);
        Assert.Equal("data.user.legacy", removed.Path);
        Assert.Equal("true", removed.Before);
        Assert.Null(removed.After);

        var added = diff.Body.Single(d => d.Kind == DiffKind.Added);
        Assert.Equal("data.user.email", added.Path);
        Assert.Null(added.Before);
        Assert.Equal("\"a@b.c\"", added.After);
    }

    [Fact]
    public void Reordered_object_properties_are_not_a_difference()
    {
        var diff = ResponseDiff.Compare(
            Snap(body: """{"a":1,"b":2}"""),
            Snap(body: """{"b":2,"a":1}"""));

        Assert.True(diff.Identical);
    }

    [Fact]
    public void A_string_becoming_a_number_is_a_type_change_not_a_value_change()
    {
        var diff = ResponseDiff.Compare(
            Snap(body: """{"id":"1"}"""),
            Snap(body: """{"id":1}"""));

        var d = Assert.Single(diff.Body);
        Assert.Equal("id", d.Path);
        Assert.Equal(DiffKind.TypeChanged, d.Kind);
        Assert.Equal("\"1\"", d.Before);
        Assert.Equal("1", d.After);
    }

    [Fact]
    public void A_container_that_changed_type_is_reported_once_and_not_walked_into()
    {
        var diff = ResponseDiff.Compare(
            Snap(body: """{"data":{"a":1,"b":2}}"""),
            Snap(body: """{"data":[1,2]}"""));

        var d = Assert.Single(diff.Body);
        Assert.Equal("data", d.Path);
        Assert.Equal(DiffKind.TypeChanged, d.Kind);
    }

    [Fact]
    public void True_to_false_is_a_change_because_both_are_booleans()
    {
        var diff = ResponseDiff.Compare(
            Snap(body: """{"ok":true}"""),
            Snap(body: """{"ok":false}"""));

        var d = Assert.Single(diff.Body);
        Assert.Equal(DiffKind.Changed, d.Kind);
        Assert.Equal("true", d.Before);
        Assert.Equal("false", d.After);
    }

    [Fact]
    public void Array_elements_are_compared_by_index()
    {
        var diff = ResponseDiff.Compare(
            Snap(body: """{"items":[{"id":1},{"id":2}]}"""),
            Snap(body: """{"items":[{"id":1},{"id":9}]}"""));

        var d = Assert.Single(diff.Body);
        Assert.Equal("items[1].id", d.Path);
        Assert.Equal(DiffKind.Changed, d.Kind);
    }

    [Fact]
    public void An_extra_array_element_is_added_and_a_missing_one_is_removed()
    {
        var grew = ResponseDiff.Compare(
            Snap(body: """{"items":["a","b"]}"""),
            Snap(body: """{"items":["a","b","c"]}"""));
        var added = Assert.Single(grew.Body);
        Assert.Equal("items[2]", added.Path);
        Assert.Equal(DiffKind.Added, added.Kind);
        Assert.Equal("\"c\"", added.After);

        var shrank = ResponseDiff.Compare(
            Snap(body: """{"items":["a","b"]}"""),
            Snap(body: """{"items":["a"]}"""));
        var removed = Assert.Single(shrank.Body);
        Assert.Equal("items[1]", removed.Path);
        Assert.Equal(DiffKind.Removed, removed.Kind);
        Assert.Equal("\"b\"", removed.Before);
    }

    [Fact]
    public void A_root_level_array_uses_bare_index_paths()
    {
        var diff = ResponseDiff.Compare(
            Snap(body: """[{"k":"v0"},{"k":"v1"}]"""),
            Snap(body: """[{"k":"v0"},{"k":"changed"}]"""));

        Assert.Equal("[1].k", Assert.Single(diff.Body).Path);
    }

    [Fact]
    public void A_reported_path_can_be_read_back_by_JsonPath()
    {
        // The real contract between the two: an ignore path is written the way a capture path is,
        // so every path this engine emits must evaluate against the document it came from.
        string actualBody = """{"items":[{"name":"one"},{"name":"two"}]}""";
        var diff = ResponseDiff.Compare(
            Snap(body: """{"items":[{"name":"one"},{"name":"ONE"}]}"""),
            Snap(body: actualBody));

        var d = Assert.Single(diff.Body);
        Assert.Equal("items[1].name", d.Path);

        var root = System.Text.Json.JsonDocument.Parse(actualBody).RootElement;
        Assert.Equal("two", JsonPath.Evaluate(root, d.Path)!.Value.GetString());
        Assert.Equal("\"two\"", d.After);
    }

    [Fact]
    public void A_huge_added_value_is_truncated_rather_than_reproduced()
    {
        string big = new string('x', 5000);
        var diff = ResponseDiff.Compare(
            Snap(body: """{"a":1}"""),
            Snap(body: $$"""{"a":1,"blob":"{{big}}"}"""));

        var d = Assert.Single(diff.Body);
        Assert.Equal(201, d.After!.Length);          // 200 characters plus the ellipsis
        Assert.EndsWith("…", d.After);
    }

    [Fact]
    public void An_exact_ignore_path_suppresses_only_that_value()
    {
        var baseline = Snap(body: """{"data":{"id":"a1","name":"Ada"}}""");
        var actual = Snap(body: """{"data":{"id":"b2","name":"Grace"}}""");

        var diff = ResponseDiff.Compare(baseline, actual, new DiffOptions { IgnorePaths = new[] { "data.id" } });

        Assert.Equal("data.name", Assert.Single(diff.Body).Path);
    }

    [Fact]
    public void Ignoring_the_only_difference_leaves_the_responses_identical()
    {
        var diff = ResponseDiff.Compare(
            Snap(body: """{"requestId":"one"}"""),
            Snap(body: """{"requestId":"two"}"""),
            new DiffOptions { IgnorePaths = new[] { "requestId" } });

        Assert.True(diff.Identical);
        Assert.Empty(diff.Body);
    }

    [Fact]
    public void A_trailing_star_ignores_a_prefix_but_not_its_siblings()
    {
        var baseline = Snap(body: """{"data":{"tokenId":"t1","token":"x","other":"keep"}}""");
        var actual = Snap(body: """{"data":{"tokenId":"t2","token":"y","other":"changed"}}""");

        var diff = ResponseDiff.Compare(baseline, actual, new DiffOptions { IgnorePaths = new[] { "data.token*" } });

        Assert.Equal("data.other", Assert.Single(diff.Body).Path);
    }

    [Fact]
    public void Ignoring_a_container_suppresses_everything_under_it()
    {
        var baseline = Snap(body: """{"data":{"a":1,"deep":{"b":2}},"top":"same"}""");
        var actual = Snap(body: """{"data":{"a":9,"deep":{"b":8},"added":true},"top":"same"}""");

        var diff = ResponseDiff.Compare(baseline, actual, new DiffOptions { IgnorePaths = new[] { "data" } });

        Assert.Empty(diff.Body);
    }

    [Fact]
    public void An_indexed_ignore_path_suppresses_exactly_that_element()
    {
        var baseline = Snap(body: """{"items":[{"id":1},{"id":2}]}""");
        var actual = Snap(body: """{"items":[{"id":7},{"id":8}]}""");

        var diff = ResponseDiff.Compare(baseline, actual, new DiffOptions { IgnorePaths = new[] { "items[0].id" } });

        Assert.Equal("items[1].id", Assert.Single(diff.Body).Path);
    }

    [Fact]
    public void Non_json_bodies_fall_back_to_a_line_and_byte_summary()
    {
        var baseline = Snap(body: "<html>\n<body>hi</body>\n</html>", contentType: "text/html");
        var actual = Snap(body: "<html>\n</html>", contentType: "text/html");

        var diff = ResponseDiff.Compare(baseline, actual);

        var d = Assert.Single(diff.Body);
        Assert.Equal("", d.Path);
        Assert.Equal(DiffKind.Changed, d.Kind);
        Assert.Equal("3 lines, 30 bytes", d.Before);
        Assert.Equal("2 lines, 14 bytes", d.After);
    }

    [Fact]
    public void Identical_non_json_bodies_are_not_a_difference()
    {
        var diff = ResponseDiff.Compare(
            Snap(body: "<html>ok</html>", contentType: "text/html"),
            Snap(body: "<html>ok</html>", contentType: "text/html"));

        Assert.True(diff.Identical);
    }

    [Fact]
    public void An_empty_body_reads_as_zero_lines()
    {
        var diff = ResponseDiff.Compare(
            Snap(body: "", contentType: null),
            Snap(body: "<p>now there is content</p>", contentType: "text/html"));

        var d = Assert.Single(diff.Body);
        Assert.Equal("0 lines, 0 bytes", d.Before);
        Assert.Equal("1 lines, 27 bytes", d.After);
    }

    [Fact]
    public void Two_empty_bodies_are_identical()
    {
        Assert.True(ResponseDiff.Compare(Snap(body: ""), Snap(body: "")).Identical);
    }

    [Fact]
    public void Json_against_a_non_json_error_page_falls_back_to_text()
    {
        var diff = ResponseDiff.Compare(
            Snap(body: """{"ok":true}"""),
            Snap(body: "<html>502 Bad Gateway</html>", contentType: "text/html"));

        var d = Assert.Single(diff.Body);
        Assert.Equal("", d.Path);
    }

    [Fact]
    public void Malformed_json_on_both_sides_does_not_throw()
    {
        var diff = ResponseDiff.Compare(Snap(body: "{bad json"), Snap(body: "{worse"));

        Assert.Single(diff.Body);
        Assert.False(diff.Identical);
    }

    [Fact]
    public void Binary_bodies_of_different_sizes_report_only_their_sizes()
    {
        var diff = ResponseDiff.Compare(
            Bytes(new byte[] { 0x00, 0x01, 0x02 }),
            Bytes(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 }));

        var d = Assert.Single(diff.Body);
        Assert.Equal("", d.Path);
        Assert.Equal("3 bytes", d.Before);
        Assert.Equal("5 bytes", d.After);
    }

    [Fact]
    public void Binary_bodies_of_the_same_size_report_that_the_content_differs()
    {
        var diff = ResponseDiff.Compare(
            Bytes(new byte[] { 0x00, 0x01, 0x02, 0x03 }),
            Bytes(new byte[] { 0x00, 0x01, 0x02, 0xFF }));

        var d = Assert.Single(diff.Body);
        Assert.Equal("4 bytes", d.Before);
        Assert.Equal("4 bytes, content differs", d.After);
    }

    [Fact]
    public void Byte_identical_binary_bodies_are_identical()
    {
        var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x0D };
        Assert.True(ResponseDiff.Compare(Bytes(payload), Bytes((byte[])payload.Clone())).Identical);
    }

    [Fact]
    public void Invalid_utf8_counts_as_binary_rather_than_text()
    {
        // No NUL byte here — the bytes are simply not decodable, which is the other half of the
        // rule the CLI already uses to decide a body is binary.
        var diff = ResponseDiff.Compare(
            Bytes(new byte[] { 0xFF, 0xFE, 0x41, 0x42 }),
            Bytes(new byte[] { 0xFF, 0xFE, 0x43, 0x44 }));

        Assert.Equal("4 bytes, content differs", Assert.Single(diff.Body).After);
    }

    [Fact]
    public void Format_of_an_identical_pair_says_so_in_one_line()
    {
        var diff = ResponseDiff.Compare(Snap(200, """{"a":1}"""), Snap(200, """{"a":1}"""));

        Assert.Equal("no differences", DiffText.Format(diff));
    }

    [Fact]
    public void Format_renders_status_headers_and_body_in_that_order()
    {
        var baseline = Snap(200, """{"data":{"name":"Ada","legacy":true}}""",
            headers: ("X-Gone", "was"));
        var actual = Snap(503, """{"data":{"name":"Grace"}}""",
            headers: ("X-New", "yes"));

        string text = DiffText.Format(ResponseDiff.Compare(baseline, actual));

        Assert.Equal(string.Join("\n",
            "status: 200 -> 503",
            "header X-Gone: was -> (absent)",
            "header X-New: (absent) -> yes",
            "body data.name changed: \"Ada\" -> \"Grace\"",
            "body data.legacy removed: true -> (absent)"), text);
    }

    [Fact]
    public void Format_names_the_whole_body_when_the_diff_has_no_path()
    {
        var diff = ResponseDiff.Compare(
            Snap(200, "<html>a</html>", "text/html"),
            Snap(200, "<html>bb</html>", "text/html"));

        Assert.Equal("body (body) changed: 1 lines, 14 bytes -> 1 lines, 15 bytes", DiffText.Format(diff));
    }

    [Fact]
    public void Format_spells_a_type_change_as_two_words()
    {
        var diff = ResponseDiff.Compare(Snap(200, """{"id":"1"}"""), Snap(200, """{"id":1}"""));

        Assert.Equal("body id type changed: \"1\" -> 1", DiffText.Format(diff));
    }

    [Fact]
    public void Format_marks_an_added_value_as_added()
    {
        var diff = ResponseDiff.Compare(Snap(200, """{"a":1}"""), Snap(200, """{"a":1,"b":2}"""));

        Assert.Equal("body b added: (absent) -> 2", DiffText.Format(diff));
    }

    [Fact]
    public void Format_does_not_end_with_a_newline()
    {
        string text = DiffText.Format(ResponseDiff.Compare(Snap(200), Snap(404)));

        Assert.Equal("status: 200 -> 404", text);
    }

    [Fact]
    public void A_saved_request_keeps_the_snapshot_of_a_successful_send()
    {
        var node = new CollectionNode { Name = "ping" };
        var snapshot = Snap(200, """{"ok":true}""");

        node.RecordResult(200, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), snapshot);

        Assert.Same(snapshot, node.KnownGood);
        Assert.Equal(200, node.LastStatusCode);
        Assert.True(node.IsKnownGood);
    }

    [Fact]
    public void A_saved_request_does_not_keep_the_snapshot_of_a_failed_send()
    {
        var node = new CollectionNode { Name = "ping" };

        node.RecordResult(500, DateTime.UtcNow, Snap(500, """{"error":"boom"}"""));

        Assert.Null(node.KnownGood);
        Assert.Equal(500, node.LastStatusCode);
    }

    [Fact]
    public void A_later_failure_does_not_erase_a_good_baseline()
    {
        var node = new CollectionNode { Name = "ping" };
        var good = Snap(200, """{"ok":true}""");
        node.RecordResult(200, DateTime.UtcNow, good);

        node.RecordResult(500, DateTime.UtcNow, Snap(500, """{"error":"boom"}"""));
        node.RecordResult(null, DateTime.UtcNow);            // transport failure, no snapshot at all

        Assert.Same(good, node.KnownGood);
        Assert.Null(node.LastStatusCode);                     // the *status* still tracks the last send
    }

    [Fact]
    public void Recording_a_result_without_a_snapshot_still_updates_the_status_fields()
    {
        var node = new CollectionNode { Name = "ping" };
        var when = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

        node.RecordResult(204, when);

        Assert.Equal(204, node.LastStatusCode);
        Assert.Equal(when, node.LastCheckedUtc);
        Assert.Null(node.KnownGood);
    }

    [Fact]
    public void Recording_a_result_notifies_that_the_baseline_may_have_moved()
    {
        var node = new CollectionNode { Name = "ping" };
        var notified = new List<string?>();
        node.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        node.RecordResult(200, DateTime.UtcNow, Snap(200, """{"ok":true}"""));

        Assert.Contains(nameof(CollectionNode.KnownGood), notified);
    }

    [Fact]
    public void Snapshot_from_a_transport_failure_has_status_zero()
    {
        var failed = new ApiResponse { Error = new ApiError(ApiErrorKind.Network, "down") };

        var snapshot = ResponseSnapshot.From(failed);

        Assert.Equal(0, snapshot.StatusCode);
        Assert.Empty(snapshot.Body);
        Assert.Null(snapshot.ContentType);
    }

    [Fact]
    public void A_har_entry_and_a_live_response_for_the_same_exchange_snapshot_the_same_way()
    {
        string body = """{"data":{"id":1}}""";
        var live = ResponseSnapshot.From(new ApiResponse
        {
            StatusCode = 200,
            Headers = new[]
            {
                new KeyValuePair<string, string>("Content-Type", "application/json"),
                new KeyValuePair<string, string>("X-Trace", "abc")
            },
            Body = Encoding.UTF8.GetBytes(body),
            ContentType = "application/json"
        });

        var recorded = ResponseSnapshot.From(new HarEntry
        {
            Response = new HarResponse
            {
                Status = 200,
                Headers =
                {
                    new HarNameValue("Content-Type", "application/json"),
                    new HarNameValue("X-Trace", "abc")
                },
                Content = new HarContent { MimeType = "application/json", Text = body, Size = body.Length }
            }
        });

        Assert.True(ResponseDiff.Compare(recorded, live).Identical);
        Assert.Equal(live.ContentType, recorded.ContentType);
    }

    [Fact]
    public void A_base64_har_body_decodes_to_the_bytes_the_server_sent()
    {
        var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x1A };
        var recorded = ResponseSnapshot.From(new HarEntry
        {
            Response = new HarResponse
            {
                Status = 200,
                Content = new HarContent
                {
                    MimeType = "image/png",
                    Encoding = "base64",
                    Text = Convert.ToBase64String(payload)
                }
            }
        });

        Assert.Equal(payload, recorded.Body);
        Assert.True(ResponseDiff.Compare(recorded, Bytes(payload)).Body.Count == 0);
    }

    [Fact]
    public void A_malformed_base64_har_body_falls_back_to_its_text_instead_of_throwing()
    {
        var recorded = ResponseSnapshot.From(new HarEntry
        {
            Response = new HarResponse
            {
                Status = 200,
                Content = new HarContent { MimeType = "image/png", Encoding = "BASE64", Text = "not!base64" }
            }
        });

        Assert.Equal("not!base64", Encoding.UTF8.GetString(recorded.Body));
    }

    [Fact]
    public void A_har_entry_with_no_content_snapshots_an_empty_body_and_no_content_type()
    {
        var recorded = ResponseSnapshot.From(new HarEntry { Response = new HarResponse { Status = 204 } });

        Assert.Equal(204, recorded.StatusCode);
        Assert.Empty(recorded.Body);
        Assert.Null(recorded.ContentType);
    }

    [Fact]
    public void A_snapshot_survives_a_json_round_trip()
    {
        var original = ResponseSnapshot.From(new ApiResponse
        {
            StatusCode = 200,
            Headers = new[] { new KeyValuePair<string, string>("Content-Type", "application/json") },
            Body = new byte[] { 0x00, 0x7B, 0x7D },
            ContentType = "application/json"
        });

        string json = System.Text.Json.JsonSerializer.Serialize(original);
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<ResponseSnapshot>(json)!;

        Assert.Equal(original.StatusCode, reloaded.StatusCode);
        Assert.Equal(original.ContentType, reloaded.ContentType);
        Assert.Equal(original.Body, reloaded.Body);
        Assert.Equal(
            original.Headers.Select(h => $"{h.Key}={h.Value}"),
            reloaded.Headers.Select(h => $"{h.Key}={h.Value}"));
        Assert.True(ResponseDiff.Compare(original, reloaded).Identical);
    }

    [Fact]
    public void Snapshot_from_a_response_keeps_status_headers_body_and_content_type()
    {
        var response = new ApiResponse
        {
            StatusCode = 201,
            Headers = new[] { new KeyValuePair<string, string>("X-A", "1") },
            Body = Encoding.UTF8.GetBytes("hi"),
            ContentType = "text/plain"
        };

        var snapshot = ResponseSnapshot.From(response);

        Assert.Equal(201, snapshot.StatusCode);
        Assert.Equal("X-A", Assert.Single(snapshot.Headers).Key);
        Assert.Equal("hi", Encoding.UTF8.GetString(snapshot.Body));
        Assert.Equal("text/plain", snapshot.ContentType);
    }
}
