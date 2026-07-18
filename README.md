<div align="center">

  <img alt="Certificate API Tester — client-certificate (mTLS) auth from the Windows store" src="docs/assets/banner.svg" width="820" />

  **A Windows desktop API tester that authenticates to endpoints with a client certificate from your Windows certificate store (mTLS) — and renders whatever the endpoint returns, even when you don't know its format.**

  [![License: MIT](https://img.shields.io/badge/License-MIT-63f2ab.svg)](LICENSE)
  [![Latest release](https://img.shields.io/github/v/release/Real-Fruit-Snacks/windows-cert-api-tester?color=6bdcff&label=release)](https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases)
  [![Platform: Windows 10/11](https://img.shields.io/badge/platform-Windows%2010%2F11-f0c674.svg)](#requirements)
  [![.NET 9](https://img.shields.io/badge/.NET-9.0-b78cff.svg)](https://dotnet.microsoft.com/)
  [![CI](https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/actions/workflows/ci.yml/badge.svg)](https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/actions/workflows/ci.yml)

  [Documentation](https://real-fruit-snacks.github.io/windows-cert-api-tester/) · [Handbook](wiki/README.md) · [Download](https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/latest) · [Report an issue](https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/issues)

</div>

---

## Overview

Some internal sites and APIs don't take a password — they ask your browser to "choose a certificate," then complete a mutual-TLS handshake with a client certificate issued to you and stored in your Windows certificate store. Testing those endpoints from a normal API client is awkward: most tools want the certificate and its private key as files on disk, which enterprise and smart-card certificates deliberately don't allow.

Certificate API Tester talks to those endpoints directly. You pick a certificate from your Windows store, compose a request, and send it — the operating system performs the signing during the TLS handshake, so the private key never has to leave the store (and never has to be exportable). Because you often only know the endpoint and not the shape of what comes back, the response viewer figures out the format for you and pretty-prints it. And when the target is a **web page** rather than an API, a *Rendered* tab opens it as a browser would — fetching every resource on the page through your certificate. Because the certificate is optional, it also doubles as a general-purpose API client for anything else.

It runs as a single self-contained `.exe` with no external dependencies — no installer, no admin rights, and no .NET runtime required on the machine. Copy the file and run it.

<div align="center">
  <img alt="Certificate API Tester screenshot" src="docs/assets/screenshot.png" width="900" />
</div>

## Features

- **Pick a client certificate from the Windows store** — lists certificates in `CurrentUser\My` (optionally `LocalMachine\My`) with subject, issuer, thumbprint, and expiry, flags the ones meant for client authentication, and has a filter box for finding one quickly. The private key is never exported; Windows signs the handshake, so smart-card and non-exportable certificates work.
- **Client certificate from a file** — not just the Windows store: load a `.pfx`/`.p12` (with an optional password) or a `.pem`/`.crt` (key in the same file or a separate one) with **From file…** on the certificate row, or headless with `--cert-file` / `--cert-password` / `--key-file` on `send`, `fuzz`, `serve`, and `mcp`. Handy when you're handed a cert that isn't imported into the store.
- **File uploads (multipart/form-data)** — in the app, switch the **Body** tab to **Form data (multipart)** and add fields (tick **File** to upload a file); headless, `certapi send -F "field=value" -F "file=@path"` (curl-style; `-F` implies POST and `name=@path;type=<ct>` sets a part's content type). Multipart requests save into collections and run in suites like any other.
- **Certificate optional — a general API tester too** — leave the picker on **"— no certificate —"** (the default) to send an ordinary request, so it works just as well against endpoints that don't require mutual TLS.
- **Full request builder** — method (GET/POST/PUT/PATCH/DELETE/HEAD/OPTIONS), URL, an enable/disable key-value **query-parameter grid** and **headers grid**, **Bearer/Basic auth** helpers, a request body with a **content-type selector**, a **timeout** field, and a **Cancel** button for in-flight requests.
- **Request tabs** — keep several requests open side by side, each with its own website, method, parameters, headers, body, auth, certificate, and response. Add a tab with `+` or **Ctrl+T**, close it with its `✕` or **Ctrl+W**; your open tabs are there again next time you launch.
- **Query parameters** — a dedicated **Params** tab with an enable/key/value grid. Paste a URL with a `?query` and it splits into the grid automatically; the grid is recombined (correctly encoded) onto the URL when you send.
- **Collections** — save named requests into folders and reopen them in a tab. Switch the sidebar between **History** and **Collections**; save the current request, organise it in folders, rename, or delete. Collections persist between sessions.
- **Known-good endpoints** — every saved request remembers its last result: send it and a dot appears next to its name (mint for a 2xx, red for a failure or error status), with a tooltip showing when it was last checked and what it returned. See at a glance which endpoints are verified working.
- **Data-driven runs** — `certapi run <path> --data users.csv` (or `.json`) repeats a request once per row of a dataset, with each row's columns available as `{{variables}}` — combine with assertions to table-test an endpoint across many inputs in one command.
- **Response assertions (tests)** — a **Tests** tab on any request: assert on the **status**, **response time**, a **header**, a **JSON body path**, or the **body text**, with `==` / `!=` / contains / matches / exists / absent / `<` / `>`. `certapi run` then passes a request only when its assertions all pass (a request with no assertions still passes on any 2xx), so a collection becomes a real pass/fail test suite — failed assertions are listed on stderr and in the JSON output.
- **Environments & variables** — define `{{variable}}` values per environment (Dev / Staging / Prod) and switch from the **ENV** selector in the title bar. Variables are substituted in the URL, query, headers, body, and auth when you send — stored requests keep the raw `{{tokens}}`, and any token with no value is flagged in the status line.
- **Capture & reuse auth tokens** — grab a value from a response (a JSON field like `access_token` or a response header) and save it into a `{{variable}}` automatically. Call your auth endpoint once, then send `Authorization: Bearer {{token}}` on every later request — no copy-paste. Works in the app (a **Capture** tab) and headless (`certapi send --capture token=access_token`).
- **Session cookies** — the app keeps a cookie jar for the session (like a browser), so a login's `Set-Cookie` is sent on later requests automatically; headless, add `--cookies` to `certapi run` to share a jar across a suite.
- **Automatic bearer tokens** — login once and follow-on requests to the same host carry the
  captured token automatically, in the GUI, `certapi send`/`run`, and the MCP server. Host-scoped,
  never overriding explicit auth; `--no-auto-token` / a status-bar toggle opt out. Captured tokens
  are stored in your workspace in plain text, like other secrets — treat exported workspaces as
  private.
- **Collection defaults** — collections remember their website + client certificate, so opening
  any endpoint is immediately sendable.
- **Endpoint discovery** — probe a wordlist against a website to map an undocumented API, in the
  app (**Discover…**) or headless (`certapi fuzz`). Discoveries open as tabs or save as a collection.
- **`--debug` / `--log-file`** — every certapi command can explain exactly what it sent, looked
  up, and negotiated, on screen or into a log file.
- **GraphQL** — `certapi send <url> --graphql "<query>" --gql-variables '{"id":1}'` posts a correctly-formed GraphQL request (JSON `{ query, variables }`), so you can hit GraphQL endpoints from the command line.
- **Import from cURL** — paste a `curl` command and it opens a ready-to-send tab with the method, URL, query, headers, body, and auth filled in (understands `-X`, `-H`, `-d`, `-u`, `-k`, Bearer headers, quoting, and line continuations).
- **Import OpenAPI / Swagger** — point it at a JSON OpenAPI 3.x or Swagger 2.0 file to generate a collection of requests, foldered by tag, with the server as each request's website.
- **Export as OpenAPI** — write the selected folder (or all collections) as an OpenAPI 3.0 JSON file: folders become tags, each saved request becomes an operation with its parameters, headers, and body example, and each known-good note becomes the operation description. Tokens and passwords are never written — auth is exported as a security scheme only.
- **Save / load workspaces** — export everything (open tabs, collections with their known-good results, environments, saved websites, history) to a single JSON file and load it back later, merging into or replacing the current workspace. Move between machines, keep named project snapshots, or hand a teammate a ready-to-use setup.
- **Headless mode (`certapi.exe`)** — the whole tester without the window: send one-off requests with a client certificate from the Windows store, run saved requests and whole collections as pass/fail suites (updating their known-good markers), list certificates, run the mTLS self-test, and import/export cURL, OpenAPI, and workspaces — all scriptable, with body-to-stdout output and meaningful exit codes.
- **Local mTLS gateway (`certapi serve`)** — run a loopback reverse proxy that forwards to a certificate-protected site: point any local app's base URL at `http://localhost:<port>` and it reaches the upstream with your Windows-store client certificate attached, no mTLS code of its own. Loopback-only, with an optional shared-secret token.
- **MCP server for AI agents (`certapi mcp`)** — expose certapi to an AI agent over the Model Context Protocol: it can send mTLS requests, list certificates, and list/run saved requests, all using a certificate you pin at launch and bounded by a host allowlist. The agent never handles certificates itself.
- **Rendered website view** — a **Rendered** response tab opens the current URL as a web page, fetching *every* resource (document, CSS, JS, images, XHR) with your selected client certificate — so a certificate-protected internal site renders fully, not just its HTML. It loads on demand and uses the Edge WebView2 runtime included with Windows 11.
- **A response viewer for unknown formats** — reads the `Content-Type` but doesn't trust it blindly: pretty-prints JSON and XML with **syntax highlighting**, shows HTML/text, and hex-dumps binary. When the content type is missing or misleading it *sniffs* the body (JSON → XML → text → binary). Pretty / Raw / Headers / Diagnostics views are always available.
- **Connection diagnostics** — see the negotiated **TLS version and cipher**, whether your client certificate was **actually presented** to the server, and the server's certificate (subject, issuer, thumbprint, expiry, and chain).
- **Network trace** — a **Network** tab, like a browser's network panel: every HTTP call is logged — the request you sent *and* every resource the Rendered view fetches — with method, status, type, size, timing, and a marker when it used your client certificate. Filter by text, status class (2xx–5xx, errors), or cert-only; click a row for a resizable details pane with its headers; right-click to copy the URL or a matching curl command.
- **Pop-out response views** — open a single response view *or the whole response panel (tabs and all)* in its own window: detach the panel to give the request editor the full main window, or pop just the Network trace or a Rendered page beside your work. Everything stays live — the trace keeps streaming, a Rendered page stays interactive — and closing a popped-out window puts its content back.
- **Saved websites** — save a base URL (e.g. `https://internal.corp`) and the URL box becomes just the path after it, so you can fire off `/api/thing`, `/api/other` without retyping the host.
- **Request history** — a sidebar of recent requests, labelled by path (with the host beneath). Click one to reload the *entire* request — website, certificate, headers, auth, timeout, and body — **and** the response it returned. The app also remembers your window, last certificate, and settings between runs.
- **Copy as code** — turn the current request into a snippet with **Copy as ▾**: cURL, PowerShell (`Invoke-RestMethod`), Python (`requests`), or C# (`HttpClient`) — variables resolved, headers and body included — to drop into a script or hand to a teammate.
- **Find in response** — a search box over the response body finds and selects the next match (Enter for next, wrapping around) — handy for locating a value in a large JSON payload.
- **Copy & export** — copy the response body, copy the request as code, and save any response (including binary) with a sensible file extension.
- **Clear failure messages** — distinguishes "server refused the certificate," "the server's own certificate isn't trusted," a network/DNS error, and a timeout.
- **Reach internal sites behind a private CA** — an explicit, off-by-default *Ignore server certificate errors* toggle (clearly labelled insecure).
- **Honors your proxy** — follows the machine's configured proxy, including "Automatically detect settings" (WPAD) and a "Use automatic configuration script" (PAC) from Internet Options, authenticating with your Windows credentials when required.
- **Built-in self-test** — a *Run Self-Test* button stands up a local mutual-TLS server on your own machine and proves the whole certificate-authentication path end to end, **no real endpoint required.**
- **Local test server** — a *Mock server…* button (and `certapi mock`) runs a standing server you can fire requests at: it echoes each request as JSON and serves `/status/<code>`, `/sse`, `/token`, and a WebSocket echo, over plain HTTP, HTTPS, or mutual TLS (it generates and writes out the certs). Point the app at itself to try every feature without a real API.
- **Built-in help** — a **?** in the title bar (or **F1**) opens a Help & Reference window that walks through every feature, lists the keyboard shortcuts, and shows an About panel. It's all embedded, so it works even with no web access.
- **OAuth 2.0 tokens** — a *Get OAuth 2.0 token…* button on the Auth tab runs the client-credentials, password, and refresh grants, plus the authorization-code grant with PKCE (opens your browser, catches the loopback redirect). The token is stored for the API's host and filled into the Bearer field. `certapi token` does the same headless. The token endpoint itself can be mTLS-protected.
- **Windows Integrated Auth (Negotiate/NTLM)** — for internal sites that use your Windows identity. A *Windows (integrated)* auth type signs in with your logged-in account (SSO) by default, or explicit `DOMAIN\user` + password. Headless: `certapi send --windows-auth` (or `--windows-user`/`--windows-password`). Kerberos or NTLM is negotiated automatically.
- **Session capture** — for sites you log into through a web page. A *Capture session…* button opens a browser (presenting your client certificate); you log in on the site itself, and it captures the resulting session cookies and any bearer token — scoped per website and attached automatically to later requests, in the app and headless via `certapi`. It can also save the API calls it saw as a ready-to-run collection. Your password is never seen or stored.
- **Live streaming (WebSocket & SSE)** — a *Stream* button opens a console that connects to a `ws://`/`wss://` endpoint (send messages, watch replies) or an `http(s)` `text/event-stream` endpoint (watch events arrive), reusing your selected client certificate. The `certapi ws` and `certapi sse` commands do the same headless.
- **Light or dark theme** — the Terminal Workbench palette ships in both. Toggle it from the sun/moon button in the title bar; your choice is remembered and applies to every window.
- **Keyboard-friendly and portable** — shortcuts for everything (below), a fully themed UI, and a single self-contained executable.

## Using it

### Send your first request
1. **Pick a certificate** in the CERTIFICATE row (or leave it on *"— no certificate —"* for a plain request). The filter box finds one quickly among many.
2. Choose a **method** and type a **URL** (a full `https://…`, or a path if you've saved a website).
3. Press **Send** (or **Ctrl+Enter**). The response appears below: **Pretty** highlights JSON/XML, **Raw** shows the exact bytes, **Headers** and **Diagnostics** (TLS version, cipher, whether your certificate was presented) round it out.

### Save a base URL (websites)
Type a base like `https://internal.corp` in the WEBSITE row and click the saved-websites arrow to keep it. The URL box then takes just the path (`/api/thing`), so you don't retype the host.

### Organise requests (collections)
Switch the sidebar to **COLLECTIONS**, then **Save current request…** to store it in a folder. Double-click a saved request to reopen it in a tab. Each saved request shows a **known-good dot** after you send it — mint for a 2xx, red for a failure — with a tooltip of when it was last checked.

### Discovering endpoints

No API docs? Probe candidate endpoints to see what exists. With no wordlist it uses a built-in
starter list, so this just works out of the box:

    certapi fuzz https://api.example.com --cert "CN=My Client"

For a thorough sweep, supply your own (larger) list with `-w` — the built-in one is only a quick
look. A copy of it ships as [`wordlists/common-api-endpoints.txt`](wordlists/common-api-endpoints.txt):

    certapi fuzz https://api.example.com -w my-endpoints.txt --cert "CN=My Client"

Each line is a path (or `METHOD path`); `#` comments and blanks are ignored. Anything but a 404 or
a connection error counts as a discovery. Add `--save-collection Discovered` to keep the hits, or
`--json` for machine output. In the app, use **Discover…** in the toolbar (with a **Use built-in
list** button).

<p align="center">
  <img alt="The Discover endpoints window, colour-coding each probe by outcome" src="docs/assets/shot-discover.svg" width="860" />
</p>

Results are colour-coded by outcome — **Found** (2xx), **Unauthorized** (401/403, it exists but
needs auth), **MethodNotAllowed** (405, it exists with a different method), **Redirect**,
**ServerError**, and **NotFound**. 404s and connection errors are hidden by default. A token
captured from an auth endpoint is reused automatically on later probes to the same host, so you can
log in first and then discover the endpoints that need it. The same run headless:

<p align="center">
  <img alt="certapi fuzz discovering endpoints from the command line" src="docs/assets/shot-fuzz.svg" width="820" />
</p>

### Testing responses

Turn a saved request into a real test with the **Tests** tab: add assertions on the response and
`certapi run` will pass the request only when they all hold.

- **Target:** Status · Time (ms) · a Header · a Body JSON path (e.g. `data.id`) · the Body text
- **Comparison:** `==` · `!=` · contains · matches (regex) · exists · absent · `<` · `>`

For example, assert `Status == 200`, `Body data.id exists`, and `Time < 500`. Run the suite:

    certapi run smoke-suite --json

A request with no assertions still passes on any 2xx (unchanged), so adding tests is opt-in per
request. Failed assertions are printed on stderr (`… assertion failed — Status == 200 (got 503)`)
and included in `--json`, and the app shows a `✓ tests 3/3 passed` summary with the detail in the
**Diagnostics** view.

### Environments & variables
Open the **ENV** selector (title bar) → **Edit** to define `{{variable}}` values per environment (Dev / Staging / Prod). Use `{{name}}` anywhere — URL, query, headers, body, or the auth fields — and it's substituted when you send. Switch environments to point the same requests at a different backend.

### Capture & reuse an auth token
Many APIs want you to log in first and then send the returned token on every call. Do it once and reuse it automatically:
1. Build the **login request** (e.g. `POST https://internal.corp/auth` with your credentials in the body).
2. Open its **Capture** tab → **+ Add capture**. Set **Variable** = `token`, **From** = `Body`, **Path** = `access_token` (use a dotted path like `data.access_token` for nested fields, or **From = Header** with a header name).
3. **Send** the login request. The status line shows `Captured token`, and the value is saved into your active environment (a `Captured` environment is created automatically if you don't have one selected). Captured values are stored with your environments (in the workspace/state file, in plain text), so treat exported workspaces as private.
4. In your other requests, set **Auth → Bearer** with the token `{{token}}` (or put `{{token}}` in any header). Send — the captured token is filled in. Re-run the login request anytime to refresh it.

### Import / export
- **Import ▾** (next to the tabs): paste a **cURL** command, or import an **OpenAPI/Swagger** JSON file to generate a whole collection.
- **Export**: write a collection as **OpenAPI** (from the collections sidebar), or **Export workspace** to move everything — tabs, collections, environments, history — to another machine.

### Headless (the `certapi` command-line tool)
`certapi.exe` (a separate download on the releases page) does everything without the window:

```powershell
# one-off request with a client certificate from the Windows store
certapi send https://internal.corp/api/orders --cert "CN=matt"

# log in and capture the token into a workspace, then reuse it
certapi send https://internal.corp/auth --cert "CN=matt" --capture token=access_token --workspace team.json
certapi send https://internal.corp/api/orders --workspace team.json --env Captured -H "Authorization: Bearer {{token}}"

# run saved requests as a pass/fail suite (exit code 1 if any fail)
certapi run "internal api" --env Prod
certapi run --all --json

# fetch an OAuth 2.0 token and store it for later sends to the API
certapi token --token-url https://auth.internal.corp/token --client-id app --client-secret s3cret \
    --scope "api.read" --save --for https://api.internal.corp
certapi send https://api.internal.corp/orders

# Windows Integrated Auth (Negotiate/NTLM) with your signed-in account
certapi send https://intranet.corp/api/me --windows-auth

# stream a WebSocket (send messages, print replies) or Server-Sent Events
certapi ws wss://internal.corp/socket --cert "CN=matt" -m '{"sub":"prices"}' --expect 3
certapi sse https://internal.corp/events --cert "CN=matt" --max-events 5 --json

# utilities and import/export
certapi certs --filter matt
certapi selftest
certapi import openapi .\spec.json --into imported
certapi export workspace -o team-setup.json

# run a local test server and fire requests at it (try --tls or --mtls too)
certapi mock --port 8770
certapi send http://127.0.0.1:8770/anything -X POST -d '{"hi":1}'
```

Saved requests, collections, and environments come from the app's own state automatically, or from any exported workspace file via `--workspace` — so it works on machines that have never opened the app. Run `certapi help <command>` for every option. Response bodies go to stdout and diagnostics to stderr, with script-friendly exit codes (0 success · 1 failure · 2 usage · 3 data).

### Local gateway (for apps that can't do client certificates)
Point any app's base URL at a local port and it reaches a certificate-protected site with your certificate attached:

```powershell
certapi serve https://internal.corp --port 8819 --cert "CN=matt"
# then the app calls http://localhost:8819/api/orders
```

Loopback only; add `--token <value>` to require a shared secret.

### MCP server (for AI agents)
Give an AI agent controlled use of your certificate over the Model Context Protocol. Configure your MCP host:

```json
{ "mcpServers": { "certapi": {
  "command": "certapi",
  "args": ["mcp", "--cert", "CN=matt", "--allow", "internal.corp"] } } }
```

The agent gets `send_request`, `list_certificates`, `list_saved`, `run_saved`, and `self_test` — always using the certificate you pinned, and only reaching hosts on the `--allow` list.

## Screenshots

**Render a certificate-protected website — every resource (HTML, CSS, JS, images, XHR) is fetched with your client certificate**

<div align="center">
  <img alt="Rendered website view" src="docs/assets/shot-rendered.png" width="860" />
</div>

**Organise saved requests into collections — with known-good markers — and switch between environments of `{{variables}}`**

<div align="center">
  <img alt="Collections sidebar" src="docs/assets/shot-collections.png" width="820" />
  <br/><br/>
  <img alt="Environments and variables" src="docs/assets/shot-environments.png" width="560" />
</div>

**A network trace — every request the page made, like a browser's network panel, each fetched through your certificate**

<div align="center">
  <img alt="Network trace tab" src="docs/assets/shot-network.png" width="860" />
</div>

**Built-in Help & Reference — every feature explained, embedded so it works with no web access**

<div align="center">
  <img alt="In-app Help window" src="docs/assets/shot-help.png" width="820" />
</div>

**Run it headless as a local gateway — an app points its base URL at the port and reaches a certificate-protected site with no mTLS code of its own**

<div align="center">
  <img alt="certapi serve local mTLS gateway forwarding requests" src="docs/assets/shot-gateway.svg" width="860" />
</div>

## Requirements

- **To run:** Windows 10 or 11 (64-bit). Nothing else — the released `.exe` bundles the .NET runtime.
- **To build:** the [.NET 9 SDK](https://dotnet.microsoft.com/download) on Windows.

## Download

Grab `ApiTester.App.exe` from the [latest release](https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/latest) and double-click it. There is no installer and it needs no admin rights — copy it wherever you like and run it. The command-line client `certapi.exe` is a separate asset on the same releases page.

## Quick start

1. **Pick a certificate** — or leave it on *"— no certificate —"* to send a plain request. Use the filter box to find one among many.
2. **Compose the request** — choose a method, type a URL, and (optionally) add headers, a body with its content type, or Bearer/Basic auth.
3. **Send** (or press **Ctrl+Enter**) and read the response. The **Pretty** tab highlights JSON/XML; **Diagnostics** shows the TLS and certificate details of the connection.

To sanity-check the certificate path with no real endpoint, click **Run Self-Test** — it runs a full mutual-TLS round-trip against a local server on your own machine.

### Endpoints to try

- **Mutual TLS:** import the test client certificate from [badssl.com/download](https://badssl.com/download/) (`badssl.com-client.p12`, password `badssl.com`) into `CurrentUser\My`, select it, and hit `https://client.badssl.com/` — it returns `200` with the cert and `400` without.
- **Server-cert toggle:** `https://self-signed.badssl.com/` fails as *ServerCertificateUntrusted* until you enable *Ignore server certificate errors*.
- **General (no cert):** `https://httpbin.org/anything`, `https://postman-echo.com/get`, `https://jsonplaceholder.typicode.com/todos/1`.
- **Formats:** `https://httpbin.org/xml`, `/html`, `/image/png` (binary → hex dump).

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+Enter` / `Enter` in the URL box | Send the request |
| `Esc` | Cancel an in-flight request |
| `Ctrl+L` | Focus the URL box |
| `Ctrl+S` | Save the response |
| `Ctrl+H` | Toggle the history sidebar |
| `Ctrl+T` | New request tab |
| `Ctrl+W` | Close the current tab |
| `F5` | Refresh the certificate list |
| `F1` | Open the in-app help |

## Build from source

```bash
git clone https://github.com/Real-Fruit-Snacks/windows-cert-api-tester.git
cd windows-cert-api-tester

dotnet build                                       # compile
dotnet test --filter "Category!=StoreRoundTrip"    # unit + mTLS integration tests
dotnet run --project src/ApiTester.App             # launch the app
```

Produce the portable single-file executable:

```bash
dotnet publish src/ApiTester.App -c Release -r win-x64 --self-contained -o publish
# -> publish/ApiTester.App.exe   (runs on any Windows 10/11 machine, no install)

dotnet publish src/ApiTester.Cli -c Release -r win-x64 --self-contained -o publish
# -> publish/certapi.exe         (the command-line client)
```

> The runtime-identifier and self-contained flags live on the publish command, not in the project file, so everyday `dotnet build` / `dotnet test` stay fast and framework-dependent.

## No external dependencies

- **Running it:** the released `ApiTester.App.exe` is a self-contained single file. Copy it to any Windows 10/11 machine and run it — no installer, no admin rights, and no pre-existing .NET runtime.
- **One optional exception:** the *Rendered* website view uses the Microsoft Edge **WebView2 runtime**, which ships with Windows 11 (and is a standard component on up-to-date Windows 10). It loads only when you open that tab; if the runtime is absent, the tab says so and everything else works unchanged.
- **Building it on your own CI:** the repository includes a [`.gitlab-ci.yml`](.gitlab-ci.yml) so a self-hosted GitLab instance can build, test, and package the executable on a Windows runner, and optionally publish this documentation site to GitLab Pages. Point NuGet at your own package mirror if you use one.

## How it works

- **Authentication is mutual TLS.** The app builds an `HttpClient` over a `SocketsHttpHandler` and attaches the certificate you picked. During the handshake the server requests a client certificate and the app presents yours. For non-exportable keys (enterprise CAs, smart cards) the signing is done by Windows CNG/CryptoAPI — the application never sees the raw private key.
- **The rendered view browses through your certificate.** The *Rendered* tab hosts a WebView2 and intercepts *every* resource it requests — the document, styles, scripts, images, and XHR — fetching each through the same client-certificate `HttpClient`, so an entire certificate-protected page renders authenticated, not just its first response.
- **The response is decoded defensively.** Content-type is a hint, not a guarantee, so the formatter validates before it trusts and sniffs when it can't.
- **Diagnostics are captured from the live connection.** For direct connections the app performs the TLS handshake itself so it can report the negotiated protocol/cipher and whether your client certificate was actually presented; the server certificate and chain are always captured.
- **Your proxy is respected.** Requests follow the machine's configured proxy (WPAD/PAC from Internet Options) and authenticate to it with your Windows credentials when required.
- **The self-test is real.** It generates an in-memory CA plus a server and client certificate, runs a `TcpListener` + `SslStream` server that *requires* a client certificate, and drives a real request through the same code path the app uses for live endpoints.

## Project layout

```
windows-cert-api-tester/
├── src/
│   ├── ApiTester.Core/     Engine — cert store access, mTLS client, response formatting, self-test
│   └── ApiTester.App/      WPF desktop UI (a thin layer over Core)
├── tests/
│   └── ApiTester.Tests/    Unit tests + an end-to-end mutual-TLS integration test
├── .github/workflows/      Build/test CI and the release pipeline
├── .gitlab-ci.yml          Self-hosted GitLab build + Pages
└── docs/                   Documentation site and artwork
```

The engine (`ApiTester.Core`) has no UI dependency, so every behaviour is covered by tests without touching the window.

## Security

- Client certificates are **never exported**; the live `X509Certificate2` is handed to the networking layer and Windows performs the signing.
- *Ignore server certificate errors* is **off by default** and clearly labelled insecure — turn it on only for internal sites whose server certificate you trust.
- The app makes no network calls other than the requests you send. There is no telemetry. Window and request settings are stored locally under `%AppData%\CertApiTester`.

## License

Released under the [MIT License](LICENSE).
