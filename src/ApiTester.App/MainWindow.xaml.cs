using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ApiTester.Core;
using Microsoft.Web.WebView2.Core;

namespace ApiTester.App;

public partial class MainWindow : Window
{
    private readonly CertificateStoreService _certService = new();
    private readonly ApiClient _apiClient = new();
    private readonly ResponseFormatter _formatter = new();
    private readonly AppState _state = AppState.Load();

    private readonly ObservableCollection<RequestTab> _tabs = new();
    private RequestTab? _loadedTab;   // the tab currently loaded into the editor controls
    private bool _switching;          // guards re-entrancy during tab switches
    private bool _loading;            // true while pushing a model into the controls

    private readonly ObservableCollection<CollectionNode> _collections = new();
    private readonly ObservableCollection<ApiEnvironment> _environments = new();
    private List<string> _unresolvedVars = new();
    private bool _envLoading;

    private MtlsBrowserSession? _browserSession;
    private CancellationTokenSource? _browserCts;
    private bool _browserReady;
    private RequestTab? _renderTraceTarget;   // the tab whose Network trace a render populates
    private string? _renderCertSubject;       // the client cert the browser session offers

    private IReadOnlyList<CertificateInfo> _certs = new List<CertificateInfo>();
    private List<CertOption> _allOptions = new();
    private List<CertOption> _visibleOptions = new();

    private ApiResponse? _lastResponse;
    private string _lastRawText = "";
    private CancellationTokenSource? _cts;

    private sealed record CertOption(string Label, X509Certificate2? Cert, string? Thumbprint);

    private RequestTab? ActiveTab => TabStrip.SelectedItem as RequestTab;
    private RequestModel? ActiveRequest => ActiveTab?.Request;

    public MainWindow()
    {
        InitializeComponent();

        // Restore persisted window bounds.
        if (_state.WindowWidth is > 400) Width = _state.WindowWidth.Value;
        if (_state.WindowHeight is > 300) Height = _state.WindowHeight.Value;
        if (_state.WindowLeft is { } l && _state.WindowTop is { } t && IsOnScreen(l, t))
        {
            Left = l; Top = t;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        LoadCertificates();
        SelectCertByThumbprint(_state.LastCertThumbprint);
        RefreshSavedBases();
        RefreshHistoryList();
        InitializeTabs();
        InitializeCollections();
        InitializeEnvironments();

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
        if (ActiveRequest is { } active) CaptureControlsInto(active);

        if (WindowState == WindowState.Normal)
        {
            _state.WindowLeft = Left; _state.WindowTop = Top;
            _state.WindowWidth = Width; _state.WindowHeight = Height;
        }
        _state.WindowMaximized = WindowState == WindowState.Maximized;

        _state.Tabs = _tabs.Select(t => t.Request).ToList();
        _state.ActiveTabIndex = Math.Max(0, TabStrip.SelectedIndex);
        _state.Collections = _collections.ToList();
        _state.Environments = _environments.ToList();

        // Keep the single-value globals in step with the active tab for first-launch continuity.
        _state.LastCertThumbprint = ActiveRequest?.CertThumbprint;
        _state.IgnoreServerCertErrors = ActiveRequest?.IgnoreServerCert ?? false;
        _state.TimeoutSeconds = ActiveRequest?.TimeoutSeconds ?? 100;
        _state.LastBaseUrl = string.IsNullOrWhiteSpace(ActiveRequest?.BaseUrl) ? null : ActiveRequest!.BaseUrl!.Trim();

        _state.Save();

        _browserSession?.Dispose();
        _browserCts?.Cancel();
        _browserCts?.Dispose();

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

    private void HelpButton_Click(object sender, RoutedEventArgs e) => ShowHelp();

    private void ShowHelp()
    {
        var help = new HelpWindow { Owner = this };
        help.ShowDialog();
    }

    private void ToggleHistory() =>
        HistoryPanel.Visibility = HistoryPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    // ---------- tabs ----------

    private void InitializeTabs()
    {
        IEnumerable<RequestModel> models = _state.Tabs.Count > 0
            ? _state.Tabs
            : new[]
            {
                new RequestModel
                {
                    IgnoreServerCert = _state.IgnoreServerCertErrors,
                    TimeoutSeconds = _state.TimeoutSeconds,
                    BaseUrl = _state.LastBaseUrl ?? "",
                    CertThumbprint = _state.LastCertThumbprint
                }
            };

        foreach (var m in models) _tabs.Add(new RequestTab(m));
        TabStrip.ItemsSource = _tabs;
        TabStrip.SelectedIndex = Math.Clamp(_state.ActiveTabIndex, 0, _tabs.Count - 1);
    }

    private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switching) return;
        if (TabStrip.SelectedItem is not RequestTab newTab) return; // transient -1 during removal
        _switching = true;
        try
        {
            if (_loadedTab is { } old && !ReferenceEquals(old, newTab)) CaptureControlsInto(old.Request);
            LoadIntoControls(newTab.Request);
            _loadedTab = newTab;
            ShowTabResponse(newTab);
            BindNetwork(newTab);
        }
        finally { _switching = false; }
    }

    private void NewTabButton_Click(object sender, RoutedEventArgs e) => AddNewTab();

    private RequestTab AddNewTab()
    {
        var m = new RequestModel
        {
            // Inherit the current base URL / cert / timeout so a new tab is ready to use.
            BaseUrl = ActiveRequest?.BaseUrl,
            CertThumbprint = ActiveRequest?.CertThumbprint,
            IgnoreServerCert = ActiveRequest?.IgnoreServerCert ?? false,
            TimeoutSeconds = ActiveRequest?.TimeoutSeconds ?? 100
        };
        var tab = new RequestTab(m);
        _tabs.Add(tab);
        TabStrip.SelectedItem = tab;
        UrlBox.Focus();
        return tab;
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RequestTab tab }) CloseTab(tab);
    }

    private void CloseTab(RequestTab tab)
    {
        int idx = _tabs.IndexOf(tab);
        if (idx < 0) return;
        bool closingActive = ReferenceEquals(tab, ActiveTab);
        if (ReferenceEquals(tab, _loadedTab)) _loadedTab = null; // do not capture a tab we're discarding

        _tabs.Remove(tab);
        if (_tabs.Count == 0) _tabs.Add(new RequestTab(new RequestModel()));

        if (closingActive)
            TabStrip.SelectedIndex = Math.Clamp(idx, 0, _tabs.Count - 1);
    }

    /// <summary>Push a request model's values into the editor controls.</summary>
    private void LoadIntoControls(RequestModel m)
    {
        _loading = true;
        try
        {
            SelectMethod(m.Method);
            BaseUrlBox.Text = m.BaseUrl ?? "";
            UrlBox.Text = m.Path;
            HeadersItems.ItemsSource = m.Headers;
            ParamsItems.ItemsSource = m.QueryParams;
            BodyBox.Text = m.Body ?? "";
            SelectContentType(m.ContentType);
            AuthTypeCombo.SelectedIndex = m.AuthType switch { "Bearer" => 1, "Basic" => 2, _ => 0 };
            BearerTokenBox.Text = m.AuthType == "Bearer" ? m.AuthSecret ?? "" : "";
            BasicUserBox.Text = m.AuthUser ?? "";
            BasicPassBox.Text = m.AuthType == "Basic" ? m.AuthSecret ?? "" : "";
            IgnoreServerCertCheck.IsChecked = m.IgnoreServerCert;
            TimeoutBox.Text = m.TimeoutSeconds.ToString();
            SelectCertByThumbprint(m.CertThumbprint);
        }
        finally { _loading = false; }
    }

    /// <summary>Read the editor controls back into a request model.</summary>
    private void CaptureControlsInto(RequestModel m)
    {
        m.Method = SelectedMethod();
        m.BaseUrl = BaseUrlBox.Text;

        // Fold any query typed straight into the URL box into the parameter grid.
        var (path, typedQuery) = RequestUrl.SplitForEditing(UrlBox.Text.Trim());
        m.Path = path;
        if (typedQuery.Count > 0)
        {
            m.QueryParams.Clear();
            foreach (var kv in typedQuery) m.QueryParams.Add(new ParamRow { Key = kv.Key, Value = kv.Value });
        }

        m.Body = BodyBox.Text;
        m.ContentType = SelectedContentType();
        m.AuthType = AuthTypeCombo.SelectedIndex switch { 1 => "Bearer", 2 => "Basic", _ => "None" };
        m.AuthUser = BasicUserBox.Text;
        m.AuthSecret = AuthTypeCombo.SelectedIndex == 1 ? BearerTokenBox.Text : BasicPassBox.Text;
        m.CertThumbprint = SelectedThumbprint();
        m.IgnoreServerCert = IgnoreServerCertCheck.IsChecked == true;
        m.TimeoutSeconds = ParseTimeout();
        // Headers and QueryParams edited in the grids are the model's own collections — already current.
    }

    private void ShowTabResponse(RequestTab tab)
    {
        if (tab.LastResponse is { } r) RenderResponse(r);
        else if (tab.Snapshot is { } s) RenderSnapshot(s, fromHistory: false);
        else ClearResponse();
    }

    // ---------- sidebar mode ----------

    private void ShowHistory_Click(object sender, RoutedEventArgs e) => SetSidebarMode(history: true);
    private void ShowCollections_Click(object sender, RoutedEventArgs e) => SetSidebarMode(history: false);

    private void SetSidebarMode(bool history)
    {
        HistoryList.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        CollectionsArea.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        ClearHistoryButton.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        HistoryTabButton.Tag = history ? "active" : null;
        CollectionsTabButton.Tag = history ? null : "active";
    }

    // ---------- collections ----------

    private void InitializeCollections()
    {
        foreach (var n in _state.Collections) _collections.Add(n);
        CollectionsTree.ItemsSource = _collections;
        UpdateCollectionsHint();
    }

    private void UpdateCollectionsHint() =>
        CollectionsEmptyHint.Visibility = _collections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The collection to add into: the selected folder, a selected request's parent, else the root.</summary>
    private ObservableCollection<CollectionNode> TargetFolder()
    {
        if (CollectionsTree.SelectedItem is CollectionNode sel)
            return sel.IsFolder ? sel.Children : FindParent(sel) ?? _collections;
        return _collections;
    }

    private ObservableCollection<CollectionNode>? FindParent(CollectionNode target, ObservableCollection<CollectionNode>? scope = null)
    {
        scope ??= _collections;
        foreach (var n in scope)
        {
            if (ReferenceEquals(n, target)) return scope;
            if (n.IsFolder)
            {
                var r = FindParent(target, n.Children);
                if (r is not null) return r;
            }
        }
        return null;
    }

    private static RequestModel CloneRequest(RequestModel m) =>
        RequestModel.FromHistoryEntry(m.ToHistoryEntry(null, null));

    private void SaveToCollectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveRequest is not { } m) return;
        CaptureControlsInto(m);
        var def = string.IsNullOrWhiteSpace(m.Path) ? m.Method : $"{m.Method} {m.Path}";
        var name = InputDialog.Show(this, "Save request", "Name for this saved request", def);
        if (string.IsNullOrWhiteSpace(name)) return;

        var node = new CollectionNode { Name = name, IsFolder = false, Request = CloneRequest(m) };
        TargetFolder().Add(node);
        m.SourceCollectionId = node.Id;   // future sends of this tab record the endpoint's result
        SetSidebarMode(history: false);
        UpdateCollectionsHint();
        StatusText.Text = $"Saved “{name}” to collections.";
    }

    private void NewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Show(this, "New folder", "Folder name", "New folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        TargetFolder().Add(new CollectionNode { Name = name, IsFolder = true });
        UpdateCollectionsHint();
    }

    private void RenameNodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (CollectionsTree.SelectedItem is not CollectionNode sel) { StatusText.Text = "Select a collection item to rename."; return; }
        var name = InputDialog.Show(this, "Rename", sel.IsFolder ? "Folder name" : "Request name", sel.Name);
        if (!string.IsNullOrWhiteSpace(name)) sel.Name = name;
    }

    private void DeleteNodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (CollectionsTree.SelectedItem is not CollectionNode sel) { StatusText.Text = "Select a collection item to delete."; return; }
        (FindParent(sel) ?? _collections).Remove(sel);
        UpdateCollectionsHint();
    }

    private void CollectionsTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CollectionsTree.SelectedItem is CollectionNode { IsFolder: false, Request: { } req } node)
        {
            var clone = CloneRequest(req);
            clone.SourceCollectionId = node.Id;   // sends from this tab record the endpoint's result
            var tab = new RequestTab(clone);
            _tabs.Add(tab);
            TabStrip.SelectedItem = tab;
            StatusText.Text = $"Opened “{node.Name}” in a new tab.";
        }
    }

    /// <summary>Find a collection node by id anywhere in the tree.</summary>
    private CollectionNode? FindNodeById(string id, ObservableCollection<CollectionNode>? scope = null)
    {
        scope ??= _collections;
        foreach (var n in scope)
        {
            if (n.Id == id) return n;
            if (n.IsFolder && FindNodeById(id, n.Children) is { } hit) return hit;
        }
        return null;
    }

    // ---------- environments & variables ----------

    private void InitializeEnvironments()
    {
        foreach (var env in _state.Environments) _environments.Add(env);
        RefreshEnvCombo();
    }

    private void RefreshEnvCombo()
    {
        _envLoading = true;
        var items = new List<string> { "— no environment —" };
        items.AddRange(_environments.Select(env => string.IsNullOrWhiteSpace(env.Name) ? "(unnamed)" : env.Name));
        EnvCombo.ItemsSource = items;

        int idx = 0;
        if (_state.ActiveEnvironmentId is { } id)
        {
            int found = _environments.ToList().FindIndex(env => env.Id == id);
            if (found >= 0) idx = found + 1;
            else _state.ActiveEnvironmentId = null; // active env was deleted
        }
        EnvCombo.SelectedIndex = idx;
        _envLoading = false;
    }

    private void EnvCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_envLoading) return;
        int i = EnvCombo.SelectedIndex;
        if (i <= 0 || i - 1 >= _environments.Count)
        {
            _state.ActiveEnvironmentId = null;
            StatusText.Text = "Environment: none.";
        }
        else
        {
            var env = _environments[i - 1];
            _state.ActiveEnvironmentId = env.Id;
            StatusText.Text = $"Environment: {(string.IsNullOrWhiteSpace(env.Name) ? "(unnamed)" : env.Name)}.";
        }
    }

    private void ManageEnvButton_Click(object sender, RoutedEventArgs e)
    {
        new EnvironmentsWindow(_environments) { Owner = this }.ShowDialog();
        RefreshEnvCombo();
    }

    // ---------- import ----------

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImportButton.ContextMenu is { } cm)
        {
            cm.PlacementTarget = ImportButton;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }
    }

    private void ImportCurl_Click(object sender, RoutedEventArgs e)
    {
        var text = InputDialog.ShowMultiline(this, "Import cURL",
            "Paste a curl command (Ctrl+Enter to import):");
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var model = RequestModel.FromParsed(CurlParser.Parse(text));
            var tab = new RequestTab(model);
            _tabs.Add(tab);
            TabStrip.SelectedItem = tab;
            StatusText.Text = "Imported request from cURL.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't parse that cURL command: " + ex.Message;
        }
    }

    private void ImportOpenApi_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import OpenAPI / Swagger file",
            Filter = "OpenAPI / Swagger (JSON)|*.json|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var parsed = OpenApiImporter.Parse(System.IO.File.ReadAllText(dlg.FileName));
            var node = CollectionNode.FromParsed(parsed);
            _collections.Add(node);
            UpdateCollectionsHint();
            SetSidebarMode(history: false);
            var count = CountRequests(node);
            StatusText.Text = count == 0
                ? $"Imported “{parsed.Name}”, but it defined no operations."
                : $"Imported {count} request(s) from “{parsed.Name}”.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't import that file: " + ex.Message;
        }
    }

    private static int CountRequests(CollectionNode n) =>
        (n.IsFolder ? 0 : 1) + n.Children.Sum(CountRequests);

    // ---------- rendered website view ----------

    private void ResponseTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, ResponseTabs)) return;
        if (ResponseTabs.SelectedItem is TabItem { Header: "Rendered" }) _ = ShowRenderedAsync();
    }

    private void RenderReloadButton_Click(object sender, RoutedEventArgs e) => _ = ShowRenderedAsync();

    private async Task ShowRenderedAsync()
    {
        if (ActiveRequest is not { } model) return;
        CaptureControlsInto(model);
        var (url, _, _, _) = ResolveActive();
        if (string.IsNullOrWhiteSpace(url))
        {
            RenderHint.Text = "Enter a URL in the request above, then reopen this tab to render the page.";
            RenderHint.Visibility = Visibility.Visible;
            return;
        }
        if (!await EnsureBrowserAsync()) return;

        _renderTraceTarget = ActiveTab;
        RebuildBrowserSession();
        RenderUrlText.Text = url;
        try
        {
            RenderHint.Visibility = Visibility.Collapsed;
            Browser.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            RenderHint.Text = "Couldn't render that URL: " + ex.Message;
            RenderHint.Visibility = Visibility.Visible;
        }
    }

    private async Task<bool> EnsureBrowserAsync()
    {
        if (_browserReady) return true;
        try
        {
            await Browser.EnsureCoreWebView2Async();
            var core = Browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += Browser_WebResourceRequested;
            _browserReady = true;
            return true;
        }
        catch (Exception ex)
        {
            RenderHint.Text = "The rendered view needs the Microsoft Edge WebView2 runtime, " +
                              "which couldn't be started on this machine:\n" + ex.Message;
            RenderHint.Visibility = Visibility.Visible;
            return false;
        }
    }

    private void RebuildBrowserSession()
    {
        _browserSession?.Dispose();
        _browserCts?.Cancel();
        _browserCts?.Dispose();
        _browserCts = new CancellationTokenSource();
        var cert = SelectedCert();
        _renderCertSubject = cert?.Subject;
        _browserSession = new MtlsBrowserSession(cert, IgnoreServerCertCheck.IsChecked == true);
    }

    private void Browser_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_browserSession is not { } session || _browserCts is not { } cts) return;

        var deferral = e.GetDeferral();
        var request = e.Request;
        string method = request.Method;
        string uri = request.Uri;
        var headers = request.Headers.Select(h => new KeyValuePair<string, string>(h.Key, h.Value)).ToList();
        byte[]? body = ReadRequestContent(request.Content);
        _ = HandleResourceAsync(session, cts.Token, e, deferral, method, uri, headers, body);
    }

    private async Task HandleResourceAsync(
        MtlsBrowserSession session, CancellationToken ct,
        CoreWebView2WebResourceRequestedEventArgs e, CoreWebView2Deferral deferral,
        string method, string uri, List<KeyValuePair<string, string>> headers, byte[]? body)
    {
        try
        {
            var result = await session.FetchAsync(method, new Uri(uri), headers, body, null, ct);
            var headerLines = string.Join("\r\n", result.Headers
                .Where(h => Forwardable(h.Key))
                .Select(h => $"{h.Key}: {h.Value}"));
            e.Response = Browser.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(result.Body), result.StatusCode,
                string.IsNullOrEmpty(result.ReasonPhrase) ? "OK" : result.ReasonPhrase, headerLines);
            RecordNetwork(_renderTraceTarget, new NetworkEntry
            {
                Method = method,
                Url = uri,
                StatusCode = result.StatusCode,
                ReasonPhrase = result.ReasonPhrase,
                ContentType = result.ContentType,
                Size = result.Body?.LongLength ?? 0,
                ElapsedMs = result.ElapsedMs,
                ClientCertSubject = _renderCertSubject,
                Source = "Rendered",
                RequestHeaders = headers,
                ResponseHeaders = result.Headers ?? new List<KeyValuePair<string, string>>()
            });
        }
        catch (Exception ex)
        {
            var bytes = Encoding.UTF8.GetBytes(
                "<html><body style='font-family:Consolas;background:#0e1214;color:#ff6e7a;padding:24px'>" +
                "<h2>Could not load this resource</h2><pre>" +
                System.Net.WebUtility.HtmlEncode(ex.Message) + "</pre></body></html>");
            e.Response = Browser.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(bytes), 502, "Bad Gateway", "Content-Type: text/html; charset=utf-8");
            if (!ct.IsCancellationRequested)
                RecordNetwork(_renderTraceTarget, new NetworkEntry
                {
                    Method = method, Url = uri, Error = ex.Message,
                    ClientCertSubject = _renderCertSubject, Source = "Rendered", RequestHeaders = headers
                });
        }
        finally { deferral.Complete(); }
    }

    // Hop-by-hop / content-coding headers must not be forwarded (the body is already decompressed).
    private static bool Forwardable(string name) =>
        !name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("Connection", StringComparison.OrdinalIgnoreCase);

    private static byte[]? ReadRequestContent(Stream? content)
    {
        if (content is null) return null;
        try
        {
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            return ms.Length == 0 ? null : ms.ToArray();
        }
        catch { return null; }
    }

    // ---------- network trace ----------

    private const int MaxNetworkEntries = 500;

    private string _networkClass = "All";
    private NetworkEntry? _networkDetailEntry;

    private void RecordNetwork(RequestTab? tab, NetworkEntry entry)
    {
        if (tab is null) return;
        tab.Network.Add(entry);
        while (tab.Network.Count > MaxNetworkEntries) tab.Network.RemoveAt(0);
        if (ReferenceEquals(tab, ActiveTab))
        {
            UpdateNetworkCount();
            if (NetworkList.Items.Count > 0)
                NetworkList.ScrollIntoView(NetworkList.Items[NetworkList.Items.Count - 1]);
        }
    }

    private void BindNetwork(RequestTab tab)
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(tab.Network);
        view.Filter = NetworkRowVisible;
        NetworkList.ItemsSource = view;
        CloseNetworkDetail();
        UpdateNetworkCount();
    }

    private bool NetworkRowVisible(object item) =>
        item is NetworkEntry en &&
        en.Matches(NetworkSearchBox.Text, _networkClass, NetworkCertOnly.IsChecked == true);

    private void NetworkClass_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button clicked) return;
        _networkClass = clicked.Content?.ToString() ?? "All";
        foreach (var chip in new[] { NetClassAll, NetClass2xx, NetClass3xx, NetClass4xx, NetClass5xx, NetClassErr })
            chip.Tag = ReferenceEquals(chip, clicked) ? "active" : null;
        RefreshNetworkFilter();
    }

    private void NetworkSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshNetworkFilter();

    private void NetworkFilter_Changed(object sender, RoutedEventArgs e) => RefreshNetworkFilter();

    private void RefreshNetworkFilter()
    {
        if (NetworkList?.ItemsSource is System.ComponentModel.ICollectionView view) view.Refresh();
        UpdateNetworkCount();
    }

    private void UpdateNetworkCount()
    {
        if (NetworkCountText is null) return;
        int total = ActiveTab?.Network.Count ?? 0;
        if (total == 0)
        {
            NetworkCountText.Text = "No requests yet — send a request or open the Rendered tab.";
            return;
        }
        var shown = NetworkList.Items.OfType<NetworkEntry>().ToList();
        long size = shown.Sum(en => en.Error is null ? en.Size : 0);
        string counts = shown.Count == total
            ? $"{total} request{(total == 1 ? "" : "s")}"
            : $"{shown.Count} of {total} requests";
        NetworkCountText.Text = $"{counts} · {NetworkEntry.FormatSize(size)}";
    }

    private void ClearNetworkButton_Click(object sender, RoutedEventArgs e)
    {
        ActiveTab?.Network.Clear();
        CloseNetworkDetail();
        UpdateNetworkCount();
    }

    private void NetworkList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NetworkList.SelectedItem is not NetworkEntry entry)
        {
            CloseNetworkDetail();
            return;
        }
        _networkDetailEntry = entry;
        NetworkDetailTitle.Text = $"{entry.Method}  {entry.StatusLabel}  ·  {entry.Url}";
        BuildNetworkDetailPane(entry);
        if (NetworkDetailPane.Visibility != Visibility.Visible)
        {
            NetworkDetailRow.MinHeight = 90;
            NetworkDetailRow.Height = new GridLength(190);
        }
        NetworkSplitter.Visibility = Visibility.Visible;
        NetworkDetailPane.Visibility = Visibility.Visible;
    }

    private void CloseNetworkDetail()
    {
        _networkDetailEntry = null;
        NetworkDetailPane.Visibility = Visibility.Collapsed;
        NetworkSplitter.Visibility = Visibility.Collapsed;
        NetworkDetailRow.MinHeight = 0;
        NetworkDetailRow.Height = GridLength.Auto;
        NetworkDetailHost.Children.Clear();
    }

    private void NetworkDetailClose_Click(object sender, RoutedEventArgs e) => NetworkList.SelectedItem = null;

    private void NetworkDetailCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_networkDetailEntry is { } entry) TrySetClipboard(BuildNetworkDetail(entry), "Copied request details.");
    }

    private void NetworkList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-click selects the row under the cursor so the context menu acts on it.
        var d = e.OriginalSource as DependencyObject;
        while (d is not null && d is not ListBoxItem)
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        if (d is ListBoxItem item) item.IsSelected = true;
    }

    private void NetworkCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (NetworkList.SelectedItem is NetworkEntry entry) TrySetClipboard(entry.Url, "Copied URL.");
    }

    private void NetworkCopyCurl_Click(object sender, RoutedEventArgs e)
    {
        if (NetworkList.SelectedItem is NetworkEntry entry) TrySetClipboard(entry.ToCurl(), "Copied cURL command.");
    }

    private void BuildNetworkDetailPane(NetworkEntry entry)
    {
        var host = NetworkDetailHost;
        host.Children.Clear();

        var statusBrush = (System.Windows.Media.Brush)new StatusToBrushConverter().Convert(
            entry, typeof(System.Windows.Media.Brush), null!, System.Globalization.CultureInfo.CurrentCulture);

        host.Children.Add(DetailCaption("GENERAL", first: true));
        host.Children.Add(DetailRow("URL", entry.Url));
        host.Children.Add(DetailRow("Status",
            entry.Error is not null ? "ERROR — " + entry.Error : $"{entry.StatusCode} {entry.ReasonPhrase}".Trim(),
            statusBrush));
        host.Children.Add(DetailRow("Type", string.IsNullOrEmpty(entry.ContentType) ? "—" : entry.ContentType));
        if (entry.Error is null)
        {
            host.Children.Add(DetailRow("Size", entry.SizeLabel));
            host.Children.Add(DetailRow("Time", entry.TimeLabel));
        }
        host.Children.Add(DetailRow("Started", entry.Timestamp.ToString("HH:mm:ss.fff")));
        host.Children.Add(DetailRow("Source", entry.Source == "Rendered" ? "Rendered page resource" : "Request you sent"));
        host.Children.Add(DetailRow("Client cert", ClientCertLine(entry)));

        if (entry.RequestHeaders.Count > 0)
        {
            host.Children.Add(DetailCaption($"REQUEST HEADERS ({entry.RequestHeaders.Count})"));
            foreach (var h in entry.RequestHeaders) host.Children.Add(DetailRow(h.Key, h.Value));
        }
        if (entry.ResponseHeaders.Count > 0)
        {
            host.Children.Add(DetailCaption($"RESPONSE HEADERS ({entry.ResponseHeaders.Count})"));
            foreach (var h in entry.ResponseHeaders) host.Children.Add(DetailRow(h.Key, h.Value));
        }
    }

    private static string ClientCertLine(NetworkEntry e)
    {
        if (e.ClientCertSubject is null) return "none";
        string how = e.Source == "Request"
            ? (e.ClientCertPresented ? " (presented to the server)" : " (offered; the server did not request it)")
            : " (via the mutual-TLS session)";
        return e.ClientCertSubject + how;
    }

    private TextBlock DetailCaption(string text, bool first = false) => new()
    {
        Text = text,
        Style = (Style)FindResource("Caption"),
        FontSize = 10,
        Margin = new Thickness(0, first ? 0 : 12, 0, 4)
    };

    private FrameworkElement DetailRow(string label, string value, System.Windows.Media.Brush? valueBrush = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var key = new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            Foreground = (System.Windows.Media.Brush)FindResource("Text.Muted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 10, 0)
        };
        var val = new TextBlock
        {
            Text = value,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = valueBrush ?? (System.Windows.Media.Brush)FindResource("Text.Soft")
        };
        Grid.SetColumn(val, 1);
        grid.Children.Add(key);
        grid.Children.Add(val);
        return grid;
    }

    private static string BuildNetworkDetail(NetworkEntry e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{e.Method} {e.Url}");
        sb.AppendLine();
        sb.AppendLine("Status      : " + (e.Error is not null ? "ERROR — " + e.Error : $"{e.StatusCode} {e.ReasonPhrase}"));
        sb.AppendLine("Type        : " + (e.ContentType ?? "—"));
        if (e.Error is null)
        {
            sb.AppendLine("Size        : " + e.SizeLabel);
            sb.AppendLine("Time        : " + e.TimeLabel);
        }
        sb.AppendLine("Started     : " + e.Timestamp.ToString("HH:mm:ss.fff"));
        sb.AppendLine("Source      : " + e.Source);
        sb.AppendLine("Client cert : " + ClientCertLine(e));

        if (e.RequestHeaders.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("REQUEST HEADERS");
            foreach (var h in e.RequestHeaders) sb.AppendLine($"  {h.Key}: {h.Value}");
        }
        if (e.ResponseHeaders.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("RESPONSE HEADERS");
            foreach (var h in e.ResponseHeaders) sb.AppendLine($"  {h.Key}: {h.Value}");
        }
        return sb.ToString();
    }

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

    // ---------- headers / params / auth ----------

    private void AddHeaderButton_Click(object sender, RoutedEventArgs e) => ActiveRequest?.Headers.Add(new HeaderRow());

    private void RemoveHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HeaderRow row }) ActiveRequest?.Headers.Remove(row);
    }

    private void AddParamButton_Click(object sender, RoutedEventArgs e) => ActiveRequest?.QueryParams.Add(new ParamRow());

    private void RemoveParamButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ParamRow row }) ActiveRequest?.QueryParams.Remove(row);
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
        var m = ActiveRequest!;
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var h in m.Headers)
            if (h.Enabled && !string.IsNullOrWhiteSpace(h.Name))
                headers.Add(new KeyValuePair<string, string>(h.Name.Trim(), h.Value ?? ""));

        switch (m.AuthType)
        {
            case "Bearer" when !string.IsNullOrWhiteSpace(m.AuthSecret):
                headers.Add(new KeyValuePair<string, string>("Authorization", "Bearer " + m.AuthSecret!.Trim()));
                break;
            case "Basic":
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{m.AuthUser}:{m.AuthSecret}"));
                headers.Add(new KeyValuePair<string, string>("Authorization", "Basic " + basic));
                break;
        }
        return headers;
    }

    private ApiRequest BuildRequest()
    {
        var m = ActiveRequest!;
        var (url, headers, body, unresolved) = ResolveActive();
        _unresolvedVars = unresolved;
        string? contentType = body is not null && m.ContentType != "(none)" ? m.ContentType : null;

        return new ApiRequest
        {
            Method = new HttpMethod(m.Method),
            Url = url,
            Headers = headers,
            Body = body,
            ContentType = contentType,
            Timeout = TimeSpan.FromSeconds(m.TimeoutSeconds)
        };
    }

    /// <summary>Build the current environment's variable map.</summary>
    private Dictionary<string, string> CurrentVars()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        var env = _environments.FirstOrDefault(e => e.Id == _state.ActiveEnvironmentId);
        if (env is not null)
            foreach (var v in env.Variables)
                if (!string.IsNullOrWhiteSpace(v.Key)) d[v.Key.Trim()] = v.Value ?? "";
        return d;
    }

    /// <summary>Resolve the active request's URL, headers, and body against the current
    /// environment's <c>{{variables}}</c>, returning any tokens that couldn't be resolved.</summary>
    private (string Url, List<KeyValuePair<string, string>> Headers, string? Body, List<string> Unresolved) ResolveActive()
    {
        var m = ActiveRequest!;
        var vars = CurrentVars();
        var unresolved = new List<string>();
        string R(string s)
        {
            var (r, u) = VariableResolver.Resolve(s ?? "", vars);
            foreach (var x in u) if (!unresolved.Contains(x)) unresolved.Add(x);
            return r;
        }

        var url = R(m.EffectiveUrl());
        var headers = BuildHeaders().Select(h => new KeyValuePair<string, string>(R(h.Key), R(h.Value))).ToList();
        var body = string.IsNullOrEmpty(m.Body) ? null : R(m.Body);
        return (url, headers, body, unresolved);
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => _ = SendRequestAsync();

    private async System.Threading.Tasks.Task SendRequestAsync()
    {
        if (!SendButton.IsEnabled) return;
        if (ActiveRequest is not { } model) return;
        CaptureControlsInto(model);
        if (string.IsNullOrWhiteSpace(model.EffectiveUrl())) { StatusText.Text = "Enter a URL."; return; }
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
                request, cert, model.IgnoreServerCert, cancellationToken: _cts.Token);
            RenderResponse(response);
            if (_unresolvedVars.Count > 0)
                StatusText.Text += "   ⚠ unresolved " +
                    string.Join(", ", _unresolvedVars.Select(v => "{{" + v + "}}"));
            if (ActiveTab is { } tab)
            {
                tab.LastResponse = response;
                tab.LastRawText = _lastRawText;
                tab.Snapshot = BuildSnapshot(response);
                RecordNetwork(tab, new NetworkEntry
                {
                    Method = request.Method.Method,
                    Url = request.Url,
                    StatusCode = response.StatusCode,
                    ReasonPhrase = response.ReasonPhrase,
                    ContentType = response.ContentType,
                    Size = response.Body?.LongLength ?? 0,
                    ElapsedMs = response.Elapsed.TotalMilliseconds,
                    ClientCertSubject = response.Connection?.ClientCertificateSubject,
                    ClientCertPresented = response.Connection?.ClientCertificateSent ?? false,
                    Error = response.Error?.Message,
                    Source = "Request",
                    RequestHeaders = request.Headers?.ToList() ?? new List<KeyValuePair<string, string>>(),
                    ResponseHeaders = response.Headers?.ToList() ?? new List<KeyValuePair<string, string>>()
                });
            }
            // Record the outcome on the collection entry this tab came from — but only while the
            // tab still targets that saved endpoint (same method and URL).
            if (model.SourceCollectionId is { } srcId &&
                FindNodeById(srcId) is { IsFolder: false, Request: { } saved } srcNode &&
                saved.Method == model.Method && saved.EffectiveUrl() == model.EffectiveUrl())
                srcNode.RecordResult(response.Error is null ? response.StatusCode : null, DateTime.UtcNow);
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
        if (ActiveRequest is not { } model) return;
        CaptureControlsInto(model);
        if (string.IsNullOrWhiteSpace(model.EffectiveUrl())) { StatusText.Text = "Enter a URL first."; return; }
        TrySetClipboard(BuildCurl(), "Copied cURL command.");
    }

    private void TrySetClipboard(string text, string ok)
    {
        try { Clipboard.SetText(text); StatusText.Text = ok; }
        catch (Exception ex) { StatusText.Text = "Copy failed: " + ex.Message; }
    }

    private string BuildCurl()
    {
        var m = ActiveRequest!;
        var (url, headers, body, _) = ResolveActive();
        var sb = new StringBuilder();
        sb.Append("curl -X ").Append(m.Method).Append(" \"").Append(url).Append('"');
        foreach (var h in headers)
            sb.Append(" \\\n  -H \"").Append(h.Key).Append(": ").Append(h.Value.Replace("\"", "\\\"")).Append('"');
        if (m.CertThumbprint is not null)
            sb.Append(" \\\n  --cert \"").Append(m.CertThumbprint).Append("\"   # client cert from the Windows store (curl built with Schannel)");
        if (m.IgnoreServerCert)
            sb.Append(" \\\n  -k");
        if (!string.IsNullOrEmpty(body))
            sb.Append(" \\\n  --data \"").Append(body.Replace("\"", "\\\"")).Append('"');
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
        if (ActiveRequest is not { } model) return;
        var entry = model.ToHistoryEntry(response.StatusCode, BuildSnapshot(response));
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
        if (ActiveTab is not { } tab) return;

        // Replace the active tab's request with the stored one (kept as the same instance).
        _loading = true;
        try { tab.Request.LoadFrom(entry); }
        finally { _loading = false; }

        LoadIntoControls(tab.Request);
        tab.UpdateTitle();

        // Restore that request's own response into this tab.
        tab.LastResponse = null;
        tab.LastRawText = "";
        tab.Snapshot = entry.Response;
        if (entry.Response is not null) RenderSnapshot(entry.Response, fromHistory: true);
        else ClearResponse();
    }

    private void RenderSnapshot(ResponseSnapshot s, bool fromHistory)
    {
        var suffix = fromHistory ? "   (from history)" : "";
        DiagnosticsBox.Text = s.Diagnostics ?? "No connection details available.";

        if (s.ErrorMessage is not null)
        {
            _lastResponse = null;
            _lastRawText = s.ErrorMessage;
            SetPretty(s.ErrorMessage, BodyKind.Text);
            RawBox.Text = s.ErrorMessage;
            ResponseHeadersBox.Text = "";
            StatusText.Text = $"Error [{s.ErrorKind}]: {s.ErrorMessage}{suffix}";
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
            SetPretty("(the response body was too large to keep — re-send to see it)", BodyKind.Text);
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
        StatusText.Text = $"{s.StatusCode} {s.ReasonPhrase}  •  {s.Body.Length} bytes  •  {s.ElapsedMs:F0} ms{suffix}";
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

    private string SelectedMethod() => ((ComboBoxItem)MethodCombo.SelectedItem).Content!.ToString()!;

    private string SelectedContentType() =>
        (ContentTypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "application/json";

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

    // ---------- URL / method live edits ----------

    private void MethodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ActiveRequest is null) return;
        ActiveRequest.Method = SelectedMethod();
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || ActiveRequest is null) return;
        ActiveRequest.Path = UrlBox.Text; // keeps the tab title in step as you type
    }

    private void UrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || ActiveRequest is null) return;
        if (!UrlBox.Text.Contains('?')) return;

        var (path, ps) = RequestUrl.SplitForEditing(UrlBox.Text.Trim());
        _loading = true;
        try
        {
            UrlBox.Text = path;
            ActiveRequest.Path = path;
            ActiveRequest.QueryParams.Clear();
            foreach (var kv in ps) ActiveRequest.QueryParams.Add(new ParamRow { Key = kv.Key, Value = kv.Value });
        }
        finally { _loading = false; }
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
        else if (ctrl && e.Key == Key.T) { e.Handled = true; AddNewTab(); }
        else if (ctrl && e.Key == Key.W) { e.Handled = true; if (ActiveTab is { } t) CloseTab(t); }
        else if (e.Key == Key.F5) { e.Handled = true; LoadCertificates(); }
        else if (e.Key == Key.F1) { e.Handled = true; ShowHelp(); }
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
