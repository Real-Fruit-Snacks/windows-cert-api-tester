# 16. Response Views

Every way the app shows you a response, plus copying, saving, and themes.

## The response tabs

| Tab | Shows |
|---|---|
| **Pretty** | JSON and XML formatted with syntax highlighting; HTML/text shown as-is; binary hex-dumped. The body type is sniffed even when the `Content-Type` is missing or lying. |
| **Raw** | The exact response bytes decoded as text, with a **find** box (Enter for next match, wraps around). |
| **Headers** | Every response header. |
| **Diagnostics** | Connection details — TLS version and cipher, whether your client certificate was presented, and the server's certificate and chain. See [Certificates & mTLS](06-Certificates-and-mTLS.md). |
| **Rendered** | The request's URL opened as a **web page** (below). |
| **Network** | A browser-style trace of every HTTP call (below). |

## Find in a response

Above the response, the **find** box locates and selects the next match in the body — Enter (or **Find
next**) jumps to the next occurrence and wraps around. Handy for a value buried in a large payload.

## Rendered website

The **Rendered** tab loads the URL as a real web page instead of raw text — useful when the target is
a site, not an API. Every resource the page fetches (document, CSS, JS, images, XHR) is loaded with
**your client certificate**, so a certificate-protected site renders fully. It loads on demand (nothing
runs until you open the tab); **Reload** fetches again. It uses the Windows WebView2 runtime; if that's
unavailable the tab says so and the rest of the app is unaffected.

## Network trace

The **Network** tab is like a browser's network panel: every HTTP call is logged — the request you
sent and every resource the Rendered view fetched — with method, status, type, size, timing, and a
**client-certificate marker**. You can:

- filter by text, status class (2xx–5xx / errors), or **cert-only**,
- click a row for a resizable details pane with headers,
- right-click a row to copy its URL or a matching **curl** command.

## Pop-out views

The **pop-out** button (next to Copy body) detaches either the selected view or the whole response
panel into its own window — watch the Network trace beside the Pretty body, or give the request editor
the full main window. Everything stays live; closing a popped-out window returns its content.

## Copying and saving

- **Copy body** — copy the response body to the clipboard.
- **Copy as ▾** (on the request side) — the *request* as cURL / PowerShell / Python / C#.
- **Save** (`Ctrl+S`) — save any response to a file, including binary, with a sensible extension for
  the content type. On the CLI, `certapi send -o file` writes the body to a file.

## Themes

The app ships **light and dark** Terminal Workbench palettes. Toggle with the **☀ / ☾** button in the
title bar; your choice is saved and applied to every window (and the native title bar) — the syntax
highlighting and status colors adapt too. On the CLI there's no theme; output is plain text.

Next: [Import & Export](17-Import-and-Export.md).
