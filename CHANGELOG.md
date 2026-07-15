# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[1.6.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.6.0
[1.5.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.5.0
[1.4.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.4.0
[1.3.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.3.0
[1.2.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.2.0
[1.1.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.1.0
[1.0.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.0.0
