# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.53.0] - 2026-07-26

### Added
- **HAR → OpenAPI** — `certapi export openapi --from-har session.har -o api.json` turns a captured
  HTTP Archive (HAR) session into an OpenAPI 3.0 document, the payoff for mapping an undocumented
  internal API: `fuzz` finds the paths, a HAR capture records what a real client actually sent, and
  this turns the recording into a specification. Repeated calls to the same endpoint collapse into
  one operation — headers common to every instance are kept (varying ones dropped), the first
  non-empty request body becomes the body example, and query keys seen across instances become query
  parameters. Path templating is deliberately conservative — a wrong `{param}` is worse than a
  literal path — so a segment becomes `{id}` only when it is all digits, a Universally Unique
  Identifier (UUID), or a hexadecimal string 16 characters or longer, *and* it actually varies between
  calls; one that happens to carry the same value in every entry stays literal. Responses of 400 and
  above are skipped, and a header whose value is exactly `[redacted]` (as `--har`'s default redaction
  writes it) is never exported — the existing OpenAPI-export rule still applies too: auth becomes a
  security scheme only, never the secret value. `--host <h>` keeps only one host's entries, and
  `--no-template-ids` keeps identifier-looking segments literal. In the app, Import ▾ → **Export
  OpenAPI from HAR file…** does the same from an `.har` file picker.
- **Mock from HAR** — `certapi mock --har session.har` serves a captured session back as a fake
  backend instead of the built-in echo routes, so an application, a test suite, or a teammate's client
  can develop against a recorded API offline. Matching precedence is exact method + path + query
  first, then method + path, then `--no-match-status` (default 404) for anything that matches
  neither; repeated calls to one route replay in recorded order and then repeat the last one.
  Hop-by-hop and framing headers (`Transfer-Encoding`, `Connection`, `Content-Length`) are never
  replayed, and a recorded `Set-Cookie` replays exactly as captured — a redacted capture replays
  `[redacted]`, which is correct and visible rather than silently wrong. The built-in `/`,
  `/status/<code>`, `/sse`, `/token`, `/windows-auth`, and `/cookie-auth` routes are not served while
  replaying. In the app, the Mock server window's **From HAR…** button starts the mock in replay mode
  the same way.
- **`serve --tls`** — closes the limitation the 1.51.0 gateway shipped with. `--tls` serves the
  gateway itself over HTTPS on `127.0.0.1`, using a generated `CN=127.0.0.1` certificate (a Subject
  Alternative Name (SAN) covering `127.0.0.1` and `localhost`, cached under
  `%AppData%\CertApiTester\gateway-tls\` and reused across runs) bound to the port through the
  Windows HTTP Server API — the same mechanism `netsh http add sslcert` configures. Binding is
  machine state, so nothing happens silently: the first bind needs an elevated (Run as administrator)
  prompt, and without one `serve --tls` exits 2 printing the exact `netsh` command to run; a port
  already bound to someone else's certificate is left alone and reported by its thumbprint rather
  than clobbered. `--tls-trust` additionally installs the certificate into `CurrentUser\Root` so the
  browser accepts it silently — explicit, logged, and reversible with `--tls-untrust`. Without
  `--tls-trust` the browser just warns once, and the startup banner says which situation applies.
  With `--tls` on, the `--browser` cookie rewriter stops stripping `Secure` from `Set-Cookie`, stops
  downgrading `SameSite=None` to `Lax`, and emits no `__Host-`/`__Secure-` warning — the known
  limitation recorded in the 1.51.0 section below, that those cookies could never survive a plaintext
  `http://127.0.0.1` origin, is now closed.

## [1.52.0] - 2026-07-26

### Added
- **HTTP Archive (HAR) capture** — `--har <file>` on `certapi send`, `run`, and `fuzz` records every
  request the command performs as a well-formed HAR archive, written once at the end of the run —
  including each redirect hop as its own entry, so a chain that changed origin is visible in the
  file, not just on screen. Secret redaction is on by default: the *values* of `Authorization`,
  `Proxy-Authorization`, `Cookie`, and `Set-Cookie` are written as `[redacted]`, so a trace is safe
  to hand to a teammate or attach to a bug report; `--har-include-secrets` opts out when you need the
  real values. In the app, Import ▾ → "Export Network trace as HAR…" writes the current tab's
  Network trace to a `.har` file with the same redact-by-default choice.
- **HAR replay, through mutual TLS** — `certapi import har <file>` turns a captured archive into a
  collection alongside `import curl`/`import openapi`, and `certapi run <file.har>` detects a `.har`
  positional directly and replays its entries as an ordered suite. The point of replay is what a
  browser's own HAR export can never do: the request is resent with a client certificate attached
  (`--cert`/`--cert-file`), so a session captured in a browser can be re-proven over mutual TLS
  without hand-rebuilding it. A HAR run never writes live state — no known-good markers, no
  captured tokens. A malformed HAR file is a one-line data error (exit 3); a well-formed HAR with
  zero entries is also exit 3, since there is nothing to replay. In the app, Import ▾ → "HAR
  file…" does the same import.
- **Per-site server-certificate trust** — a narrower alternative to the blanket "ignore server cert
  errors" toggle: `certapi trust add <host> [--thumbprint <t> | --from-url <https-url>]` pins one
  specific server-certificate thumbprint to one host, `certapi trust list` shows the pins, and
  `certapi trust remove <host> [--thumbprint <t>]` un-pins one or all of them. `--from-url` connects
  once, captures the certificate the server actually presents, and pins that thumbprint without you
  having to find it by hand. `send` and `run` consult the store automatically and note on stderr
  when a pinned certificate is what let the request through. In the app, a
  `ServerCertificateUntrusted` response offers a "Trust & retry" action, and a Trusted-certificates
  manager (Import ▾ → "Trusted certificates…") lists and removes pins. The existing blanket
  ignore-errors switch is unchanged and still available for cases pinning doesn't fit.

## [1.51.0] - 2026-07-25

### Added
- **Several upstreams behind one gateway port** — `--upstream /api=https://api.internal` mounts a host
  at a path prefix and repeats as often as you need, so a single `certapi serve` can stand in for a
  whole backend: `GET /api/orders` reaches `https://api.internal/orders`, with the prefix stripped
  before the request is forwarded. The longest matching prefix wins, a path under no prefix is a 404
  that contacts nothing, and two upstreams mounted on the same prefix is a usage error rather than a
  silent last-one-wins. The positional `certapi serve <upstream>` form still works — it is the same
  thing written `/=<url>`.
- **A gateway a browser can call — `certapi serve --browser`** — until now the gateway was usable only
  from an application whose base URL you control, because a faithful relay hands a browser exactly the
  headers that make it refuse the response. `--browser` turns on the whole bundle below at once, and
  every part of it also works on its own. None of it is on by default: without these flags the
  gateway is the same byte-faithful relay it has always been, adding and rewriting nothing.
  - `--cors` answers Cross-Origin Resource Sharing (CORS) preflights at the gateway instead of
    forwarding them upstream, and adds the response headers a browser insists on before it will let a
    script read the reply. With no value it echoes the caller's own `Origin`; given a comma-separated
    list it allows only those origins and refuses the rest with 403. Every `Access-Control-*` header
    the upstream sent is stripped before the gateway adds its own, because two allow-origin values
    are a hard browser failure rather than something it warns about.
  - `--rewrite-cookies` re-emits each `Set-Cookie` without `Domain=` and without `Secure`, and turns
    `SameSite=None` into `Lax`, so the browser will actually store the cookie against the gateway.
    `HttpOnly`, `Path`, `Max-Age`, and `Expires` pass through untouched.
  - `--rewrite-location` points a 3xx `Location` aimed at the upstream back at the gateway, so the
    browser stays on the local origin. One aimed anywhere else is relayed exactly as written and
    logged as a warning: that hop leaves the gateway, and your client certificate with it.
  - `--allow-upgrade` relays WebSocket connections through to the upstream with your client
    certificate attached, passing `Sec-WebSocket-Protocol` on so subprotocol negotiation still works.
- **Known limitation of `--rewrite-cookies`** — a cookie named `__Host-…` or `__Secure-…` requires the
  `Secure` attribute by specification, and no browser accepts `Secure` on a plaintext
  `http://127.0.0.1` origin, so such a cookie cannot be made to work through the gateway however it is
  rewritten. It is still passed on to the browser and named in a warning rather than dropped behind
  your back. Serving the gateway over HTTPS — a future `serve --tls` — is the real fix, and it does
  not exist yet.
- **Transport control on the gateway** — `certapi serve` now takes the transport flags the other
  commands gained in 1.50.0. `--proxy` / `--proxy-user` / `--no-proxy` and `--resolve` change how the
  gateway reaches an upstream; the redirect, decompression, and HTTP-version flags are accepted but do
  not apply, because the gateway always hands back the 3xx and the exact bytes the upstream sent.

## [1.50.0] - 2026-07-25

### Added
- **Proxy control** — send through a proxy you name instead of whatever the machine happens to be
  configured to use: `--proxy http://proxy.corp:8080` on `send`, `run`, and `fuzz` (with
  `--proxy-user user:pass` when it authenticates), or `--no-proxy` to ignore the configured proxy
  entirely, including an automatically detected or script-configured one. In the app, a new
  **Transport** tab on the request editor sets the same per request, and the Diagnostics view now
  names the proxy that was actually used.
- **Visible redirects** — 3xx hops are followed by the client itself and reported, so you can see
  where a request really ended up: `--show-redirects` prints the chain, `--json` includes it, and the
  app lists each hop as a row in the Network trace. `--no-redirect` stops at the 3xx,
  `--max-redirs <n>` changes the limit (default 20), and exceeding it is a clear "too many redirects"
  error carrying the hops collected so far. Each hop flags the two things that matter when a client
  certificate is in play — the `Authorization` header being dropped on a cross-origin hop, and a
  downgrade from `https` to `http` — because a redirect to another host means your certificate is
  about to be presented somewhere you didn't choose.
- **Pin a hostname to an address** — `--resolve <host>:<port>:<ip>` (repeatable) dials the address you
  give while still sending the original hostname in Server Name Indication (SNI) and the `Host`
  header, so you can test one node behind a load balancer, or verify a Domain Name System (DNS)
  cutover before it happens. `certapi send --all-ips` does the sweep for you: one send per address the
  host resolves to, with a per-address table of status, elapsed time, and body length. Pinning needs a
  direct connection, so `--resolve` together with an active proxy is a usage error rather than a
  silent no-op.
- **HTTP version pinning** — `--http1.1` or `--http2` forces the Hypertext Transfer Protocol (HTTP)
  version instead of negotiating it, which settles "is it the protocol or the endpoint?" quickly.
  Also available per request in the app's Transport tab.
- **The real reason a connection failed** — a transport error now carries the underlying exception
  chain and socket error code instead of collapsing to a single line. That chain is where the
  SChannel status for a refused client certificate actually lives, and it was previously discarded.
  `--debug` prints it, `--json` gains a structured `error` object (kind, message, detail), and the
  app's Diagnostics panel renders it. Exit codes are unchanged.

### Changed
- **Compressed responses are decoded automatically** — the client now sends `Accept-Encoding` and
  decompresses the response, matching every other HTTP client. This is a behavior change: if you
  relied on receiving the raw compressed bytes — to relay them byte-for-byte, say — add
  `--no-decompress` to get the previous behavior back.
- **A proxied connection still reports no handshake detail** — driving the handshake by hand would
  break proxy tunnelling, so the Transport Layer Security (TLS) version, the cipher, and whether your
  client certificate was presented are all blank whenever a proxy is in play. That was already true;
  what's new is the way out — `--no-proxy` bypasses the proxy *and* restores the full connection
  diagnostics.

## [1.49.0] - 2026-07-18

### Added
- **Ad-hoc assertions on `certapi send`** — `--assert "<expr>"` (repeatable) checks the response and
  exits 1 if it fails, so a single `send` is a CI smoke test without a saved workspace. Expressions:
  `status == 200`, `status < 300`, `time < 500`, `header <name> contains <v>`, `body <jsonpath> exists`,
  `body-text matches <regex>`; operators `== != contains matches exists !exists < >`. Reuses the same
  evaluator behind the GUI Tests tab and `certapi run`.

## [1.48.0] - 2026-07-18

### Changed
- **Professional starter wordlist** — the built-in endpoint list for `certapi fuzz` and the Discover
  window is now a curated ~270-entry list organized by category — operational/health, version & build,
  metrics (including Spring Actuator), API roots & versioning, API docs & specs, authentication &
  identity, users/accounts, admin, access control, common business resources, search, files/media,
  configuration, jobs/events, logs/audit, debug/dev, commonly-exposed sensitive paths
  (`.env`, `.git/config`, backups, …), and framework-specific paths — up from ~47. Key POST-only
  endpoints (login, token, register, graphql, …) are method-pinned. It's still a fast first pass; bring
  your own larger list with `-w <file>` for a thorough sweep.

## [1.47.2] - 2026-07-18

### Fixed
- **Maximize button glyph** — the title-bar maximize/restore button went blank after the window was
  first maximized or restored (its glyph was cleared on the window-state change). It now shows the
  correct maximize and restore icons.

## [1.47.1] - 2026-07-18

### Fixed
- **Session capture — address scheme** — a URL typed into the capture browser without a scheme is no
  longer forced to `https://`. Loopback/`localhost` hosts default to `http://`, so a local mock or dev
  server is reachable; other hosts still default to `https://`.
- **Session capture — visible errors** — a failed resource fetch in the capture browser now shows the
  error in the panel instead of leaving a blank page, and the window only auto-navigates to a genuine
  absolute URL.

### Changed
- **Mock route listing** — the in-app Help now lists the `/cookie-auth` route alongside the others
  (the README, Pages site, and handbook were already updated).

## [1.47.0] - 2026-07-18

### Added
- **Session capture** — a *Capture session…* button (next to *Mock server…*) opens a browser you can
  log in through; it presents your client certificate on every request, captures the resulting session
  **cookies** (including HttpOnly) and any **bearer token**, and scopes them per website. Captured
  sessions attach automatically to later requests — in the app and headless via `certapi send` /
  `certapi run`. Your password is typed into the site's own form and is never seen or stored. The
  finish step can also save the API calls observed during login as a ready-to-run collection.
- **Session chip controls** — the status-bar chip now shows captured cookies as well as tokens, with
  menu items to clear cookies for a site and to toggle *Automatically use captured cookies*.
- **Mock `/cookie-auth` route** — the local test server sets a session cookie and then reports the
  request as authenticated once the cookie comes back, so session capture can be exercised end to end.

## [1.46.1] - 2026-07-18

### Fixed
- **Theme toggle repaints the response** — switching themes now re-highlights whatever the Pretty view
  is showing. Previously the self-test detail (and anything else written outside a normal response)
  kept the old palette's colors — nearly unreadable pale-on-white after a switch to light.
- **Mock route list includes `/windows-auth`** — the route existed but was missing from the GUI mock
  console's routes line, the in-app Help, `certapi mock --help`, and the startup routes message.
- **"stopped" no longer shows in green** — the mock console's status line is muted while stopped and
  accent-green only while the server is live.

### Changed
- **Consistent window chrome** — every secondary window now uses the same title treatment (the accent
  `❯` glyph + bold title) and the same borderless caption close button. Previously the Mock server,
  Live stream, and OAuth windows had a small boxed close button, and the Environments, Discover, and
  pop-out windows were missing the brand glyph.

## [1.46.0] - 2026-07-18

### Added
- **Windows Integrated Authentication (Negotiate/NTLM)** — for internal sites that authenticate with
  your Windows identity. A new *Windows (integrated)* auth type uses your signed-in account for single
  sign-on by default, or explicit `DOMAIN\user` + password. Headless: `certapi send --windows-auth`
  (aliases `--ntlm` / `--negotiate`), or `--windows-user DOMAIN\user --windows-password …`. Saved
  requests carry it through `certapi run`. The handler negotiates Kerberos or NTLM automatically.
- **Mock `/windows-auth` route** — the local test server now emulates a Windows-auth-protected endpoint
  (challenges with `401 WWW-Authenticate: NTLM`, then accepts the handshake), so you can try your
  Windows-auth setup end to end against `certapi mock`.

## [1.45.0] - 2026-07-18

### Fixed
- **Assertion regex is bounded** — a `Matches` assertion runs a user-supplied pattern against the
  whole response body, so it now has a 2-second match timeout: a catastrophic-backtracking pattern
  fails the assertion instead of hanging `certapi run` or the GUI Tests tab.

### Changed
- **Releases include the full source** — each release now attaches `certapi-source-<tag>.zip`, a
  clean archive of the entire repository at that tag (all tracked files, no build output).

## [1.44.0] - 2026-07-18

### Added
- **Mock server in the app** — a *Mock server…* button (next to Run Self-Test) opens a console that
  starts and stops the local test server over plain HTTP, HTTPS, or mutual TLS, shows its base URL and
  routes, and logs each request as it arrives. For the TLS modes it generates the certificates and
  offers an *Open certs* shortcut; *Copy URL* drops the address straight into a request. Same server
  as `certapi mock` — now discoverable in the GUI.

## [1.43.0] - 2026-07-18

### Added
- **`certapi mock` — a standing local test server** to fire requests at (the persistent counterpart
  to `selftest`). It echoes each request back as JSON (method, path, query, headers, body, and, under
  mTLS, the client certificate you presented) and serves fixed routes: `/status/<code>`, `/sse`
  (a `text/event-stream`), `/token` (an OAuth 2.0 token), and a WebSocket echo on any path. Runs over
  plain HTTP (`--http`, default), HTTPS (`--tls`), or mutual TLS (`--mtls`, which requires and accepts
  any client certificate); `--tls`/`--mtls` write the generated server cert and a ready-to-use client
  `.pfx` to `--cert-dir`. So the app can be pointed at itself — `certapi send`, `sse`, `ws`, and
  `token` all work against it end to end.

## [1.42.0] - 2026-07-18

### Fixed
- **Stream console no longer leaks a socket per connection** — each WebSocket session is now disposed
  when it ends (and its cancellation source reused cleanly), so repeated connect/disconnect cycles
  don't accumulate `ClientWebSocket` handles.
- **`certapi token --save --workspace <file>`** now creates the workspace file when it doesn't exist
  yet, instead of warning and skipping the save.
- **`certapi ws --expect 0`** returns after sending instead of waiting for a reply that isn't coming.
- **OAuth token requests** report a clean "timed out" instead of surfacing a raw cancellation when the
  token endpoint is slow.

## [1.41.0] - 2026-07-18

### Added
- **OAuth 2.0 token acquisition** — fetch access tokens without leaving the app. A *Get OAuth 2.0
  token…* button on the Auth tab runs the client-credentials, password, and refresh-token grants
  directly, and the authorization-code grant interactively (opens your browser, catches the redirect
  on a loopback port, with PKCE). The token is stored for the API's host (so Auto auth attaches it)
  and dropped into the Bearer field. The token endpoint itself can require mTLS.
- **`certapi token`** — the same headless: `certapi token --token-url <url> --client-id … --client-secret …`
  for client-credentials (or `--grant password|refresh`), with `--save --for <api-url>` to store the
  token for later `certapi send` calls, `--client-auth basic`, `--scope`, `--param k=v`, and `--json`.

## [1.40.0] - 2026-07-18

### Fixed
- **Light theme now applies everywhere** — colours that were hardcoded for the dark palette and
  didn't follow a theme switch now do: the primary-button hover/pressed states, selected-row and
  secondary-button highlights, JSON/XML syntax highlighting (deeper, readable hues on white), the
  endpoint-discovery result colours, and the rendered-view error page. Toggling the theme also
  re-highlights the currently shown response.

### Changed
- **Stream console shows the live mode** — as you type the URL, the header indicates whether it will
  open a WebSocket (ws/wss) or stream Server-Sent Events (http/https).

## [1.39.0] - 2026-07-18

### Added
- **Live streaming (WebSocket & Server-Sent Events)** — a new *Stream* button on the request line
  opens a console that connects to a `ws://`/`wss://` endpoint (send messages, watch replies) or an
  `http(s)` `text/event-stream` endpoint (watch events arrive), using the selected client certificate
  and the insecure toggle. Two matching CLI commands: `certapi ws <url>` (send `--message`/stdin
  lines, print replies, `--expect <n>` for scripts) and `certapi sse <url>` (`--max-events`, `--json`
  ndjson output). Both honour `--cert`/`--cert-file`, `--insecure`, and custom `-H` headers.

## [1.38.0] - 2026-07-18

### Added
- **Light theme** — the Terminal Workbench palette now ships in light as well as dark. A sun/moon
  button in the title bar toggles between them; the choice is saved and restored on the next launch,
  and it applies live to the whole app (including the native window caption) without a restart.

## [1.37.0] - 2026-07-18

### Added
- **GraphQL** — `certapi send <url> --graphql "<query>" --gql-variables '{"id":1}'` posts a
  correctly-formed GraphQL request (JSON `{ query, variables }`, application/json, POST), with the
  query escaped and the variables validated.

## [1.36.0] - 2026-07-18

### Added
- **Find in response** — a search box above the response finds and selects the next match in the
  body (Enter for next, wrapping around), so you can locate a value in a large payload quickly.

## [1.35.0] - 2026-07-18

### Added
- **Session cookies** — the app now keeps a cookie jar for the session (like a browser), so a
  `Set-Cookie` in any response is sent back on later requests to that host; cookie-based logins
  carry across sends. Headless, add `--cookies` to `certapi run` to share a jar across a suite.

## [1.34.0] - 2026-07-18

### Added
- **Data-driven runs** — `certapi run <path> --data <file.csv|.json>` repeats the request(s) once
  per row of a dataset, each row's columns overriding `{{variables}}`. Results are labelled
  `[row N]`; combine with assertions to table-test an endpoint across many inputs in one command.

## [1.33.0] - 2026-07-18

### Added
- **Multipart body editor in the app** — the **Body** tab now has a **Form data (multipart)** mode:
  add fields, tick **File** to upload a file (with a file picker), and send. Multipart requests save
  into collections and run in suites like any other, so `certapi run` can exercise upload endpoints
  too. (The `certapi send -F` command line shipped in 1.31.0.)

## [1.32.0] - 2026-07-18

### Added
- **Copy as code** — the response toolbar's cURL button is now **Copy as ▾**: turn the current
  request into a snippet as cURL, PowerShell (`Invoke-RestMethod`), Python (`requests`), or C#
  (`HttpClient`), with `{{variables}}` resolved and headers and body included.

## [1.31.0] - 2026-07-18

### Added
- **File uploads (multipart/form-data)** — `certapi send -F "field=value" -F "file=@path"` posts a
  multipart form and uploads files (curl-style: `-F` is repeatable, implies POST, and
  `name=@path;type=<ct>` sets a part's content type; mutually exclusive with `-d`). Note: the CLI
  supports this today; a multipart editor in the app's Body tab is planned.

## [1.30.0] - 2026-07-18

### Added
- **Client certificate from a file** — use a certificate that isn't in the Windows store: load a
  `.pfx`/`.p12` (with an optional password) or a `.pem`/`.crt` (key in the same file or a separate
  one). In the app, **From file…** on the certificate row loads it for the session; headless, use
  `--cert-file` with `--cert-password` / `--key-file` on `send`, `fuzz`, `serve`, and `mcp`.

### Fixed
- The in-app Help was updated for the current UI: the request now has six tabs (Params, Headers,
  Body, Auth, Capture, Tests) and the command-line section documents `certapi fuzz`.

## [1.29.0] - 2026-07-18

### Added
- **Response assertions (tests)** — a **Tests** tab on any request lets you assert on the response:
  the **status**, **response time**, a **header**, a **JSON body path**, or the **body text**, with
  `==` / `!=` / contains / matches (regex) / exists / absent / `<` / `>`. `certapi run` now passes a
  request only when its assertions all pass — a request with no assertions still passes on any 2xx,
  so a collection becomes a real pass/fail test suite. Failed assertions are listed on stderr and in
  the `--json` output; the app shows a `✓ tests 3/3 passed` summary with the detail in Diagnostics.

## [1.28.1] - 2026-07-18

### Changed
- Clearer feedback while a request is in flight: the **Send** button now reads "Sending…", a slim
  indeterminate progress bar appears across the top of the response area, and the response views
  show "Waiting for response…" — so a previous response can't be mistaken for the new one.

## [1.28.0] - 2026-07-18

### Added
- **Built-in endpoint wordlist** — `certapi fuzz <base-url>` now works with no `-w`: it falls back
  to a built-in starter list of common endpoints (health, version, auth, admin, users, …), and the
  Discover window gains a **Use built-in list** button. Supply your own list with `-w` for a
  thorough sweep — the built-in list is only a quick look. The starter list also ships as
  `wordlists/common-api-endpoints.txt` and as a release asset.

### Fixed
- Endpoint discovery no longer drops a query string on a wordlist entry (e.g. `/search?q=1` used to
  be probed as `/search`).
- Saving discovered endpoints to a collection no longer reverts unsaved top-level collection changes
  made earlier in the same session.

## [1.27.0] - 2026-07-18

### Added
- **Endpoint discovery (fuzzing)** — point the tool at a website and a wordlist of candidate
  endpoints and it probes each one to show which exist. In the app, the new **Discover…** window
  streams colour-coded results (Found / Unauthorized / MethodNotAllowed / …), hides 404s and
  errors by default, opens any hit in a request tab on double-click, and can save all discoveries
  as a collection. Headless: `certapi fuzz <base-url> -w <wordlist>` with `--methods`,
  `--concurrency`, `--delay`, status `--match`/`--hide`/`--all`, `--json`, `-o`, `-w -` (stdin),
  and `--save-collection`. Captured auth tokens are attached automatically, so you can log in
  first and then discover authenticated endpoints. A starter wordlist ships in
  `wordlists/common-api-endpoints.txt`.

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

[Unreleased]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.53.0...HEAD
[1.53.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.52.0...v1.53.0
[1.52.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.51.0...v1.52.0
[1.51.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.50.0...v1.51.0
[1.50.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.49.0...v1.50.0
[1.49.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.48.0...v1.49.0
[1.48.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.47.2...v1.48.0
[1.47.2]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.47.1...v1.47.2
[1.47.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.47.0...v1.47.1
[1.47.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.46.1...v1.47.0
[1.46.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.46.0...v1.46.1
[1.46.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.45.0...v1.46.0
[1.45.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.44.0...v1.45.0
[1.44.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.43.0...v1.44.0
[1.43.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.42.0...v1.43.0
[1.42.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.41.0...v1.42.0
[1.41.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.40.0...v1.41.0
[1.40.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.39.0...v1.40.0
[1.39.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.38.0...v1.39.0
[1.38.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.37.0...v1.38.0
[1.37.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.36.0...v1.37.0
[1.36.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.35.0...v1.36.0
[1.35.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.34.0...v1.35.0
[1.34.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.33.0...v1.34.0
[1.33.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.32.0...v1.33.0
[1.32.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.31.0...v1.32.0
[1.31.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.30.0...v1.31.0
[1.30.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.29.0...v1.30.0
[1.29.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.28.1...v1.29.0
[1.28.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.28.0...v1.28.1
[1.28.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.27.0...v1.28.0
[1.27.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.26.0...v1.27.0
[1.26.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.25.0...v1.26.0
[1.25.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.24.0...v1.25.0
[1.24.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.23.0...v1.24.0
[1.23.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.22.1...v1.23.0
[1.22.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.22.0...v1.22.1
[1.22.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.21.0...v1.22.0
[1.21.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.20.0...v1.21.0
[1.20.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.19.0...v1.20.0
[1.19.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.18.0...v1.19.0
[1.18.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.17.0...v1.18.0
[1.17.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.16.0...v1.17.0
[1.16.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.15.0...v1.16.0
[1.15.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.14.1...v1.15.0
[1.14.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.14.0...v1.14.1
[1.14.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.13.1...v1.14.0
[1.13.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.13.0...v1.13.1
[1.13.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.12.0...v1.13.0
[1.12.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.11.3...v1.12.0
[1.11.3]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.11.2...v1.11.3
[1.11.2]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.11.1...v1.11.2
[1.11.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.11.0...v1.11.1
[1.11.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.10.0...v1.11.0
[1.10.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.9.0...v1.10.0
[1.9.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.8.0...v1.9.0
[1.8.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.7.0...v1.8.0
[1.7.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.6.0...v1.7.0
[1.6.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.5.0...v1.6.0
[1.5.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.4.0...v1.5.0
[1.4.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.0.0
