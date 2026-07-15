using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ApiTester.Core;

namespace ApiTester.App;

public partial class MainWindow : Window
{
    private readonly CertificateStoreService _certService = new();
    private readonly ApiClient _apiClient = new();
    private readonly ResponseFormatter _formatter = new();
    private readonly AppState _state = AppState.Load();
    private readonly ObservableCollection<HeaderRow> _headerRows = new();

    private IReadOnlyList<CertificateInfo> _certs = new List<CertificateInfo>();
    private List<CertOption> _allOptions = new();
    private List<CertOption> _visibleOptions = new();

    private ApiResponse? _lastResponse;
    private string _lastRawText = "";
    private CancellationTokenSource? _cts;

    private sealed record CertOption(string Label, X509Certificate2? Cert, string? Thumbprint);

    public MainWindow()
    {
        InitializeComponent();

        HeadersItems.ItemsSource = _headerRows;

        // Restore persisted window/request settings.
        if (_state.WindowWidth is > 400) Width = _state.WindowWidth.Value;
        if (_state.WindowHeight is > 300) Height = _state.WindowHeight.Value;
        if (_state.WindowLeft is { } l && _state.WindowTop is { } t && IsOnScreen(l, t))
        {
            Left = l; Top = t;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        IgnoreServerCertCheck.IsChecked = _state.IgnoreServerCertErrors;
        TimeoutBox.Text = _state.TimeoutSeconds.ToString();
        BaseUrlBox.Text = _state.LastBaseUrl ?? "";

        LoadCertificates();
        SelectCertByThumbprint(_state.LastCertThumbprint);
        RefreshSavedBases();
        RefreshHistoryList();
        ShowPrettyHint();

        PreviewKeyDown += Window_PreviewKeyDown;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyDarkTitleBar(this);
        if (_state.WindowMaximized) WindowState = WindowState.Maximized;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _state.WindowLeft = Left; _state.WindowTop = Top;
            _state.WindowWidth = Width; _state.WindowHeight = Height;
        }
        _state.WindowMaximized = WindowState == WindowState.Maximized;
        _state.LastCertThumbprint = SelectedThumbprint();
        _state.IgnoreServerCertErrors = IgnoreServerCertCheck.IsChecked == true;
        _state.TimeoutSeconds = ParseTimeout();
        _state.LastBaseUrl = string.IsNullOrWhiteSpace(BaseUrlBox.Text) ? null : BaseUrlBox.Text.Trim();
        _state.Save();
        base.OnClosing(e);
    }

    private static bool IsOnScreen(double l, double t) =>
        l >= -50 && t >= -50 &&
        l < SystemParameters.VirtualScreenWidth - 100 && t < SystemParameters.VirtualScreenHeight - 100;

    // ---------- window chrome ----------

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (MaxButton is null) return;
        bool maximized = WindowState == WindowState.Maximized;
        MaxButton.Content = maximized ? "" : ""; // restore / maximize glyph (Segoe MDL2 Assets)
        MaxButton.ToolTip = maximized ? "Restore" : "Maximize";
        RootContainer.Margin = maximized ? new Thickness(7) : new Thickness(0);
    }

    private void HistoryToggle_Click(object sender, RoutedEventArgs e) => ToggleHistory();

    private void ToggleHistory() =>
        HistoryPanel.Visibility = HistoryPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    // ---------- certificates ----------

    private void LoadCertificates()
    {
        _certs = _certService.ListClientCertificates();
        _allOptions = new List<CertOption> { new("— no certificate —", null, null) };
        foreach (var c in _certs)
        {
            var expiry = c.IsExpired() ? " [EXPIRED]" : "";
            var eku = c.HasClientAuthEku ? "" : " (no client-auth EKU)";
            _allOptions.Add(new CertOption($"{c.Subject}  —  {c.Thumbprint}{eku}{expiry}", c.Certificate, c.Thumbprint));
        }
        ApplyCertFilter();
        StatusText.Text = _certs.Count == 0
            ? "No client certificates found — you can still test APIs that don't require one."
            : $"{_certs.Count} certificate(s) available — pick one for mutual-TLS, or keep “no certificate”.";
    }

    private void ApplyCertFilter()
    {
        var f = CertFilter.Text?.Trim() ?? "";
        _visibleOptions = string.IsNullOrEmpty(f)
            ? _allOptions
            : _allOptions.Where((o, i) => i == 0 || o.Label.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        CertCombo.ItemsSource = _visibleOptions.Select(o => o.Label).ToList();
        if (CertCombo.SelectedIndex < 0 && _visibleOptions.Count > 0) CertCombo.SelectedIndex = 0;
    }

    private void CertFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_allOptions.Count == 0) return;
        ApplyCertFilter();
    }

    private void RefreshCertsButton_Click(object sender, RoutedEventArgs e) => LoadCertificates();

    private X509Certificate2? SelectedCert()
    {
        int i = CertCombo.SelectedIndex;
        return i >= 0 && i < _visibleOptions.Count ? _visibleOptions[i].Cert : null;
    }

    private string? SelectedThumbprint()
    {
        int i = CertCombo.SelectedIndex;
        return i >= 0 && i < _visibleOptions.Count ? _visibleOptions[i].Thumbprint : null;
    }

    private void SelectCertByThumbprint(string? thumbprint)
    {
        if (string.IsNullOrEmpty(thumbprint)) { CertCombo.SelectedIndex = 0; return; }
        int idx = _visibleOptions.FindIndex(o => o.Thumbprint == thumbprint);
        CertCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    // ---------- website (base URL) ----------

    private void RefreshSavedBases()
    {
        var items = new List<string> { "— saved websites —" };
        items.AddRange(_state.SavedBaseUrls);
        SavedBasesCombo.ItemsSource = items;
        SavedBasesCombo.SelectedIndex = 0;
    }

    private void SavedBasesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SavedBasesCombo.SelectedIndex > 0 && SavedBasesCombo.SelectedItem is string s)
        {
            BaseUrlBox.Text = s;
            SavedBasesCombo.SelectedIndex = 0;
        }
    }

    private void ForgetBaseButton_Click(object sender, RoutedEventArgs e)
    {
        var b = BaseUrlBox.Text.Trim();
        if (!string.IsNullOrEmpty(b) && _state.SavedBaseUrls.Remove(b))
        {
            RefreshSavedBases();
            StatusText.Text = $"Forgot website {b}.";
        }
    }

    private void SaveBaseUrl()
    {
        var b = BaseUrlBox.Text.Trim();
        if (!string.IsNullOrEmpty(b) && !_state.SavedBaseUrls.Contains(b, StringComparer.OrdinalIgnoreCase))
        {
            _state.SavedBaseUrls.Insert(0, b);
            if (_state.SavedBaseUrls.Count > 20) _state.SavedBaseUrls.RemoveRange(20, _state.SavedBaseUrls.Count - 20);
            RefreshSavedBases();
        }
    }

    private string EffectiveUrl() => UrlHelper.Combine(BaseUrlBox.Text, UrlBox.Text);

    // ---------- headers / auth ----------

    private void AddHeaderButton_Click(object sender, RoutedEventArgs e) => _headerRows.Add(new HeaderRow());

    private void RemoveHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HeaderRow row }) _headerRows.Remove(row);
    }

    private void AuthTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BearerPanel is null) return; // during init
        BearerPanel.Visibility = AuthTypeCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        BasicPanel.Visibility = AuthTypeCombo.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- send ----------

    private int ParseTimeout()
    {
        if (int.TryParse(TimeoutBox.Text, out var s) && s > 0) return Math.Min(s, 3600);
        return 100;
    }

    private List<KeyValuePair<string, string>> BuildHeaders()
    {
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var h in _headerRows)
            if (h.Enabled && !string.IsNullOrWhiteSpace(h.Name))
                headers.Add(new KeyValuePair<string, string>(h.Name.Trim(), h.Value ?? ""));

        switch (AuthTypeCombo.SelectedIndex)
        {
            case 1 when !string.IsNullOrWhiteSpace(BearerTokenBox.Text):
                headers.Add(new KeyValuePair<string, string>("Authorization", "Bearer " + BearerTokenBox.Text.Trim()));
                break;
            case 2:
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{BasicUserBox.Text}:{BasicPassBox.Text}"));
                headers.Add(new KeyValuePair<string, string>("Authorization", "Basic " + basic));
                break;
        }
        return headers;
    }

    private ApiRequest BuildRequest()
    {
        var method = ((ComboBoxItem)MethodCombo.SelectedItem).Content!.ToString()!;
        var body = string.IsNullOrEmpty(BodyBox.Text) ? null : BodyBox.Text;
        var ct = (ContentTypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
        string? contentType = body is not null && ct is not null && ct != "(none)" ? ct : null;

        return new ApiRequest
        {
            Method = new HttpMethod(method),
            Url = EffectiveUrl(),
            Headers = BuildHeaders(),
            Body = body,
            ContentType = contentType,
            Timeout = TimeSpan.FromSeconds(ParseTimeout())
        };
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => _ = SendRequestAsync();

    private async System.Threading.Tasks.Task SendRequestAsync()
    {
        if (!SendButton.IsEnabled) return;
        if (string.IsNullOrWhiteSpace(EffectiveUrl())) { StatusText.Text = "Enter a URL."; return; }
        SaveBaseUrl();

        var cert = SelectedCert();
        var request = BuildRequest();
        _cts = new CancellationTokenSource();
        SendButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusText.Text = "Sending…";
        try
        {
            var response = await _apiClient.SendAsync(
                request, cert, IgnoreServerCertCheck.IsChecked == true, cancellationToken: _cts.Token);
            RenderResponse(response);
            AddToHistory(response);
        }
        finally
        {
            SendButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ---------- response rendering ----------

    private void RenderResponse(ApiResponse response)
    {
        _lastResponse = response;
        DiagnosticsBox.Text = FormatDiagnostics(response.Connection);

        if (response.Error is not null)
        {
            _lastRawText = response.Error.Message;
            StatusText.Text = response.Error.Kind == ApiErrorKind.None
                ? response.Error.Message
                : $"Error [{response.Error.Kind}]: {response.Error.Message}";
            SetPretty(response.Error.Message, BodyKind.Text);
            RawBox.Text = response.Error.Message;
            ResponseHeadersBox.Text = "";
            return;
        }

        var formatted = _formatter.Format(response);
        SetPretty(formatted.Text, formatted.Kind);
        _lastRawText = Encoding.UTF8.GetString(response.Body);
        RawBox.Text = _lastRawText;
        ResponseHeadersBox.Text = string.Join("\n", response.Headers.Select(h => $"{h.Key}: {h.Value}"));

        var tls = response.Connection?.TlsProtocol is { } p ? $"  •  {p}" : "";
        StatusText.Text =
            $"{response.StatusCode} {response.ReasonPhrase}  •  {response.Body.Length} bytes  •  " +
            $"{response.Elapsed.TotalMilliseconds:F0} ms  •  {formatted.Kind}{tls}";
    }

    private void SetPretty(string text, BodyKind kind) =>
        PrettyRich.Document = SyntaxHighlighter.Build(text, kind);

    private static string FormatDiagnostics(ConnectionInfo? c)
    {
        if (c is null) return "No connection details available.";
        var sb = new StringBuilder();
        sb.AppendLine("CONNECTION");
        sb.AppendLine($"  Via proxy       : {(c.ViaProxy ? "yes" : "no")}");
        sb.AppendLine($"  TLS protocol    : {c.TlsProtocol ?? "—"}");
        sb.AppendLine($"  Cipher suite    : {c.CipherSuite ?? "—"}");
        sb.AppendLine();
        sb.AppendLine("CLIENT CERTIFICATE");
        sb.AppendLine($"  Offered         : {c.ClientCertificateSubject ?? "none"}");
        string presented = c.ClientCertificateSubject is null ? "n/a"
            : c.ClientCertificateSent ? "yes — presented to the server"
            : c.ViaProxy ? "unknown (connection went through a proxy)"
            : "no — the server did not request a client certificate";
        sb.AppendLine($"  Presented       : {presented}");
        sb.AppendLine();
        sb.AppendLine("SERVER CERTIFICATE");
        sb.AppendLine($"  Subject         : {c.ServerCertificateSubject ?? "—"}");
        sb.AppendLine($"  Issuer          : {c.ServerCertificateIssuer ?? "—"}");
        sb.AppendLine($"  Thumbprint      : {c.ServerCertificateThumbprint ?? "—"}");
        sb.AppendLine($"  Expires         : {c.ServerCertificateNotAfter?.ToString("u") ?? "—"}");
        if (c.ServerCertificateChain.Count > 0)
        {
            sb.AppendLine("  Chain           :");
            foreach (var s in c.ServerCertificateChain) sb.AppendLine($"    • {s}");
        }
        return sb.ToString();
    }

    // ---------- copy / save ----------

    private void CopyBodyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastRawText)) { StatusText.Text = "Nothing to copy."; return; }
        TrySetClipboard(_lastRawText, "Copied response body.");
    }

    private void CopyCurlButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(EffectiveUrl())) { StatusText.Text = "Enter a URL first."; return; }
        TrySetClipboard(BuildCurl(), "Copied cURL command.");
    }

    private void TrySetClipboard(string text, string ok)
    {
        try { Clipboard.SetText(text); StatusText.Text = ok; }
        catch (Exception ex) { StatusText.Text = "Copy failed: " + ex.Message; }
    }

    private string BuildCurl()
    {
        var method = ((ComboBoxItem)MethodCombo.SelectedItem).Content!.ToString()!;
        var sb = new StringBuilder();
        sb.Append("curl -X ").Append(method).Append(" \"").Append(EffectiveUrl()).Append('"');
        foreach (var h in BuildHeaders())
            sb.Append(" \\\n  -H \"").Append(h.Key).Append(": ").Append(h.Value.Replace("\"", "\\\"")).Append('"');
        var thumb = SelectedThumbprint();
        if (thumb is not null)
            sb.Append(" \\\n  --cert \"").Append(thumb).Append("\"   # client cert from the Windows store (curl built with Schannel)");
        if (IgnoreServerCertCheck.IsChecked == true)
            sb.Append(" \\\n  -k");
        if (!string.IsNullOrEmpty(BodyBox.Text))
            sb.Append(" \\\n  --data \"").Append(BodyBox.Text.Replace("\"", "\\\"")).Append('"');
        return sb.ToString();
    }

    private void SaveResponseButton_Click(object sender, RoutedEventArgs e) => SaveResponse();

    private void SaveResponse()
    {
        if (_lastResponse is null || _lastResponse.Body.Length == 0)
        {
            StatusText.Text = "No response body to save.";
            return;
        }
        var ext = ExtForContentType(_lastResponse.ContentType);
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "response" + ext, DefaultExt = ext };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                System.IO.File.WriteAllBytes(dialog.FileName, _lastResponse.Body);
                StatusText.Text = $"Saved {_lastResponse.Body.Length} bytes to {dialog.FileName}.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Save failed: " + ex.Message;
            }
        }
    }

    private static string ExtForContentType(string? ct)
    {
        ct = ct?.ToLowerInvariant() ?? "";
        if (ct.Contains("json")) return ".json";
        if (ct.Contains("xml")) return ".xml";
        if (ct.Contains("html")) return ".html";
        if (ct.Contains("png")) return ".png";
        if (ct.Contains("jpeg") || ct.Contains("jpg")) return ".jpg";
        if (ct.Contains("gif")) return ".gif";
        if (ct.Contains("pdf")) return ".pdf";
        if (ct.StartsWith("text/")) return ".txt";
        return ".bin";
    }

    // ---------- history ----------

    private const int MaxStoredBody = 256 * 1024;

    private void AddToHistory(ApiResponse response)
    {
        var entry = new HistoryEntry
        {
            Method = ((ComboBoxItem)MethodCombo.SelectedItem).Content!.ToString()!,
            BaseUrl = string.IsNullOrWhiteSpace(BaseUrlBox.Text) ? null : BaseUrlBox.Text.Trim(),
            Url = UrlBox.Text.Trim(),
            Headers = _headerRows.Select(h => new HeaderRow { Enabled = h.Enabled, Name = h.Name, Value = h.Value }).ToList(),
            Body = string.IsNullOrEmpty(BodyBox.Text) ? null : BodyBox.Text,
            ContentType = (ContentTypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "application/json",
            AuthType = AuthTypeCombo.SelectedIndex switch { 1 => "Bearer", 2 => "Basic", _ => "None" },
            AuthUser = BasicUserBox.Text,
            AuthSecret = AuthTypeCombo.SelectedIndex == 1 ? BearerTokenBox.Text : BasicPassBox.Text,
            CertThumbprint = SelectedThumbprint(),
            IgnoreServerCert = IgnoreServerCertCheck.IsChecked == true,
            TimeoutSeconds = ParseTimeout(),
            StatusCode = response.StatusCode,
            Response = BuildSnapshot(response)
        };
        _state.History.RemoveAll(h => h.Method == entry.Method && h.EffectiveUrl == entry.EffectiveUrl);
        _state.History.Insert(0, entry);
        if (_state.History.Count > 30) _state.History.RemoveRange(30, _state.History.Count - 30);
        RefreshHistoryList();
    }

    private ResponseSnapshot BuildSnapshot(ApiResponse r)
    {
        var body = r.Body;
        bool truncated = body.Length > MaxStoredBody;
        return new ResponseSnapshot
        {
            StatusCode = r.StatusCode,
            ReasonPhrase = r.ReasonPhrase,
            ElapsedMs = r.Elapsed.TotalMilliseconds,
            ContentType = r.ContentType,
            Body = truncated ? Array.Empty<byte>() : body,
            BodyTruncated = truncated,
            Headers = r.Headers.Select(h => new HeaderRow { Name = h.Key, Value = h.Value }).ToList(),
            Diagnostics = FormatDiagnostics(r.Connection),
            ErrorKind = r.Error?.Kind.ToString(),
            ErrorMessage = r.Error?.Message
        };
    }

    private void RefreshHistoryList()
    {
        HistoryList.SelectionChanged -= HistoryList_SelectionChanged;
        HistoryList.ItemsSource = null;
        HistoryList.ItemsSource = _state.History;
        HistoryList.SelectionChanged += HistoryList_SelectionChanged;
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _state.History.Clear();
        RefreshHistoryList();
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryEntry entry) LoadEntry(entry);
    }

    private void LoadEntry(HistoryEntry entry)
    {
        // Fully replace the current request with the stored one.
        BaseUrlBox.Text = entry.BaseUrl ?? "";
        SelectMethod(entry.Method);
        UrlBox.Text = entry.Url;

        _headerRows.Clear();
        foreach (var h in entry.Headers)
            _headerRows.Add(new HeaderRow { Enabled = h.Enabled, Name = h.Name, Value = h.Value });

        BodyBox.Text = entry.Body ?? "";
        SelectContentType(entry.ContentType);

        AuthTypeCombo.SelectedIndex = entry.AuthType switch { "Bearer" => 1, "Basic" => 2, _ => 0 };
        BearerTokenBox.Text = entry.AuthType == "Bearer" ? entry.AuthSecret ?? "" : "";
        BasicUserBox.Text = entry.AuthUser ?? "";
        BasicPassBox.Text = entry.AuthType == "Basic" ? entry.AuthSecret ?? "" : "";

        IgnoreServerCertCheck.IsChecked = entry.IgnoreServerCert;
        TimeoutBox.Text = entry.TimeoutSeconds.ToString();
        SelectCertByThumbprint(entry.CertThumbprint);

        // Restore that request's own response.
        if (entry.Response is not null) RenderSnapshot(entry.Response);
        else ClearResponse();
    }

    private void RenderSnapshot(ResponseSnapshot s)
    {
        DiagnosticsBox.Text = s.Diagnostics ?? "No connection details available.";

        if (s.ErrorMessage is not null)
        {
            _lastResponse = null;
            _lastRawText = s.ErrorMessage;
            SetPretty(s.ErrorMessage, BodyKind.Text);
            RawBox.Text = s.ErrorMessage;
            ResponseHeadersBox.Text = "";
            StatusText.Text = $"Error [{s.ErrorKind}]: {s.ErrorMessage}   (from history)";
            return;
        }

        var recon = new ApiResponse
        {
            StatusCode = s.StatusCode,
            ReasonPhrase = s.ReasonPhrase,
            ContentType = s.ContentType,
            Body = s.Body,
            Headers = s.Headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToList(),
            Elapsed = TimeSpan.FromMilliseconds(s.ElapsedMs)
        };
        _lastResponse = recon;

        if (s.BodyTruncated)
        {
            SetPretty("(the response body was too large to keep in history — re-send to see it)", BodyKind.Text);
            _lastRawText = "";
        }
        else
        {
            var formatted = _formatter.Format(recon);
            SetPretty(formatted.Text, formatted.Kind);
            _lastRawText = Encoding.UTF8.GetString(s.Body);
        }
        RawBox.Text = _lastRawText;
        ResponseHeadersBox.Text = string.Join("\n", recon.Headers.Select(h => $"{h.Key}: {h.Value}"));
        StatusText.Text = $"{s.StatusCode} {s.ReasonPhrase}  •  {s.Body.Length} bytes  •  {s.ElapsedMs:F0} ms   (from history)";
    }

    private void ClearResponse()
    {
        ShowPrettyHint();
        RawBox.Text = "";
        ResponseHeadersBox.Text = "";
        DiagnosticsBox.Text = "";
        _lastResponse = null;
        _lastRawText = "";
    }

    private void ShowPrettyHint()
    {
        var run = new System.Windows.Documents.Run("The formatted response appears here after you send a request.")
        {
            Foreground = (System.Windows.Media.Brush)FindResource("Text.Faint")
        };
        PrettyRich.Document = new System.Windows.Documents.FlowDocument(new System.Windows.Documents.Paragraph(run))
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12.5
        };
    }

    private void SelectMethod(string method)
    {
        foreach (ComboBoxItem item in MethodCombo.Items)
            if ((item.Content?.ToString() ?? "") == method) { MethodCombo.SelectedItem = item; return; }
    }

    private void SelectContentType(string contentType)
    {
        foreach (ComboBoxItem item in ContentTypeCombo.Items)
            if ((item.Content?.ToString() ?? "") == contentType) { ContentTypeCombo.SelectedItem = item; return; }
    }

    // ---------- keyboard ----------

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = SendRequestAsync();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (ctrl && e.Key == Key.Enter) { e.Handled = true; _ = SendRequestAsync(); }
        else if (ctrl && e.Key == Key.L) { e.Handled = true; UrlBox.Focus(); UrlBox.SelectAll(); }
        else if (ctrl && e.Key == Key.S) { e.Handled = true; SaveResponse(); }
        else if (ctrl && e.Key == Key.H) { e.Handled = true; ToggleHistory(); }
        else if (e.Key == Key.F5) { e.Handled = true; LoadCertificates(); }
        else if (e.Key == Key.Escape && CancelButton.IsEnabled) { e.Handled = true; _cts?.Cancel(); }
    }

    // ---------- self-test ----------

    private async void SelfTestButton_Click(object sender, RoutedEventArgs e)
    {
        SelfTestButton.IsEnabled = false;
        StatusText.Text = "Running self-test…";
        try
        {
            var result = await new SelfTestRunner().RunAsync();
            StatusText.Text = (result.Passed ? "Self-test PASSED  •  " : "Self-test FAILED  •  ") + result.Detail;
            SetPretty(result.Detail, BodyKind.Text);
        }
        finally
        {
            SelfTestButton.IsEnabled = true;
        }
    }
}
