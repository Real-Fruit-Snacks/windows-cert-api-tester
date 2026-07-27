using ApiTester.App;
using ApiTester.Core;

namespace ApiTester.Tests;

public class TransportDiagnosticsTests
{
    [Fact]
    public void Format_says_no_details_when_the_connection_is_unknown()
    {
        Assert.Equal("No connection details available.", TransportDiagnostics.Format(new ApiResponse()));
    }

    [Fact]
    public void Format_names_the_proxy_the_request_went_through()
    {
        var text = TransportDiagnostics.Format(new ApiResponse
        {
            Connection = new ConnectionInfo { ViaProxy = true, ProxyUri = "http://corp-proxy:8080" }
        });

        Assert.Contains("Via proxy       : yes", text);
        Assert.Contains("Proxy           : http://corp-proxy:8080", text);
    }

    [Fact]
    public void Format_has_no_proxy_line_on_a_direct_connection()
    {
        var text = TransportDiagnostics.Format(new ApiResponse
        {
            Connection = new ConnectionInfo { ViaProxy = false }
        });

        Assert.Contains("Via proxy       : no", text);
        Assert.DoesNotContain("Proxy           :", text);
    }

    [Fact]
    public void Format_names_the_rule_that_bypassed_the_proxy()
    {
        var text = TransportDiagnostics.Format(new ApiResponse
        {
            Connection = new ConnectionInfo
            {
                ViaProxy = false,
                ProxyDisposition = ProxyDisposition.BypassedByRule,
                ProxyBypassRule = ".corp"
            }
        });

        Assert.Contains("Via proxy       : no", text);
        Assert.Contains("Bypassed by     : .corp (--noproxy)", text);
    }

    [Fact]
    public void Format_has_no_bypass_line_when_the_request_went_through_a_proxy()
    {
        var text = TransportDiagnostics.Format(new ApiResponse
        {
            Connection = new ConnectionInfo
            {
                ViaProxy = true,
                ProxyUri = "http://corp-proxy:8080",
                ProxyDisposition = ProxyDisposition.Proxied
            }
        });

        Assert.DoesNotContain("Bypassed by", text);
    }

    [Fact]
    public void Format_has_no_bypass_line_on_a_plain_direct_connection()
    {
        var text = TransportDiagnostics.Format(new ApiResponse
        {
            Connection = new ConnectionInfo { ViaProxy = false }
        });

        Assert.DoesNotContain("Bypassed by", text);
    }

    [Fact]
    public void Format_lists_every_redirect_hop_with_its_dropped_authorization_and_scheme_downgrade()
    {
        var text = TransportDiagnostics.Format(new ApiResponse
        {
            Connection = new ConnectionInfo(),
            Redirects = new[]
            {
                new RedirectHop(301, "https://a.example/one", "https://b.example/two", true, false),
                new RedirectHop(302, "https://b.example/two", "http://b.example/three", false, true)
            }
        });

        var lines = Lines(text);
        int at = lines.IndexOf("REDIRECTS");
        Assert.True(at >= 0, "the redirect block is missing:\n" + text);

        var first = lines[at + 1];
        Assert.Contains("301", first);
        Assert.Contains("https://a.example/one", first);
        Assert.Contains("https://b.example/two", first);
        Assert.Contains("authorization dropped", first);
        Assert.DoesNotContain("scheme downgrade", first);

        var second = lines[at + 2];
        Assert.Contains("302", second);
        Assert.Contains("http://b.example/three", second);
        Assert.Contains("scheme downgrade", second);
        Assert.DoesNotContain("authorization dropped", second);
    }

    [Fact]
    public void Format_renders_the_error_kind_message_socket_code_and_every_detail_link()
    {
        var text = TransportDiagnostics.Format(new ApiResponse
        {
            Connection = new ConnectionInfo(),
            Error = new ApiError(
                ApiErrorKind.CertificateRefused,
                "The SSL connection could not be established.",
                new[]
                {
                    new ErrorDetail("System.Security.Authentication.AuthenticationException",
                        "Authentication failed because the remote party closed the transport stream.", null),
                    new ErrorDetail("System.ComponentModel.Win32Exception",
                        "The message received was unexpected or badly formatted", -2146893018)
                },
                System.Net.Sockets.SocketError.ConnectionReset)
        });

        var lines = Lines(text);
        int at = lines.IndexOf("ERROR");
        Assert.True(at >= 0, "the error block is missing:\n" + text);

        var block = string.Join("\n", lines.Skip(at));
        Assert.Contains("CertificateRefused", block);
        Assert.Contains("The SSL connection could not be established.", block);
        Assert.Contains("ConnectionReset", block);
        Assert.Contains("AuthenticationException", block);
        Assert.Contains("Authentication failed because the remote party closed the transport stream.", block);
        Assert.Contains("Win32Exception", block);
        Assert.Contains("The message received was unexpected or badly formatted", block);
        Assert.Contains("-2146893018", block);
    }

    [Fact]
    public void Format_still_reports_the_error_when_the_connection_never_got_far_enough_to_describe()
    {
        var text = TransportDiagnostics.Format(new ApiResponse
        {
            Error = new ApiError(
                ApiErrorKind.NameResolution,
                "No such host is known. (api.example:443)",
                new[] { new ErrorDetail("System.Net.Sockets.SocketException", "No such host is known.", 11001) },
                System.Net.Sockets.SocketError.HostNotFound)
        });

        Assert.Contains("NameResolution", text);
        Assert.Contains("No such host is known. (api.example:443)", text);
        Assert.Contains("HostNotFound", text);
        Assert.Contains("11001", text);
    }

    [Fact]
    public void Format_of_an_ordinary_response_is_the_connection_and_certificate_blocks_alone()
    {
        var text = TransportDiagnostics.Format(new ApiResponse
        {
            Connection = new ConnectionInfo
            {
                TlsProtocol = "Tls13",
                CipherSuite = "TLS_AES_256_GCM_SHA384",
                ClientCertificateSubject = "CN=me",
                ClientCertificateSent = true,
                ServerCertificateSubject = "CN=api.example",
                ServerCertificateIssuer = "CN=Example CA",
                ServerCertificateThumbprint = "ABCD1234",
                ServerCertificateNotAfter = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ServerCertificateChain = new[] { "CN=Example CA", "CN=Example Root" }
            }
        });

        // The stored history entries hold this text, so it is pinned exactly.
        Assert.Equal(string.Join(Environment.NewLine, new[]
        {
            "CONNECTION",
            "  Via proxy       : no",
            "  TLS protocol    : Tls13",
            "  Cipher suite    : TLS_AES_256_GCM_SHA384",
            "",
            "CLIENT CERTIFICATE",
            "  Offered         : CN=me",
            "  Presented       : yes — presented to the server",
            "",
            "SERVER CERTIFICATE",
            "  Subject         : CN=api.example",
            "  Issuer          : CN=Example CA",
            "  Thumbprint      : ABCD1234",
            "  Expires         : 2030-01-02 03:04:05Z",
            "  Chain           :",
            "    • CN=Example CA",
            "    • CN=Example Root",
            ""
        }), text);

        Assert.DoesNotContain("REDIRECTS", text);
        Assert.DoesNotContain("ERROR", text);
    }

    [Fact]
    public void RedirectEntries_traces_one_row_per_hop_oldest_first()
    {
        var request = new ApiRequest
        {
            Method = System.Net.Http.HttpMethod.Get,
            Url = "https://a.example/one"
        };
        var response = new ApiResponse
        {
            Redirects = new[]
            {
                new RedirectHop(301, "https://a.example/one", "https://b.example/two", true, false),
                new RedirectHop(302, "https://b.example/two", "http://b.example/three", false, true)
            }
        };

        var entries = TransportDiagnostics.RedirectEntries(request, response);

        Assert.Equal(2, entries.Count);

        Assert.Equal("https://a.example/one", entries[0].Url);
        Assert.Equal(301, entries[0].StatusCode);
        Assert.Equal("GET", entries[0].Method);
        Assert.Equal("Request", entries[0].Source);
        Assert.Contains("https://b.example/two", entries[0].ReasonPhrase);
        Assert.Contains("authorization dropped", entries[0].ReasonPhrase);

        Assert.Equal("https://b.example/two", entries[1].Url);
        Assert.Equal(302, entries[1].StatusCode);
        Assert.Contains("http://b.example/three", entries[1].ReasonPhrase);
        Assert.Contains("scheme downgrade", entries[1].ReasonPhrase);
    }

    [Fact]
    public void RedirectEntries_is_empty_when_the_first_response_was_final()
    {
        var request = new ApiRequest { Method = System.Net.Http.HttpMethod.Get, Url = "https://a.example/one" };

        Assert.Empty(TransportDiagnostics.RedirectEntries(request, new ApiResponse { StatusCode = 200 }));
    }

    [Fact]
    public void RedirectEntries_never_puts_a_bearer_token_in_the_trace()
    {
        const string token = "eyJhbGciOiJIUzI1NiJ9.super-secret-payload.signature";
        var request = new ApiRequest
        {
            Method = System.Net.Http.HttpMethod.Get,
            Url = "https://a.example/one",
            Headers = new[]
            {
                new KeyValuePair<string, string>("Authorization", "Bearer " + token),
                new KeyValuePair<string, string>("Accept", "application/json")
            }
        };
        var response = new ApiResponse
        {
            Redirects = new[] { new RedirectHop(302, "https://a.example/one", "https://b.example/two", true, false) }
        };

        var entry = Assert.Single(TransportDiagnostics.RedirectEntries(request, response));

        var auth = entry.RequestHeaders.Single(h => h.Key == "Authorization").Value;
        Assert.DoesNotContain("super-secret-payload", auth);
        Assert.StartsWith("Bearer ", auth);
        Assert.Equal("application/json", entry.RequestHeaders.Single(h => h.Key == "Accept").Value);
    }

    private static List<string> Lines(string text) =>
        text.Replace("\r\n", "\n").Split('\n').ToList();
}
