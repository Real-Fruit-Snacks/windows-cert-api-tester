using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

public class ResponseFormatterTests
{
    private static ApiResponse Resp(string body, string? contentType) => new()
    {
        StatusCode = 200,
        Body = Encoding.UTF8.GetBytes(body),
        ContentType = contentType
    };

    [Fact]
    public void Pretty_prints_json()
    {
        var r = new ResponseFormatter().Format(Resp("{\"a\":1,\"b\":[2,3]}", "application/json"));
        Assert.Equal(BodyKind.Json, r.Kind);
        Assert.Contains("\n", r.Text);          // indented => contains newlines
        Assert.Contains("\"a\": 1", r.Text);
    }

    [Fact]
    public void Pretty_prints_xml()
    {
        var r = new ResponseFormatter().Format(Resp("<root><a>1</a><b>2</b></root>", "application/xml"));
        Assert.Equal(BodyKind.Xml, r.Kind);
        Assert.Contains("<a>1</a>", r.Text);
        Assert.Contains("\n", r.Text);
    }

    [Fact]
    public void Treats_html_as_html_text()
    {
        var r = new ResponseFormatter().Format(Resp("<html><body>hi</body></html>", "text/html"));
        Assert.Equal(BodyKind.Html, r.Kind);
        Assert.Contains("hi", r.Text);
    }

    [Fact]
    public void Sniffs_json_when_content_type_missing()
    {
        var r = new ResponseFormatter().Format(Resp("{\"x\":true}", null));
        Assert.Equal(BodyKind.Json, r.Kind);
    }

    [Fact]
    public void Sniffs_json_when_content_type_lies()
    {
        var r = new ResponseFormatter().Format(Resp("{\"x\":true}", "application/octet-stream"));
        Assert.Equal(BodyKind.Json, r.Kind);
    }

    [Fact]
    public void Falls_back_to_text_for_plain_text()
    {
        var r = new ResponseFormatter().Format(Resp("just some words", "text/plain"));
        Assert.Equal(BodyKind.Text, r.Kind);
        Assert.Equal("just some words", r.Text);
    }

    [Fact]
    public void Hex_dumps_binary_with_null_bytes()
    {
        var resp = new ApiResponse
        {
            StatusCode = 200,
            Body = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0x00 },
            ContentType = "application/octet-stream"
        };
        var r = new ResponseFormatter().Format(resp);
        Assert.Equal(BodyKind.Binary, r.Kind);
        Assert.Contains("00 01 02 ff 00", r.Text.ToLowerInvariant());
    }

    [Fact]
    public void Empty_body_is_empty_kind()
    {
        var r = new ResponseFormatter().Format(new ApiResponse { StatusCode = 204 });
        Assert.Equal(BodyKind.Empty, r.Kind);
    }

    [Fact]
    public void Json_content_type_with_xml_body_falls_through_to_xml()
    {
        // Content-Type claims JSON but the body is XML: JSON parse fails,
        // the formatter falls through to sniffing and classifies it as XML.
        var r = new ResponseFormatter().Format(Resp("<root><a>1</a></root>", "application/json"));
        Assert.Equal(BodyKind.Xml, r.Kind);
    }

    [Fact]
    public void Xml_content_type_with_json_body_falls_through_to_json()
    {
        // Content-Type claims XML but the body is JSON: XML parse fails,
        // the formatter falls through to sniffing and classifies it as JSON.
        var r = new ResponseFormatter().Format(Resp("{\"a\":1}", "application/xml"));
        Assert.Equal(BodyKind.Json, r.Kind);
    }
}
