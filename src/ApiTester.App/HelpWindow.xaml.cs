using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ApiTester.App;

public partial class HelpWindow : Window
{
    private const string RepoUrl = "https://github.com/Real-Fruit-Snacks/windows-cert-api-tester";
    private const string DocsUrl = "https://real-fruit-snacks.github.io/windows-cert-api-tester/";

    private readonly List<(string Title, Func<UIElement> Build)> _sections;
    private static readonly FontFamily Code = new("Consolas");

    public HelpWindow()
    {
        InitializeComponent();
        _sections = new()
        {
            ("Getting started", GettingStarted),
            ("Requests & tabs", RequestsAndTabs),
            ("Certificates & mTLS", Certificates),
            ("Collections & history", Collections),
            ("Environments & variables", Environments),
            ("Importing", Importing),
            ("Rendered website", Rendered),
            ("Keyboard shortcuts", Shortcuts),
            ("About", About),
        };
        var titles = new List<string>();
        foreach (var s in _sections) titles.Add(s.Title);
        SectionList.ItemsSource = titles;
        SectionList.SelectedIndex = 0;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyDarkTitleBar(this);
    }

    private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int i = SectionList.SelectedIndex;
        if (i >= 0 && i < _sections.Count) ContentHost.Content = _sections[i].Build();
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- content ----------

    private Brush B(string key) => (Brush)FindResource(key);

    private UIElement Section(string title, params UIElement[] body)
    {
        var panel = new StackPanel { MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock
        {
            Text = title, FontSize = 20, FontWeight = FontWeights.Bold,
            Foreground = B("Accent"), Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap
        });
        foreach (var el in body) panel.Children.Add(el);
        return panel;
    }

    private TextBlock Sub(string text) => new()
    {
        Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = B("Text.Muted"),
        Margin = new Thickness(0, 18, 0, 6)
    };

    private TextBlock P(string text) => new()
    {
        Text = text, FontSize = 13.5, Foreground = B("Text.Soft"), Margin = new Thickness(0, 0, 0, 10),
        TextWrapping = TextWrapping.Wrap, LineHeight = 21, LineStackingStrategy = LineStackingStrategy.BlockLineHeight
    };

    private UIElement Bullets(params string[] items)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var text in items)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dot = new TextBlock { Text = "▸", Foreground = B("Accent"), FontSize = 11, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
            var tx = new TextBlock { Text = text, Foreground = B("Text.Soft"), FontSize = 13.5, TextWrapping = TextWrapping.Wrap, LineHeight = 21, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
            Grid.SetColumn(tx, 1);
            g.Children.Add(dot);
            g.Children.Add(tx);
            sp.Children.Add(g);
        }
        return sp;
    }

    private UIElement CodeLine(string text) => new Border
    {
        Background = B("Bg.Input"), BorderBrush = B("Border"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6), Padding = new Thickness(11, 7, 11, 7),
        Margin = new Thickness(0, 2, 0, 12), HorizontalAlignment = HorizontalAlignment.Left,
        Child = new TextBlock { Text = text, FontFamily = Code, Foreground = B("Accent.Alt"), FontSize = 13 }
    };

    private UIElement NoteBox(string text) => new Border
    {
        Background = B("Bg.Panel"), BorderBrush = B("Accent"), BorderThickness = new Thickness(3, 0, 0, 0),
        Padding = new Thickness(13, 10, 13, 10), Margin = new Thickness(0, 8, 0, 10),
        Child = new TextBlock { Text = text, Foreground = B("Text.Soft"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight }
    };

    private UIElement KeyTable((string Key, string Action)[] rows)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows.Length; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new Border
            {
                BorderBrush = B("Border"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 0, 8),
                Child = new TextBlock { Text = rows[r].Key, FontFamily = Code, Foreground = B("Accent.Alt"), FontSize = 12.5 }
            };
            var act = new Border
            {
                BorderBrush = B("Border"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 0, 8),
                Child = new TextBlock { Text = rows[r].Action, Foreground = B("Text.Soft"), FontSize = 13, TextWrapping = TextWrapping.Wrap }
            };
            Grid.SetRow(key, r);
            Grid.SetRow(act, r);
            Grid.SetColumn(act, 1);
            grid.Children.Add(key);
            grid.Children.Add(act);
        }
        return grid;
    }

    private UIElement LinkButton(string label, string url)
    {
        var b = new Button { Content = label, Height = 30, FontSize = 12, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* no browser / no web access — nothing to do */ }
        };
        return b;
    }

    // ---------- sections ----------

    private UIElement GettingStarted() => Section("Getting started",
        P("Certificate API Tester sends HTTP requests and can authenticate them with a client certificate from your Windows certificate store (mutual TLS). The certificate is optional, so it also works as a general-purpose API client."),
        Sub("SEND YOUR FIRST REQUEST"),
        Bullets(
            "Pick a certificate in the CERTIFICATE row — or leave it on “— no certificate —” for a plain request.",
            "Choose a method and type a URL on the request line.",
            "Press Send (or Ctrl+Enter). The response appears in the panel below."),
        Sub("READING THE RESPONSE"),
        P("Pretty formats JSON and XML with syntax highlighting; Raw shows the exact bytes; Headers lists the response headers; Diagnostics shows the TLS and certificate details; Rendered opens the URL as a web page; Network traces every HTTP call — the request you sent and every resource the Rendered view fetched."),
        NoteBox("No client certificates on this machine? You can still test any endpoint that doesn't require one. To prove the certificate path end-to-end with no real server, click Run Self-Test at the bottom of the window."));

    private UIElement RequestsAndTabs() => Section("Requests & tabs",
        P("A request is built from the request line (method, URL, timeout) plus four tabs beneath it."),
        Sub("THE REQUEST TABS"),
        Bullets(
            "Params — a key/value grid for the query string. Type a ?query in the URL and it splits into the grid; the grid is recombined onto the URL, correctly encoded, when you send.",
            "Headers — a key/value grid; tick a row to include it.",
            "Body — a request body with a content-type selector.",
            "Auth — None, Bearer token, or Basic (username / password). The helper builds the Authorization header for you."),
        Sub("WORKING IN TABS"),
        Bullets(
            "Keep several requests open at once — each tab has its own website, certificate, and response.",
            "New tab: the + button or Ctrl+T. Close: the tab's ✕, middle-click, or Ctrl+W.",
            "Your open tabs are restored the next time you launch."),
        Sub("WEBSITE (BASE URL)"),
        P("Set a WEBSITE and the URL box becomes just the path after it — fire off /api/thing without retyping the host. Save frequently-used websites from the dropdown and pick them again later."));

    private UIElement Certificates() => Section("Certificates & mTLS",
        P("When a site asks for a client certificate, the app presents the one you picked and lets Windows sign the TLS handshake — the private key never leaves the store, so non-exportable and smart-card certificates work."),
        Bullets(
            "The picker lists certificates from CurrentUser\\My with subject, thumbprint, and expiry. Use the filter box to narrow a long list; press F5 to refresh.",
            "Certificates without a client-authentication EKU, and expired ones, are flagged in the list.",
            "“Ignore server cert errors” (off by default, clearly labelled insecure) lets you reach internal sites whose server certificate isn't publicly trusted."),
        Sub("DIAGNOSTICS"),
        P("The Diagnostics tab reports the negotiated TLS version and cipher, whether your client certificate was actually presented to the server, and the server's certificate — subject, issuer, thumbprint, expiry, and chain."),
        Sub("FAILURES"),
        P("Errors are classified so you know what went wrong: the server refused the certificate, the server's own certificate isn't trusted, a network/DNS error, or a timeout."));

    private UIElement Collections() => Section("Collections & history",
        P("The sidebar has two modes, switched from HISTORY / COLLECTIONS at the top."),
        Sub("COLLECTIONS"),
        Bullets(
            "“Save current request…” stores the active request under a name.",
            "“+ Folder” groups saved requests; drag isn't needed — save into the selected folder.",
            "Double-click a saved request to open it in a new tab. Rename or Delete with the buttons.",
            "Collections persist between sessions."),
        Sub("HISTORY"),
        P("History lists your recent requests, labelled by path with the host beneath. Click one to reload the entire request — website, certificate, headers, auth, timeout, and body — and the response it returned. The app also remembers your window, last certificate, and settings between runs."));

    private UIElement Environments() => Section("Environments & variables",
        P("Define values once and reuse them anywhere with {{name}} placeholders — ideal for switching between Dev, Staging, and Prod without editing every request."),
        Bullets(
            "Click Edit next to the ENV selector (top bar) to manage environments and their key/value variables.",
            "Pick the active environment from the ENV dropdown.",
            "Use {{variable}} in the URL, query, headers, body, or auth. It is substituted when you send; saved requests keep the raw {{tokens}}.",
            "A token with no value is left untouched and reported in the status line, so nothing is sent silently wrong."),
        Sub("EXAMPLE"),
        CodeLine("{{base}}/users/{{userId}}"),
        P("With base = https://api.internal.corp and userId = 42, that request is sent to https://api.internal.corp/users/42."));

    private UIElement Importing() => Section("Importing",
        P("Bring requests in from elsewhere with the Import ▾ menu next to the tabs."),
        Sub("PASTE CURL"),
        P("Paste a curl command and it opens a ready-to-send tab with the method, URL, query parameters, headers, body, and auth filled in. It understands -X, -H, -d / --data, -u (Basic auth), -k (insecure), an Authorization: Bearer header (mapped to the Bearer helper), quoting, and line continuations."),
        Sub("IMPORT OPENAPI / SWAGGER"),
        P("Choose a JSON OpenAPI 3.x or Swagger 2.0 file and it builds a collection of requests, grouped into folders by tag, with the server (OpenAPI) or host/basePath (Swagger) used as each request's website."));

    private UIElement Rendered() => Section("Rendered website",
        P("The Rendered response tab opens the current request's URL as a web page instead of raw text — useful when the target is a site rather than an API."),
        Bullets(
            "Every resource the page loads — the document, CSS, JavaScript, images, and XHR — is fetched with your selected client certificate, so a certificate-protected site renders fully, not just its HTML.",
            "It loads on demand: nothing runs until you open the tab. Use Reload to fetch again.",
            "The address line shows exactly which URL is being rendered.",
            "The Network tab logs every resource the page fetches — method, status, type, size, and timing, like a browser's network panel — so you can see what a certificate-protected page loads and whether each resource succeeded.",
            "In the Network tab you can filter the trace by text or status class (2xx–5xx, errors), show only calls made with your certificate, click a row for its full details and headers, drag the divider to resize the details, and right-click a row to copy its URL or a matching curl command."),
        NoteBox("The rendered view uses the Microsoft Edge WebView2 runtime, which ships with Windows 11 (and is a standard component on up-to-date Windows 10). If it isn't available, the tab explains that and the rest of the app is unaffected."));

    private UIElement Shortcuts() => Section("Keyboard shortcuts",
        KeyTable(new[]
        {
            ("Ctrl+Enter / Enter", "Send the request (Enter works in the URL box)"),
            ("Esc", "Cancel an in-flight request"),
            ("Ctrl+L", "Focus the URL box"),
            ("Ctrl+S", "Save the response to a file"),
            ("Ctrl+H", "Toggle the sidebar"),
            ("Ctrl+T", "New request tab"),
            ("Ctrl+W", "Close the current tab"),
            ("F5", "Refresh the certificate list"),
            ("F1", "Open this help"),
        }));

    private UIElement About()
    {
        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 12) };
        links.Children.Add(LinkButton("View on GitHub", RepoUrl));
        links.Children.Add(LinkButton("Documentation", DocsUrl));

        return Section("About",
            P($"Certificate API Tester  •  version {AppVersion()}"),
            P("A Windows desktop API tester that authenticates to endpoints with a client certificate from the Windows certificate store (mutual TLS), and renders whatever they return — even a full web page."),
            Sub("LINKS"),
            links,
            P(RepoUrl),
            P(DocsUrl),
            Sub("PRIVACY"),
            Bullets(
                "No telemetry. The app makes no network calls other than the requests you send.",
                "Client certificates are never exported; Windows performs the signing.",
                "Window and request settings are stored locally under %AppData%\\CertApiTester."),
            Sub("LICENSE"),
            P("Released under the MIT License."));
    }

    private static string AppVersion()
    {
        var asm = typeof(HelpWindow).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var v = info ?? asm.GetName().Version?.ToString() ?? "";
        int plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }
}
