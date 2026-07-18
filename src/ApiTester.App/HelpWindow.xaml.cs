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
            ("Discovering endpoints", Discovery),
            ("Testing responses", Testing),
            ("Environments & variables", Environments),
            ("Automatic tokens", AutoTokens),
            ("Importing & exporting", Importing),
            ("Command line", CommandLine),
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
        P("Use the “find in response…” box above the response to locate text in the body — Enter (or Find next) jumps to the next match and wraps around."),
        P("The pop-out button above the response (next to Copy body) opens either the selected view or the whole response panel — tabs and all — in its own window. Detach the entire panel to give the request editor the full main window, or pop a single view to watch, say, the Network trace beside the Pretty body. Everything stays live, and closing a popped-out window puts its content back in place."),
        P("“Copy as ▾” turns the current request into a ready-to-run snippet — cURL, PowerShell (Invoke-RestMethod), Python (requests), or C# (HttpClient) — with {{variables}} resolved and headers and body included."),
        NoteBox("No client certificates on this machine? You can still test any endpoint that doesn't require one. To prove the certificate path end-to-end with no real server, click Run Self-Test at the bottom of the window."));

    private UIElement RequestsAndTabs() => Section("Requests & tabs",
        P("A request is built from the request line (method, URL, timeout) plus six tabs beneath it."),
        Sub("THE REQUEST TABS"),
        Bullets(
            "Params — a key/value grid for the query string. Type a ?query in the URL and it splits into the grid; the grid is recombined onto the URL, correctly encoded, when you send.",
            "Headers — a key/value grid; tick a row to include it.",
            "Body — a request body with a content-type selector, or switch it to Form data (multipart) to add fields and upload files (tick File).",
            "Auth — Auto (use a captured token, the default), None, Bearer token, or Basic (username / password). The helper builds the Authorization header for you.",
            "Capture — save a value from the response into a {{variable}} for later requests (see Automatic tokens).",
            "Tests — assert on the response so a suite can pass/fail (see Testing responses)."),
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
            "Not in the store? “From file…” loads a client certificate from a .pfx/.p12 (with an optional password) or a .pem/.crt for this session — headless, use --cert-file / --cert-password / --key-file.",
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
            "Each saved request remembers its last result: after you send it, a dot appears next to its name — mint when the last send returned a 2xx (known good), red when it failed or returned an error status. Hover the entry for when it was last checked and what it returned.",
            "Collections persist between sessions, including each request's last result."),
        P("Right-click a collection or folder and choose “Set website & certificate…” to give it " +
          "defaults: endpoints opened from it inherit that website and certificate when they don't " +
          "carry their own. The first successful send from a collection remembers the pair " +
          "automatically, so clicking through an imported API just works."),
        Sub("HISTORY"),
        P("History lists your recent requests, labelled by path with the host beneath. Click one to reload the entire request — website, certificate, headers, auth, timeout, and body — and the response it returned. The app also remembers your window, last certificate, and settings between runs."));

    private UIElement Discovery() => Section("Discovering endpoints",
        P("When an API ships without documentation, use Discover to find out which endpoints exist. " +
          "Click “Discover…” in the toolbar, point it at a website, and hit Discover — it starts with a " +
          "built-in list of common endpoints (the “Use built-in list” button loads it), or choose/paste " +
          "your own. Each candidate is sent with your client certificate and any captured token."),
        Sub("READING THE RESULTS"),
        P("Each row shows the outcome: Found (2xx), Unauthorized (401/403 — it exists but needs auth), " +
          "MethodNotAllowed (405 — it exists, wrong method), Redirect (3xx), ServerError (5xx), NotFound (404), " +
          "OtherStatus (any other code), or Error (couldn't connect). Everything except NotFound and Error is " +
          "treated as a discovery. Hide the noise with the “Hide 404s / errors” toggle."),
        Sub("TURNING FINDINGS INTO REQUESTS"),
        P("Double-click a row to open that endpoint in a new request tab, or “Save discovered to collection…” " +
          "to store them all as saved requests you can run later."),
        NoteBox("The same discovery runs headless: certapi fuzz <website> (with no wordlist it uses the " +
                "built-in starter list; pass -w <file> for a thorough sweep). The starter list also ships " +
                "as wordlists/common-api-endpoints.txt."));

    private UIElement Testing() => Section("Testing responses",
        P("Add assertions on a request's Tests tab to turn it into a real test. Each assertion checks " +
          "one thing about the response and either passes or fails."),
        Sub("WHAT YOU CAN CHECK"),
        P("Target: Status · Time (ms) · a Header · a Body JSON path (like data.id) · the Body text. " +
          "Comparison: == , != , contains, matches (regex), exists, absent, < , > . For example: " +
          "Status == 200, Body data.id exists, Time < 500, Header Content-Type contains json."),
        Sub("HOW IT'S USED"),
        P("After you send, the status line shows a ✓ tests 3/3 passed summary and the Diagnostics view " +
          "lists each result. In suites, certapi run passes a request only when all its assertions pass — " +
          "a request with no assertions still passes on any 2xx, so tests are opt-in per request."),
        NoteBox("Run a suite headless with certapi run <collection>; failed assertions are printed on " +
                "stderr and included in --json output, and the exit code is non-zero if any request fails. " +
                "Add --data <file.csv|.json> to repeat the request(s) once per row, each row's columns " +
                "filling {{variables}} — table-testing an endpoint across many inputs."));

    private UIElement Environments() => Section("Environments & variables",
        P("Define values once and reuse them anywhere with {{name}} placeholders — ideal for switching between Dev, Staging, and Prod without editing every request."),
        Bullets(
            "Click Edit next to the ENV selector (top bar) to manage environments and their key/value variables.",
            "Pick the active environment from the ENV dropdown.",
            "Use {{variable}} in the URL, query, headers, body, or auth. It is substituted when you send; saved requests keep the raw {{tokens}}.",
            "A token with no value is left untouched and reported in the status line, so nothing is sent silently wrong."),
        Sub("EXAMPLE"),
        CodeLine("{{base}}/users/{{userId}}"),
        P("With base = https://api.internal.corp and userId = 42, that request is sent to https://api.internal.corp/users/42."),
        Sub("CAPTURE A TOKEN FROM A RESPONSE"),
        P("A request's Capture tab can save a value from its response into a {{variable}}: set a Variable name, choose Body (a dotted JSON path like data.access_token) or Header (a header name), and the value is written to your active environment when you send — a “Captured” environment is created if you don't have one selected. Reuse it as {{token}} in a Bearer token or any field. This turns an auth call + token reuse into two clicks."));

    private UIElement AutoTokens() => Section("Automatic tokens",
        P("Call a login endpoint and the app spots the bearer token in the response — access_token, " +
          "id_token, token, accessToken, or jwt in the JSON body (top level or under data/result), " +
          "or an X-Auth-Token / X-Access-Token header. No setup needed."),
        Sub("SCOPED TO THE WEBSITE"),
        P("A captured token belongs to the exact website it came from (scheme, host, and port). " +
          "Requests to any other website never receive it."),
        Sub("USING IT"),
        P("Requests whose Auth type is “Auto (captured token)” — the default — attach the token " +
          "automatically. A chip in the status bar shows the active website's token and its expiry; " +
          "click it to inspect, clear, or turn automatic tokens off. Pick “None (never send auth)” " +
          "on a request to opt out."),
        Sub("EVERYWHERE"),
        P("The same capture-and-reuse works headless: certapi send and certapi run print a note " +
          "when they capture or use a token (--no-auto-token disables it), and the MCP server " +
          "keeps a per-session token store so agent login flows just work."),
        Sub("SESSION COOKIES"),
        P("The app keeps a cookie jar for the session, like a browser: a Set-Cookie in any response " +
          "is stored and sent back on later requests to that host, so cookie-based logins work across " +
          "sends. Headless, add --cookies to certapi run to share a jar across a suite."),
        NoteBox("Explicit auth always wins: a Bearer/Basic setting or a manual Authorization " +
                "header is never overridden, and expired tokens are never sent. Captured tokens " +
                "are saved with your workspace in plain text — treat exported workspaces as private."));

    private UIElement Importing() => Section("Importing & exporting",
        P("Bring requests in from elsewhere with the Import ▾ menu next to the tabs."),
        Sub("PASTE CURL"),
        P("Paste a curl command and it opens a ready-to-send tab with the method, URL, query parameters, headers, body, and auth filled in. It understands -X, -H, -d / --data, -u (Basic auth), -k (insecure), an Authorization: Bearer header (mapped to the Bearer helper), quoting, and line continuations."),
        Sub("IMPORT OPENAPI / SWAGGER"),
        P("Choose a JSON OpenAPI 3.x or Swagger 2.0 file and it builds a collection of requests, grouped into folders by tag, with the server (OpenAPI) or host/basePath (Swagger) used as each request's website."),
        Sub("SAVE / LOAD A WORKSPACE"),
        P("“Export workspace…” in the Import ▾ menu saves everything — open tabs, collections (with their known-good results), environments, saved websites, and history — to a single JSON file. “Import workspace…” loads one back, either merging into what you have or replacing it. Use it to move between machines, keep named snapshots of a project, or hand a teammate a ready-to-use setup."),
        NoteBox("A workspace file includes request auth values, environment variables (including captured tokens), and response history — treat it as a private file."),
        Sub("EXPORT AS OPENAPI"),
        P("“Export as OpenAPI…” at the bottom of the collections sidebar writes the selected folder — or all collections when nothing is selected — as an OpenAPI 3.0 JSON file: folders become tags, each saved request becomes an operation with its query parameters, headers, and body example, and a request's known-good note (when it was last checked and what it returned) becomes the operation description."),
        NoteBox("Exports are safe to share: authentication is written only as a security scheme — bearer tokens, usernames, and passwords are never written to the file."));

    private UIElement CommandLine() => Section("Command line",
        P("certapi.exe — a separate download on the releases page — is the tester without the window, built for scripts and scheduled tasks."),
        Bullets(
            "certapi send <url> sends a one-off request; pick a client certificate with --cert <thumbprint or subject> (or --cert-file for a .pfx/.pem). The body goes to stdout, diagnostics to stderr. Upload files as multipart with -F \"field=value\" -F \"file=@path\".",
            "certapi run <collection or folder> runs saved requests as a pass/fail suite (a request passes when its Tests all pass, or on any 2xx if it has none) and updates their known-good markers — automatically against your live workspace, or add --record when running from an exported workspace file (--workspace).",
            "certapi fuzz <base-url> discovers endpoints from a wordlist — pass -w <file>, or omit it for the built-in starter list — and reports which paths exist on an undocumented API.",
            "certapi send also supports GraphQL (--graphql \"<query>\" --gql-variables \"{...}\") — a JSON { query, variables } POST.",
            "certapi certs lists client certificates; certapi selftest proves the mutual-TLS path end to end.",
            "certapi serve <upstream> --port <n> runs a local gateway on 127.0.0.1: point an app's base URL at the port and it reaches a certificate-protected site with your client certificate attached — no mTLS code in the app.",
            "certapi mcp runs a Model Context Protocol server so an AI agent can make mTLS calls with a certificate you pin at launch, bounded by a host allowlist — send_request, list_certificates, list_saved, run_saved, and self_test tools over stdio.",
            "certapi import / export move cURL commands, OpenAPI documents, and whole workspaces in and out.",
            "Exit codes are script-friendly: 0 success, 1 failure, 2 usage error, 3 data error. Run certapi help <command> for all options."),
        NoteBox("While the app is open, headless runs skip writing results (the app would overwrite them when it closes) — scheduled checks record normally."));

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
