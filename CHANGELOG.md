# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
