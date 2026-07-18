# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.26.0] - 2026-07-17

### Added
- **Automatic bearer tokens** — a token returned by any response (`access_token`, `id_token`,
  `token`, `accessToken`, `jwt`, or an `X-Auth-Token`/`X-Access-Token` header) is captured with
  zero setup and scoped to the website it came from. Requests with the new **Auto** auth mode
  (the default) attach it automatically; explicit auth is never overridden, tokens never cross
  hosts, and expired tokens are never sent. Works in the app (with a status-bar token chip to
  inspect, clear, or disable), in `certapi send`/`run` (`--no-auto-token` to opt out), and in
  the MCP server (per-session store, so agent login flows chain naturally). Tokens persist in
  the workspace in plain text, like existing auth secrets.
- **Collection defaults** — a collection or folder can hold a default website and client
  certificate ("Set website & certificate…" on right-click, or auto-remembered from the first
  successful send). Endpoints opened from a collection fill their blanks from the nearest
  folder default or the active tab — no more re-picking the website and cert for every endpoint.
- **`--debug` and `--log-file <path>`** on every certapi command: resolved URLs, sent headers
  (Authorization masked), certificate lookup, TLS details, timings, and full stack traces on
  stderr and/or appended to a log file.
- **Examples in every help screen** — `certapi help <command>` now shows realistic, copy-paste
  command examples, including login-then-call token flows and CI patterns.

### Changed
- Requests saved with auth **None** by earlier versions are treated as **Auto** (that value
  used to mean "nothing configured"); the new explicit **None (never send auth)** is preserved.
  State files are stamped with a schema version so the migration runs exactly once.

## [1.25.0] - 2026-07-16

### Added
- **Capture & reuse auth tokens** — a request can now save a value from its response into an
  environment `{{variable}}`: a JSON body field (a dotted path like `data.access_token`) or a
  response header. Call an auth endpoint once and the token is stored (in the active environment,
  or a new **Captured** one); reuse it as `Authorization: Bearer {{token}}` on later requests with
  no copy-paste. Available in the app (a new **Capture** tab on each request) and headless
  (`certapi send --capture token=access_token`, and saved requests' rules are applied by
  `certapi run`).

### Changed
- The README now includes a task-oriented **Using it** guide covering sending requests,
  certificates, collections, environments, token capture, import/export, and the `certapi`
  command line, gateway, and MCP server.

## [1.24.0] - 2026-07-16

### Added
- **MCP server for AI agents** — `certapi mcp` speaks the Model Context Protocol (JSON-RPC over
  stdio) so an AI agent can use certapi as a tool: `send_request` (an mTLS call with the pinned
  certificate), `list_certificates`, `list_saved`, `run_saved`, and `self_test`. The operator pins
  one certificate with `--cert` and an allowed host set with `--allow` at launch — the agent never
  chooses a certificate, and every outbound URL is checked against the allowlist before it leaves
  the machine. Nothing is exposed on the network.

## [1.23.0] - 2026-07-16

### Added
- **Local mTLS gateway** — `certapi serve <upstream> --port <n> --cert <id>` runs a loopback
  reverse proxy: point any local application's base URL at `http://127.0.0.1:<port>` and every
  request is forwarded to the certificate-protected upstream with your Windows-store client
  certificate attached, then the response is relayed back unchanged. The application needs no
  mTLS code of its own — just a different base URL. Binds to loopback only; add `--token <value>`
  to require callers to present a shared secret. Stop with Ctrl+C.

## [1.22.1] - 2026-07-16

### Fixed
- `certapi send --timeout` now rejects non-numeric or non-positive values as a usage error
  instead of silently using the default.
- `certapi --var` rejects overrides with a blank key (e.g. `" =value"`).
- Ambiguous collection paths in `certapi run` now list the matching entries (marked folder or
  request) instead of only counting them, and a saved entry with no request reports a clear error.
- The `--json` envelope merges duplicate response headers that differ only in case.
- Help text: clarified that `--store LocalMachine` searches the machine store in addition to
  your user store.

### Internal
- Widened a short timeout in a network test that could flake under parallel test load;
  computing the state-file path no longer creates the settings directory as a side effect.

## [1.22.0] - 2026-07-16

### Added
- **Headless mode** — a new `certapi.exe` (separate download) drives the tester from the
  command line and scripts: `send` one-off requests with a client certificate from the
  Windows store, `run` saved requests and whole collections as pass/fail suites (recording
  their known-good markers — automatically for the live workspace, with `--record` for exported
  workspace files), `certs`, `selftest`, and `import`/`export` for cURL, OpenAPI, and workspaces.
  Response bodies go to stdout and diagnostics to stderr, with script-friendly exit codes
  (0 success · 1 failure · 2 usage · 3 data).

## [1.21.0] - 2026-07-16

### Added
- **Pop out the whole response panel** — the pop-out button now offers two choices: open just the
  selected view in a window (as before), or detach the **entire response panel — tabs and all —**
  into its own window. With the panel detached, the request editor gets the full main window and a
  slim bar offers “Bring it back”. The detached panel stays fully live (switch tabs, filter the
  Network trace, copy or save the body from there), and closing its window docks it back.

## [1.20.0] - 2026-07-16

### Added
- **Pop-out response views** — a pop-out button above the response opens the selected view
  (Pretty, Raw, Headers, Diagnostics, Rendered, or Network) in its own window, so you can keep —
  say — the live Network trace or a Rendered page visible beside the main window while you work.
  The popped-out view stays fully live, the tab shows a “Bring it back” shortcut meanwhile, and
  closing the window returns the view to its tab.

## [1.19.0] - 2026-07-16

### Added
- **Save / load workspaces** — “Export workspace…” in the Import ▾ menu writes everything to a
  single JSON file: open tabs, collections (including each saved request's known-good result),
  environments, saved websites, and history. “Import workspace…” loads a workspace file back and
  asks whether to **Merge** it into your current workspace or **Replace** it. Use it to move
  between machines, keep named snapshots of a project, or hand a teammate a ready-to-use setup.
  Workspace files include request auth values and history, so treat them as private.

## [1.18.0] - 2026-07-16

### Added
- **Export as OpenAPI** — a new button at the bottom of the collections sidebar writes the
  selected folder (or all collections when nothing is selected) as an **OpenAPI 3.0 JSON** file.
  Folders become tags, each saved request becomes an operation with its query parameters, headers,
  and body example, the most common website becomes the server, and a request's known-good note
  (when it was last checked and what it returned) becomes the operation description. The exported
  file re-imports cleanly — into this app or any OpenAPI-aware tool.
- Authentication is exported **as a security scheme only** — bearer tokens, usernames, and
  passwords are never written to the file, so exports are safe to share.
- Importing an OpenAPI file now also picks up each operation's `description`.

## [1.17.0] - 2026-07-16

### Added
- **Known-good endpoints** — every saved request in your collections now remembers its last
  result. Open a saved request and send it: a dot appears next to its name in the tree — **mint**
  when the send returned a 2xx (known good), **red** when it failed or returned an error status —
  and hovering shows when it was last checked and what it returned. Results persist between
  sessions, and are only recorded while the tab still targets the saved endpoint (same method and
  URL), so editing a request can't mislabel the entry it came from.

### Changed
- Tooltips throughout the app now use the dark theme instead of the light system default.

## [1.16.0] - 2026-07-16

### Changed
- **Network panel polish** — the Network tab now works like a proper browser network panel:
  - **Filter bar**: a text filter (matches URL, method, status, and content type), status-class
    filters (**All / 2xx / 3xx / 4xx / 5xx / ERR**), and a **cert only** toggle that shows just the
    calls made with your client certificate. The counter shows how many rows match and their
    combined size (e.g. “9 of 12 requests · 2.1 MB”).
  - **Details pane**: clicking a row opens a structured details pane — general facts (URL, status,
    type, size, time, start time, source, client certificate) and the request/response headers —
    with a **Copy** button and a drag handle to resize it. New rows scroll into view as they arrive.
  - **Right-click a row** to copy its URL or a matching `curl` command.

### Fixed
- Text typed into the Network filter box was invisible (clipped by the input's vertical padding).
- Opening a row's details no longer squeezes the request list to nothing at small window sizes.

## [1.15.0] - 2026-07-15

### Added
- **Network trace** — a **Network** response tab that logs every HTTP call, like a browser's
  network panel: the request you sent **and** every resource the Rendered view fetches (document,
  CSS, JS, images, XHR). Each row shows method, status, type, size, timing, and a marker when it
  was fetched with your client certificate; click a row for its request/response headers and cert
  detail. Clearable, and it keeps metadata only (no response bodies).

## [1.14.1] - 2026-07-15

### Fixed
- The new-tab **+** button now renders as a crisp, correctly-weighted plus (it was using a
  full-width character that some fonts don't have, so it looked off).

## [1.14.0] - 2026-07-15

### Added
- **In-app Help** — a **?** button in the title bar (and **F1**) opens a Help & Reference window
  covering every part of the app: getting started, requests & tabs, certificates & mTLS,
  collections & history, environments & variables, importing, the rendered website view, a full
  keyboard-shortcut reference, and an About panel (version, links, privacy, license). All content
  is built in, so it works with no web access.

## [1.13.1] - 2026-07-15

### Changed
- Documentation: added a screenshots gallery to the README and the documentation site, and
  clarified that the Rendered website view uses the Microsoft Edge WebView2 runtime included
  with Windows 11.

### Internal
- Added unit tests covering the request-model and collection mapping (cURL / OpenAPI import and
  the history round-trip). The application binary is unchanged from 1.13.0.

## [1.13.0] - 2026-07-15

### Added
- **Rendered website view** — a new **Rendered** tab in the response area opens the current
  request's URL as a web page. Every resource the page loads (the document, styles, scripts,
  images, and XHR) is fetched with your selected client certificate, so a certificate-protected
  internal site renders fully authenticated — not just the HTML. It renders on demand (nothing
  loads until you open the tab) and has its own address line and Reload button. Uses the Microsoft
  Edge WebView2 runtime that ships with Windows 11; if it isn't present, the tab explains that.

## [1.12.0] - 2026-07-15

### Added
- **Import from cURL** — paste a `curl` command (Import ▸ Paste cURL command) and it opens a new
  tab with the method, URL, query parameters, headers, body, and auth filled in. Understands
  `-X`, `-H`, `-d`/`--data`, `-u` (Basic), `-k` (insecure), an `Authorization: Bearer` header
  (mapped to the Bearer helper), quoting, and line continuations.
- **Import OpenAPI / Swagger** — import a JSON OpenAPI 3.x or Swagger 2.0 file
  (Import ▸ Import OpenAPI file) to generate a collection of requests, grouped into folders by
  tag, with the server/host used as each request's website.

## [1.11.3] - 2026-07-15

### Fixed
- The environment selector in the title bar is wider so “— no environment —” is no longer clipped.
- Gave the request area (Params / Headers / Body / Auth) more room by default so the Basic auth
  password field and additional parameter/header rows are no longer cut off; the Auth panel also
  scrolls if space is tight.

## [1.11.2] - 2026-07-15

### Fixed
- The Environments and name-prompt dialogs now render their dark themed frame all the way to the
  top edge, instead of showing a light OS-drawn strip above the window.

## [1.11.1] - 2026-07-15

### Fixed
- The Environments window's close button now shows the correct “✕” glyph instead of an empty box.
- Text in the name prompt (new environment, new folder, rename, save request) is no longer clipped.

## [1.11.0] - 2026-07-15

### Added
- **Collections** — save named requests into folders and reopen them in a tab. A
  HISTORY / COLLECTIONS switch in the sidebar, a tree with the current request's method badge,
  and buttons to save the current request, add a folder, rename, and delete. Double-click a
  saved request to open it in a new tab. Collections persist between sessions.
- **Environments & variables** — define `{{variable}}` values per environment (e.g. Dev,
  Staging, Prod) and switch between them from the **ENV** selector in the title bar. Variables
  are substituted in the URL, query parameters, headers, body, and auth **when you send**;
  stored requests keep the raw `{{tokens}}`. Any token with no value is reported in the status
  line so nothing is sent silently wrong. An Environments editor manages environments and their
  key/value variables.

## [1.10.0] - 2026-07-15

### Added
- **Request tabs** — keep several requests open at once, each with its own website, method,
  parameters, headers, body, auth, certificate, and response. New tab with the `＋` button or
  Ctrl+T; close with the tab's `✕`, middle-click, or Ctrl+W. Open tabs are remembered between
  sessions.
- **Query-parameter editor** — a new Params tab with an enable/key/value grid. Typing a `?query`
  in the URL box splits it into the grid; the grid is recombined into the URL when the request is
  sent. Values are percent-encoded correctly.

## [1.9.0] - 2026-07-14

### Changed
- New application icon — a clean, bold padlock — replacing the busier previous design so it stays
  crisp and professional at small (taskbar) sizes.
- Added spacing between the website field and the Forget button so they no longer crowd.

## [1.8.0] - 2026-07-14

### Added
- Faint placeholder / example text in the input fields (website, URL, certificate filter, header
  name/value, body, and auth) and guidance hints in the empty response tabs, to help first-time use.

## [1.7.0] - 2026-07-14

### Added
- An application icon — a terminal window with a lock badge, in the app's palette — used for the
  executable, taskbar, and Alt-Tab. Sizes below 32px use a simplified bold chevron-and-lock glyph
  so it stays crisp at 16px.

## [1.6.0] - 2026-07-14

### Added
- **Saved websites** — save a base URL and the URL box becomes just the path appended to it.

### Changed
- History entries now capture the *whole* request (website, certificate, ignore-cert toggle,
  timeout, headers, auth, body) **and the response** each one returned; clicking an entry fully
  replaces the current request and restores its stored response.
- History is labelled by path (with the host beneath) instead of the start of the URL.

## [1.5.0] - 2026-07-14

### Changed
- Documentation refresh: a comprehensive README and documentation site covering every feature,
  with an application screenshot, a quick-start walkthrough, endpoints to try, and a
  keyboard-shortcut reference.

## [1.4.0] - 2026-07-14

### Added
- **Request history** — a sidebar of recent requests (persisted); click one to reload it in full.
- **Connection diagnostics** — a Diagnostics tab and status-line summary showing the negotiated TLS
  version and cipher, whether the client certificate was actually presented, and the server
  certificate (subject, issuer, thumbprint, expiry, and chain).
- **Syntax highlighting** for JSON and XML in the Pretty response view.
- **Headers editor** as an enable/disable key-value grid.
- **Auth helpers** — Bearer-token and Basic auth that generate the `Authorization` header.
- **Request Content-Type selector** for the body.
- **Timeout field** and a **Cancel** button for in-flight requests.
- **Copy body** and **Copy as cURL** buttons; **Save** now suggests a file extension from the content type.
- **Certificate filter** box for quickly finding a certificate.
- **Keyboard shortcuts**: Ctrl+Enter / Enter to send, Ctrl+L focus URL, Ctrl+S save, Ctrl+H toggle
  history, F5 refresh certificates, Esc cancel.
- **Remembers** window size/position, the last certificate, the ignore-cert toggle, and the timeout.

## [1.3.0] - 2026-07-14

### Changed
- Replace the OS title bar with a custom in-app title bar that matches the theme, with its
  own minimize / maximize / close controls. The window still drags, snaps, resizes, and
  maximizes normally.

## [1.2.0] - 2026-07-14

### Added
- Follow the machine's configured proxy — including "Automatically detect settings" (WPAD)
  and a "Use automatic configuration script" (PAC) from Internet Options — authenticating to
  it with the signed-in user's Windows credentials when required.

### Changed
- Dark window title bar on Windows 11 so the OS caption matches the app theme.

## [1.1.0] - 2026-07-14

### Added
- A "— no certificate —" option (now the default) so the app works as a general API tester
  for endpoints that don't require mutual TLS, and can test the no-certificate path of ones
  that make a client certificate optional.

## [1.0.0] - 2026-07-14

Initial release.

### Added
- Pick a client certificate from the Windows certificate store (`CurrentUser\My`, optionally
  `LocalMachine\My`) with subject, issuer, thumbprint, and expiry; private keys are never exported.
- Mutual-TLS request engine over `SocketsHttpHandler` supporting GET/POST/PUT/PATCH/DELETE/HEAD/OPTIONS,
  custom headers, a request body, and a configurable timeout.
- Response viewer for unknown formats: pretty-prints JSON/XML, shows HTML/text, hex-dumps binary, and
  sniffs the body when the content type is missing or misleading. Pretty / Raw / Headers views.
- Distinct failure classification: certificate refused, server certificate untrusted, network, and timeout.
- Off-by-default "ignore server certificate errors" toggle for internal sites behind a private CA.
- Built-in *Run Self-Test* that stands up a local mutual-TLS server and proves the certificate path
  end to end with no real endpoint.
- Save any response (including binary) to a file.
- Self-contained single-file executable — no installer, no admin rights, no runtime dependency.

[1.26.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.26.0
[1.25.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.25.0
[1.24.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.24.0
[1.23.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.23.0
[1.22.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.22.1
[1.22.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.22.0
[1.21.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.21.0
[1.20.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.20.0
[1.19.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.19.0
[1.18.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.18.0
[1.17.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.17.0
[1.16.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.16.0
[1.15.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.15.0
[1.14.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.14.1
[1.14.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.14.0
[1.13.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.13.1
[1.13.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.13.0
[1.12.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.12.0
[1.11.3]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.11.3
[1.11.2]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.11.2
[1.11.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.11.1
[1.11.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.11.0
[1.10.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.10.0
[1.9.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.9.0
[1.8.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.8.0
[1.7.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.7.0
[1.6.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.6.0
[1.5.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.5.0
[1.4.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.4.0
[1.3.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.3.0
[1.2.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.2.0
[1.1.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.1.0
[1.0.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.0.0
