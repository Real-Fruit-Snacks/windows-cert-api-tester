using ApiTester.Core;

namespace ApiTester.Tests;

/// <summary>Pins the Insomnia v4 mapping table: the flat resources array rebuilt into a tree by
/// parent id (in any file order), disabled rows staying disabled, bodies and auth, environments,
/// and the two template rules — `{{ _.name }}` translated, `{% tag %}` reported rather than
/// silently dropped.</summary>
public class InsomniaImportTests
{
    [Fact]
    public void A_flat_resource_array_is_rebuilt_into_a_folder_tree_by_parent_id()
    {
        // Children deliberately precede their parents in the file: Insomnia does not guarantee
        // order, so the rebuild must not depend on it.
        var result = InsomniaImport.Parse("""
            {
              "_type": "export", "__export_format": 4,
              "resources": [
                { "_id": "req_1", "_type": "request", "parentId": "fld_1", "name": "get order",
                  "method": "GET", "url": "https://api.test/orders/1" },
                { "_id": "fld_1", "_type": "request_group", "parentId": "wrk_1", "name": "Orders" },
                { "_id": "wrk_1", "_type": "workspace", "name": "My API" },
                { "_id": "req_2", "_type": "request", "parentId": "wrk_1", "name": "health",
                  "method": "GET", "url": "https://api.test/health" }
              ]
            }
            """);

        Assert.Equal("My API", result.Root.Name);
        Assert.Empty(result.Warnings);

        var folder = result.Root.Children.Single(c => c.IsFolder);
        Assert.Equal("Orders", folder.Name);
        Assert.Equal("get order", folder.Children.Single().Name);

        var loose = result.Root.Children.Single(c => !c.IsFolder);
        Assert.Equal("health", loose.Name);
    }

    [Fact]
    public void Insomnia_template_syntax_is_translated_to_this_products()
    {
        var result = InsomniaImport.Parse("""
            {
              "_type": "export", "__export_format": 4,
              "resources": [
                { "_id": "wrk", "_type": "workspace", "name": "W" },
                { "_id": "r", "_type": "request", "parentId": "wrk", "name": "call",
                  "method": "POST", "url": "https://{{ _.host }}/v1/{{ _.tenant }}",
                  "headers": [ { "name": "Authorization", "value": "Bearer {{ _.token }}" } ],
                  "body": { "mimeType": "application/json", "text": "{\"id\":\"{{ _.id }}\"}" } }
              ]
            }
            """);

        var m = result.Root.Children.Single().Request!;
        Assert.Equal("https://{{host}}/v1/{{tenant}}", m.Path);
        Assert.Equal("Bearer {{token}}", m.Headers.Single().Value);
        Assert.Equal("{\"id\":\"{{id}}\"}", m.Body);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_tag_template_is_left_in_place_and_named_in_a_warning()
    {
        // {% uuid 'v4' %} is a program, not a value — there is nothing to translate it into, and a
        // silent drop would fail at send time instead of at import time.
        var result = InsomniaImport.Parse("""
            {
              "_type": "export", "__export_format": 4,
              "resources": [
                { "_id": "wrk", "_type": "workspace", "name": "W" },
                { "_id": "r", "_type": "request", "parentId": "wrk", "name": "generated",
                  "method": "POST", "url": "https://api.test/x",
                  "headers": [ { "name": "X-Request-Id", "value": "{% uuid 'v4' %}" } ] }
              ]
            }
            """);

        Assert.Equal("{% uuid 'v4' %}", result.Root.Children.Single().Request!.Headers.Single().Value);
        Assert.Contains(result.Warnings, w => w.Contains("generated") && w.Contains("tag template"));
    }

    [Fact]
    public void Disabled_rows_stay_disabled_and_query_moves_to_rows()
    {
        var result = InsomniaImport.Parse("""
            {
              "_type": "export", "__export_format": 4,
              "resources": [
                { "_id": "wrk", "_type": "workspace", "name": "W" },
                { "_id": "r", "_type": "request", "parentId": "wrk", "name": "list",
                  "method": "GET", "url": "https://api.test/orders?ignored=1",
                  "parameters": [ { "name": "limit", "value": "10" },
                                  { "name": "debug", "value": "1", "disabled": true } ],
                  "headers": [ { "name": "Accept", "value": "application/json" },
                               { "name": "X-Off", "value": "no", "disabled": true } ] }
              ]
            }
            """);

        var m = result.Root.Children.Single().Request!;
        Assert.Equal("https://api.test/orders", m.Path);      // query stripped; rows carry it
        Assert.True(m.QueryParams[0].Enabled);
        Assert.False(m.QueryParams[1].Enabled);
        Assert.True(m.Headers[0].Enabled);
        Assert.False(m.Headers[1].Enabled);
    }

    [Fact]
    public void Form_and_multipart_bodies_map_and_a_file_part_imports_disabled()
    {
        var result = InsomniaImport.Parse("""
            {
              "_type": "export", "__export_format": 4,
              "resources": [
                { "_id": "wrk", "_type": "workspace", "name": "W" },
                { "_id": "f", "_type": "request", "parentId": "wrk", "name": "form",
                  "method": "POST", "url": "https://api.test/f",
                  "body": { "mimeType": "application/x-www-form-urlencoded",
                            "params": [ { "name": "a", "value": "1 2" },
                                        { "name": "skip", "value": "x", "disabled": true } ] } },
                { "_id": "u", "_type": "request", "parentId": "wrk", "name": "upload",
                  "method": "POST", "url": "https://api.test/u",
                  "body": { "mimeType": "multipart/form-data",
                            "params": [ { "name": "note", "value": "hi" },
                                        { "name": "doc", "type": "file", "fileName": "C:/them/r.pdf" } ] } }
              ]
            }
            """);

        var form = result.Root.Children.Single(c => c.Name == "form").Request!;
        Assert.Equal("application/x-www-form-urlencoded", form.ContentType);
        Assert.Equal("a=1%202", form.Body);

        var upload = result.Root.Children.Single(c => c.Name == "upload").Request!;
        Assert.True(upload.IsMultipart);
        Assert.True(upload.FormParts[0].Enabled);
        Assert.False(upload.FormParts[1].Enabled);    // a foreign path never auto-sends
        Assert.True(upload.FormParts[1].IsFile);
        Assert.Contains(result.Warnings, w => w.Contains("doc"));
    }

    [Fact]
    public void Bearer_and_basic_auth_map_and_a_disabled_block_is_ignored()
    {
        var result = InsomniaImport.Parse("""
            {
              "_type": "export", "__export_format": 4,
              "resources": [
                { "_id": "wrk", "_type": "workspace", "name": "W" },
                { "_id": "b", "_type": "request", "parentId": "wrk", "name": "bearer",
                  "method": "GET", "url": "https://a/1",
                  "authentication": { "type": "bearer", "token": "{{ _.tok }}" } },
                { "_id": "s", "_type": "request", "parentId": "wrk", "name": "basic",
                  "method": "GET", "url": "https://a/2",
                  "authentication": { "type": "basic", "username": "u", "password": "p" } },
                { "_id": "d", "_type": "request", "parentId": "wrk", "name": "off",
                  "method": "GET", "url": "https://a/3",
                  "authentication": { "type": "bearer", "token": "t", "disabled": true } }
              ]
            }
            """);

        var bearer = result.Root.Children.Single(c => c.Name == "bearer").Request!;
        Assert.Equal("Bearer", bearer.AuthType);
        Assert.Equal("{{tok}}", bearer.AuthSecret);     // translated on the way through

        var basic = result.Root.Children.Single(c => c.Name == "basic").Request!;
        Assert.Equal("Basic", basic.AuthType);
        Assert.Equal("u", basic.AuthUser);

        // A switched-off block leaves the product's own default alone rather than forcing None.
        var off = result.Root.Children.Single(c => c.Name == "off").Request!;
        Assert.NotEqual("Bearer", off.AuthType);
    }

    [Fact]
    public void An_unsupported_auth_type_is_a_named_warning_and_imports_without_auth()
    {
        var result = InsomniaImport.Parse("""
            {
              "_type": "export", "__export_format": 4,
              "resources": [
                { "_id": "wrk", "_type": "workspace", "name": "W" },
                { "_id": "r", "_type": "request", "parentId": "wrk", "name": "hawk",
                  "method": "GET", "url": "https://a/x", "authentication": { "type": "hawk" } }
              ]
            }
            """);

        Assert.Equal("None", result.Root.Children.Single().Request!.AuthType);
        Assert.Contains(result.Warnings, w => w.Contains("hawk"));
    }

    [Fact]
    public void Environments_come_across_with_non_string_values_preserved_as_json()
    {
        var result = InsomniaImport.Parse("""
            {
              "_type": "export", "__export_format": 4,
              "resources": [
                { "_id": "wrk", "_type": "workspace", "name": "W" },
                { "_id": "env", "_type": "environment", "parentId": "wrk", "name": "Staging",
                  "data": { "host": "api.staging.test", "port": 8443, "flags": { "beta": true } } }
              ]
            }
            """);

        var env = Assert.Single(result.Environments);
        Assert.Equal("Staging", env.Name);
        Assert.Equal("api.staging.test", env.Variables.Single(v => v.Key == "host").Value);
        // A number or object is still the value the user set; keeping its JSON text loses nothing.
        Assert.Equal("8443", env.Variables.Single(v => v.Key == "port").Value);
        Assert.Contains("beta", env.Variables.Single(v => v.Key == "flags").Value);
    }

    [Fact]
    public void Not_an_insomnia_export_is_a_format_error_naming_what_was_expected()
    {
        var ex = Assert.Throws<FormatException>(() => InsomniaImport.Parse("""{"info":{"name":"x"},"item":[]}"""));
        Assert.Contains("Insomnia", ex.Message);

        Assert.Throws<FormatException>(() => InsomniaImport.Parse("not json at all"));
    }

    [Fact]
    public void An_export_with_no_workspace_still_imports_under_a_sensible_name()
    {
        var result = InsomniaImport.Parse("""
            {
              "_type": "export", "__export_format": 4,
              "resources": [
                { "_id": "r", "_type": "request", "name": "lonely", "method": "GET", "url": "https://a/x" }
              ]
            }
            """);

        Assert.Equal("Insomnia import", result.Root.Name);
        Assert.Equal("lonely", result.Root.Children.Single().Name);
    }
}
