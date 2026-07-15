using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows;
using ApiTester.Core;

namespace ApiTester.App;

public partial class MainWindow : Window
{
    private readonly CertificateStoreService _certService = new();
    private readonly ApiClient _apiClient = new();
    private readonly ResponseFormatter _formatter = new();
    private IReadOnlyList<CertificateInfo> _certs = new List<CertificateInfo>();
    private byte[] _lastResponseBody = System.Array.Empty<byte>();

    public MainWindow()
    {
        InitializeComponent();
        LoadCertificates();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyDarkTitleBar(this);
    }

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        bool maximized = WindowState == WindowState.Maximized;
        MaxButton.Content = maximized ? "" : ""; // restore / maximize glyph (Segoe MDL2 Assets)
        MaxButton.ToolTip = maximized ? "Restore" : "Maximize";
        // A borderless (WindowStyle=None) window clips its content under the resize
        // border when maximized; inset the content to compensate.
        RootContainer.Margin = maximized ? new Thickness(7) : new Thickness(0);
    }

    private const string NoCertOption = "— no certificate —";

    private void LoadCertificates()
    {
        _certs = _certService.ListClientCertificates();
        var items = new List<string> { NoCertOption };
        items.AddRange(_certs.Select(c =>
        {
            var expiry = c.IsExpired() ? " [EXPIRED]" : "";
            var eku = c.HasClientAuthEku ? "" : " (no client-auth EKU)";
            return $"{c.Subject}  —  {c.Thumbprint}{eku}{expiry}";
        }));
        CertCombo.ItemsSource = items;
        CertCombo.SelectedIndex = 0;
        StatusText.Text = _certs.Count == 0
            ? "No client certificates found — you can still test APIs that don't require one."
            : $"{_certs.Count} certificate(s) available — pick one for mutual-TLS, or keep “no certificate”.";
    }

    private void RefreshCertsButton_Click(object sender, RoutedEventArgs e) => LoadCertificates();

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UrlBox.Text))
        {
            StatusText.Text = "Enter a URL.";
            return;
        }

        // Index 0 is the "— no certificate —" option; real certs start at index 1.
        X509Certificate2? cert = null;
        int certIndex = CertCombo.SelectedIndex - 1;
        if (certIndex >= 0 && certIndex < _certs.Count)
            cert = _certs[certIndex].Certificate;

        var request = new ApiRequest
        {
            Method = new HttpMethod(((System.Windows.Controls.ComboBoxItem)MethodCombo.SelectedItem).Content!.ToString()!),
            Url = UrlBox.Text.Trim(),
            Headers = ParseHeaders(HeadersBox.Text),
            Body = string.IsNullOrEmpty(BodyBox.Text) ? null : BodyBox.Text
        };

        SendButton.IsEnabled = false;
        StatusText.Text = "Sending…";
        try
        {
            var response = await _apiClient.SendAsync(
                request, cert, ignoreServerCertificateErrors: IgnoreServerCertCheck.IsChecked == true);
            RenderResponse(response);
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }

    private static List<KeyValuePair<string, string>> ParseHeaders(string text)
    {
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var idx = trimmed.IndexOf(':');
            if (idx <= 0) continue;
            headers.Add(new KeyValuePair<string, string>(
                trimmed[..idx].Trim(), trimmed[(idx + 1)..].Trim()));
        }
        return headers;
    }

    private void RenderResponse(ApiResponse response)
    {
        _lastResponseBody = response.Body;

        if (response.Error is not null)
        {
            StatusText.Text = $"Error [{response.Error.Kind}]: {response.Error.Message}";
            PrettyBox.Text = response.Error.Message;
            RawBox.Text = string.Empty;
            ResponseHeadersBox.Text = string.Empty;
            return;
        }

        var formatted = _formatter.Format(response);
        PrettyBox.Text = formatted.Text;
        RawBox.Text = Encoding.UTF8.GetString(response.Body);
        ResponseHeadersBox.Text = string.Join("\n",
            response.Headers.Select(h => $"{h.Key}: {h.Value}"));
        StatusText.Text =
            $"{response.StatusCode} {response.ReasonPhrase}  •  {response.Body.Length} bytes  •  " +
            $"{response.Elapsed.TotalMilliseconds:F0} ms  •  {formatted.Kind}";
    }

    private async void SelfTestButton_Click(object sender, RoutedEventArgs e)
    {
        SelfTestButton.IsEnabled = false;
        StatusText.Text = "Running self-test…";
        try
        {
            var result = await new SelfTestRunner().RunAsync();
            StatusText.Text = (result.Passed ? "Self-test PASSED  •  " : "Self-test FAILED  •  ") + result.Detail;
            PrettyBox.Text = result.Detail;
        }
        finally
        {
            SelfTestButton.IsEnabled = true;
        }
    }

    private void SaveResponseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResponseBody.Length == 0)
        {
            StatusText.Text = "No response body to save.";
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "response.bin" };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                System.IO.File.WriteAllBytes(dialog.FileName, _lastResponseBody);
                StatusText.Text = $"Saved {_lastResponseBody.Length} bytes to {dialog.FileName}.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Save failed: {ex.Message}";
            }
        }
    }
}
