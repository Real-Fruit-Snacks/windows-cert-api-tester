using ApiTester.Core;

namespace ApiTester.Tests;

public class CodeGeneratorTests
{
    private static CodeRequest Req(string method = "POST", string? body = "{\"a\":1}", bool insecure = false) => new(
        method, "https://api.example.com/things",
        new[] { new KeyValuePair<string, string>("Authorization", "Bearer abc"), new("Accept", "application/json") },
        body, insecure);

    [Fact]
    public void Curl_has_method_url_headers_and_body()
    {
        var c = CodeGenerator.Curl(Req());
        Assert.Contains("curl -X POST", c);
        Assert.Contains("https://api.example.com/things", c);
        Assert.Contains("-H \"Authorization: Bearer abc\"", c);
        Assert.Contains("--data \"{\\\"a\\\":1}\"", c);
    }

    [Fact]
    public void Curl_insecure_adds_k()
    {
        Assert.Contains("-k", CodeGenerator.Curl(Req(insecure: true)));
        Assert.DoesNotContain("\n  -k", CodeGenerator.Curl(Req(insecure: false)));
    }

    [Fact]
    public void PowerShell_uses_invoke_restmethod_and_a_headers_hashtable()
    {
        var c = CodeGenerator.PowerShell(Req());
        Assert.Contains("Invoke-RestMethod -Method POST", c);
        Assert.Contains("$headers = @{", c);
        Assert.Contains("-Body '{\"a\":1}'", c);
        Assert.Contains("-SkipCertificateCheck", CodeGenerator.PowerShell(Req(insecure: true)));
    }

    [Fact]
    public void Python_uses_requests()
    {
        var c = CodeGenerator.Python(Req());
        Assert.Contains("import requests", c);
        Assert.Contains("requests.request(\"POST\", \"https://api.example.com/things\"", c);
        Assert.Contains("headers=headers", c);
        Assert.Contains("data=", c);
        Assert.Contains("verify=False", CodeGenerator.Python(Req(insecure: true)));
    }

    [Fact]
    public void CSharp_uses_httpclient()
    {
        var c = CodeGenerator.CSharp(Req());
        Assert.Contains("new HttpClient()", c);
        Assert.Contains("new HttpRequestMessage(new HttpMethod(\"POST\")", c);
        Assert.Contains("request.Headers.TryAddWithoutValidation(\"Accept\", \"application/json\")", c);
        Assert.Contains("new StringContent(", c);
        Assert.Contains("await client.SendAsync(request)", c);
    }

    [Fact]
    public void No_body_omits_the_body_argument()
    {
        var c = CodeGenerator.Python(Req(method: "GET", body: null));
        Assert.DoesNotContain("data=", c);
    }

    [Fact]
    public void Generate_dispatches_by_language_and_rejects_unknown()
    {
        Assert.Contains("curl", CodeGenerator.Generate("curl", Req()));
        Assert.Contains("Invoke-RestMethod", CodeGenerator.Generate("PowerShell", Req()));
        Assert.Throws<ArgumentException>(() => CodeGenerator.Generate("cobol", Req()));
    }
}
