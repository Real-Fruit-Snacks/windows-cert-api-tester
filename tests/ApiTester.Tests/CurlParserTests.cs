using ApiTester.Core;

namespace ApiTester.Tests;

public class CurlParserTests
{
    [Fact]
    public void Simple_get()
    {
        var r = CurlParser.Parse("curl https://api.example.com/users");
        Assert.Equal("GET", r.Method);
        Assert.Equal("https://api.example.com/users", r.Url);
    }

    [Fact]
    public void Post_with_json_body_and_content_type()
    {
        var r = CurlParser.Parse("curl -X POST https://h/api -H \"Content-Type: application/json\" -d '{\"a\":1}'");
        Assert.Equal("POST", r.Method);
        Assert.Equal("https://h/api", r.Url);
        Assert.Equal("application/json", r.ContentType);
        Assert.Equal("{\"a\":1}", r.Body);
        Assert.Empty(r.Headers); // content-type is lifted out of headers
    }

    [Fact]
    public void Data_without_method_implies_post()
    {
        var r = CurlParser.Parse("curl https://h/f -d name=bob");
        Assert.Equal("POST", r.Method);
        Assert.Equal("name=bob", r.Body);
    }

    [Fact]
    public void Multiple_headers_are_kept()
    {
        var r = CurlParser.Parse("curl -H 'Accept: application/json' -H 'X-Key: abc' https://h");
        Assert.Equal(2, r.Headers.Count);
        Assert.Equal(new("Accept", "application/json"), r.Headers[0]);
        Assert.Equal(new("X-Key", "abc"), r.Headers[1]);
    }

    [Fact]
    public void Basic_user_is_split()
    {
        var r = CurlParser.Parse("curl -u alice:s3cret https://h");
        Assert.Equal("alice", r.BasicUser);
        Assert.Equal("s3cret", r.BasicPassword);
    }

    [Fact]
    public void Insecure_flag()
    {
        var r = CurlParser.Parse("curl -k https://self-signed.example");
        Assert.True(r.InsecureSkipVerify);
    }

    [Fact]
    public void Bearer_authorization_becomes_token()
    {
        var r = CurlParser.Parse("curl https://h -H \"Authorization: Bearer tok123\"");
        Assert.Equal("tok123", r.BearerToken);
        Assert.Empty(r.Headers);
    }

    [Fact]
    public void Line_continuations_and_long_options()
    {
        var curl = "curl --request PUT \\\n  --url https://h/thing \\\n  --header 'Accept: */*'";
        var r = CurlParser.Parse(curl);
        Assert.Equal("PUT", r.Method);
        Assert.Equal("https://h/thing", r.Url);
        Assert.Single(r.Headers);
        Assert.Equal(new("Accept", "*/*"), r.Headers[0]);
    }
}
