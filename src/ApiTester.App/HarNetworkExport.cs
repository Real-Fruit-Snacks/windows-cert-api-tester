using System;
using System.Collections.Generic;
using System.Linq;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>Maps the Network trace (metadata-only <see cref="NetworkEntry"/> rows) onto a HAR
/// document. Pure — no I/O, no sockets. Where the trace kept only metadata for a call, the
/// resulting entry is an honest partial: <c>content.text</c> is empty but <c>content.size</c> is
/// the real transferred size.</summary>
public static class HarNetworkExport
{
    public static string ToHar(IEnumerable<NetworkEntry> entries, bool includeSecrets, string creatorVersion)
    {
        var mapped = entries.Select(entry => ToEntry(entry, includeSecrets));
        return HarWriter.Write(mapped, creatorVersion);
    }

    private static HarEntry ToEntry(NetworkEntry entry, bool includeSecrets)
    {
        DateTimeOffset started;
        try { started = new DateTimeOffset(entry.Timestamp); }
        catch (ArgumentOutOfRangeException) { started = DateTimeOffset.UtcNow; }

        var requestHeaders = HarWriter.Redact(
            entry.RequestHeaders.Select(h => new HarNameValue(h.Key, h.Value)), includeSecrets);
        var responseHeaders = HarWriter.Redact(
            entry.ResponseHeaders.Select(h => new HarNameValue(h.Key, h.Value)), includeSecrets);

        return new HarEntry
        {
            StartedDateTime = started,
            Time = entry.ElapsedMs,
            Request = new HarRequest
            {
                Method = entry.Method,
                Url = entry.Url,
                Headers = requestHeaders,
                QueryString = new List<HarNameValue>(),
                PostData = null
            },
            Response = new HarResponse
            {
                Status = entry.StatusCode ?? 0,
                StatusText = entry.ReasonPhrase ?? "",
                Headers = responseHeaders,
                Content = new HarContent
                {
                    Size = entry.Size,
                    MimeType = entry.ContentType ?? "",
                    Text = "",
                    Encoding = null
                },
                RedirectUrl = ""
            },
            Timings = new HarTimings { Wait = entry.ElapsedMs, Send = -1, Receive = -1 },
            Certapi = new HarCertapi
            {
                ClientCertificateSent = entry.ClientCertPresented,
                ClientCertificateSubject = entry.ClientCertSubject,
                TlsProtocol = null,
                ServerCertificateThumbprint = null,
                ViaProxy = false
            }
        };
    }
}
