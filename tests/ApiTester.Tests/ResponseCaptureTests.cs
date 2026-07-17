using System.Text;
using ApiTester.Core;

namespace ApiTester.Tests;

public class ResponseCaptureTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoHeaders = System.Array.Empty<KeyValuePair<string, string>>();

    [Fact]
    public void Extracts_a_string_body_value()
    {
        var (value, error) = ResponseCapture.Extract(
            new CaptureSpec("token", FromHeader: false, "access_token"),
            B("""{"access_token":"abc123"}"""), "application/json", NoHeaders);
        Assert.Null(error);
        Assert.Equal("abc123", value);
    }

    [Fact]
    public void Stringifies_a_numeric_body_value()
    {
        var (value, error) = ResponseCapture.Extract(
            new CaptureSpec("id", FromHeader: false, "data.id"),
            B("""{"data":{"id":42}}"""), null, NoHeaders);
        Assert.Null(error);
        Assert.Equal("42", value);
    }

    [Fact]
    public void Reads_a_header_case_insensitively()
    {
        var headers = new[] { new KeyValuePair<string, string>("X-Session", "sess-9") };
        var (value, error) = ResponseCapture.Extract(
            new CaptureSpec("sid", FromHeader: true, "x-session"), B(""), null, headers);
        Assert.Null(error);
        Assert.Equal("sess-9", value);
    }

    [Fact]
    public void Reports_errors_without_a_value()
    {
        var (v1, e1) = ResponseCapture.Extract(new CaptureSpec("t", true, "X-Missing"), B(""), null, NoHeaders);
        Assert.Null(v1); Assert.Contains("not found", e1);

        var (v2, e2) = ResponseCapture.Extract(new CaptureSpec("t", false, "token"), B("not json"), null, NoHeaders);
        Assert.Null(v2); Assert.Contains("not JSON", e2);

        var (v3, e3) = ResponseCapture.Extract(new CaptureSpec("t", false, "obj"), B("""{"obj":{"a":1}}"""), null, NoHeaders);
        Assert.Null(v3); Assert.Contains("no string value", e3);
    }
}
