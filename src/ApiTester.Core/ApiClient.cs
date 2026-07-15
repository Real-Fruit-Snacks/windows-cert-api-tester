using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ApiTester.Core;

public sealed class ApiClient
{
    public async Task<ApiResponse> SendAsync(
        ApiRequest request,
        X509Certificate2? clientCertificate,
        bool ignoreServerCertificateErrors = false,
        Func<X509Certificate2?, bool>? trustServerCertificate = null,
        CancellationToken cancellationToken = default)
    {
        bool serverUntrusted = false;

        var handler = new SocketsHttpHandler
        {
            // Use the machine's configured proxy — including "Automatically detect settings"
            // (WPAD) and a "Use automatic configuration script" (PAC) from Internet Options —
            // and authenticate to it with the signed-in user's Windows credentials when asked.
            DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                {
                    if (errors == SslPolicyErrors.None) return true;
                    if (ignoreServerCertificateErrors) return true;
                    if (trustServerCertificate is not null)
                    {
                        using var c = cert is null ? null : new X509Certificate2(cert);
                        if (trustServerCertificate(c)) return true;
                    }
                    serverUntrusted = true;
                    return false;
                }
            }
        };
        if (clientCertificate is not null)
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };

        using var http = new HttpClient(handler, disposeHandler: true) { Timeout = request.Timeout };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var message = new HttpRequestMessage(request.Method, request.Url);

            foreach (var header in request.Headers)
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);

            if (request.Body is not null)
            {
                message.Content = new StringContent(request.Body, Encoding.UTF8);
                if (request.ContentType is not null)
                    message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
            }

            using var response = await http.SendAsync(
                message, HttpCompletionOption.ResponseContentRead, cancellationToken);
            stopwatch.Stop();

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var headers = response.Headers
                .Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v)))
                .ToList();

            return new ApiResponse
            {
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase,
                Headers = headers,
                Body = bytes,
                ContentType = response.Content.Headers.ContentType?.ToString(),
                Elapsed = stopwatch.Elapsed
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ApiResponse
            {
                Elapsed = stopwatch.Elapsed,
                Error = new ApiError(ApiErrorKind.Timeout, "The request timed out.")
            };
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            var kind =
                serverUntrusted ? ApiErrorKind.ServerCertificateUntrusted
                : ex.InnerException is AuthenticationException ? ApiErrorKind.CertificateRefused
                : ApiErrorKind.Network;
            return new ApiResponse
            {
                Elapsed = stopwatch.Elapsed,
                Error = new ApiError(kind, ex.Message)
            };
        }
        catch (Exception ex) when (ex is UriFormatException or FormatException or InvalidOperationException)
        {
            stopwatch.Stop();
            return new ApiResponse
            {
                Elapsed = stopwatch.Elapsed,
                Error = new ApiError(ApiErrorKind.Unknown, "Invalid request: " + ex.Message)
            };
        }
    }
}
