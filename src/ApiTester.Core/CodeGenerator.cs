using System.Text;

namespace ApiTester.Core;

/// <summary>A request reduced to what a code snippet needs.</summary>
public sealed record CodeRequest(
    string Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? Body,
    bool Insecure = false,
    string? CertThumbprint = null);

/// <summary>Generates a copy-pasteable request snippet in several languages, so a request built in
/// the app can be handed to a script or a teammate.</summary>
public static class CodeGenerator
{
    public static string Generate(string language, CodeRequest r) => language.ToLowerInvariant() switch
    {
        "curl" => Curl(r),
        "powershell" or "ps" => PowerShell(r),
        "python" or "py" => Python(r),
        "csharp" or "c#" or "cs" => CSharp(r),
        _ => throw new ArgumentException($"Unknown language '{language}'. Use curl, powershell, python, or csharp.")
    };

    public static string Curl(CodeRequest r)
    {
        var sb = new StringBuilder();
        sb.Append("curl -X ").Append(r.Method).Append(" \"").Append(r.Url).Append('"');
        foreach (var h in r.Headers)
            sb.Append(" \\\n  -H \"").Append(h.Key).Append(": ").Append(h.Value.Replace("\"", "\\\"")).Append('"');
        if (r.CertThumbprint is not null)
            sb.Append(" \\\n  --cert \"").Append(r.CertThumbprint).Append("\"   # client cert from the Windows store (curl built with Schannel)");
        if (r.Insecure) sb.Append(" \\\n  -k");
        if (!string.IsNullOrEmpty(r.Body))
            sb.Append(" \\\n  --data \"").Append(r.Body.Replace("\"", "\\\"")).Append('"');
        return sb.ToString();
    }

    public static string PowerShell(CodeRequest r)
    {
        var sb = new StringBuilder();
        if (r.Headers.Count > 0)
        {
            sb.Append("$headers = @{\n");
            foreach (var h in r.Headers)
                sb.Append("  \"").Append(h.Key).Append("\" = \"").Append(h.Value.Replace("\"", "`\"")).Append("\"\n");
            sb.Append("}\n");
        }
        sb.Append("Invoke-RestMethod -Method ").Append(r.Method).Append(" -Uri \"").Append(r.Url).Append('"');
        if (r.Headers.Count > 0) sb.Append(" -Headers $headers");
        if (!string.IsNullOrEmpty(r.Body))
            sb.Append(" -Body '").Append(r.Body.Replace("'", "''")).Append('\'');
        if (r.Insecure) sb.Append(" -SkipCertificateCheck");
        if (r.CertThumbprint is not null)
            sb.Append(" `\n  -Certificate (Get-Item Cert:\\CurrentUser\\My\\").Append(r.CertThumbprint).Append(')');
        return sb.ToString();
    }

    public static string Python(CodeRequest r)
    {
        var sb = new StringBuilder();
        sb.Append("import requests\n\n");
        if (r.Headers.Count > 0)
        {
            sb.Append("headers = {\n");
            foreach (var h in r.Headers)
                sb.Append("    \"").Append(h.Key).Append("\": \"").Append(h.Value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\",\n");
            sb.Append("}\n");
        }
        sb.Append("resp = requests.request(\"").Append(r.Method).Append("\", \"").Append(r.Url).Append('"');
        if (r.Headers.Count > 0) sb.Append(", headers=headers");
        if (!string.IsNullOrEmpty(r.Body))
            sb.Append(", data=").Append(PyStr(r.Body));
        if (r.Insecure) sb.Append(", verify=False");
        if (r.CertThumbprint is not null) sb.Append(", cert=(\"client.pem\", \"client.key\")  # export your client certificate to PEM");
        sb.Append(")\nprint(resp.status_code)\nprint(resp.text)");
        return sb.ToString();
    }

    public static string CSharp(CodeRequest r)
    {
        var sb = new StringBuilder();
        sb.Append("using var client = new HttpClient();\n");
        sb.Append("var request = new HttpRequestMessage(new HttpMethod(\"").Append(r.Method).Append("\"), \"").Append(r.Url).Append("\");\n");
        foreach (var h in r.Headers)
            sb.Append("request.Headers.TryAddWithoutValidation(\"").Append(h.Key).Append("\", \"").Append(h.Value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\");\n");
        if (!string.IsNullOrEmpty(r.Body))
            sb.Append("request.Content = new StringContent(").Append(CsStr(r.Body)).Append(");\n");
        if (r.CertThumbprint is not null)
            sb.Append("// Attach a client certificate (thumbprint ").Append(r.CertThumbprint).Append(") via SocketsHttpHandler.SslOptions.ClientCertificates.\n");
        sb.Append("var response = await client.SendAsync(request);\n");
        sb.Append("Console.WriteLine((int)response.StatusCode);\n");
        sb.Append("Console.WriteLine(await response.Content.ReadAsStringAsync());");
        return sb.ToString();
    }

    private static string PyStr(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";

    private static string CsStr(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
}
