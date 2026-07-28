# 21. CLI Reference (`certapi`)

Complete reference for the CLI (command-line interface) client. The built-in help is authoritative — run
`certapi help` for the overview or `certapi help <command>` for a command's full options.

```
Usage: certapi <command> [options]
```

## Commands

| Command | Purpose |
|---|---|
| [`send <url>`](#send) | Send a one-off request |
| [`token`](#token) | Fetch an OAuth 2.0 access token (and optionally save it) |
| [`run <path>`](#run) | Run saved requests from your collections (or `--all`) |
| [`fuzz <base-url>`](#fuzz) | Discover endpoints from a wordlist |
| [`bench <url>`](#bench) | Measure an endpoint's latency under load |
| [`sse <url>`](#sse) | Stream Server-Sent Events (SSE) |
| [`ws <url>`](#ws) | Open a WebSocket, send messages, print what arrives |
| [`certs`](#certs) | List client certificates |
| [`selftest`](#selftest) | Prove the mTLS (mutual Transport Layer Security) path end-to-end against a loopback server |
| [`mock`](#mock) | Run a local test server to fire requests at |
| [`import`](#import) | Import a cURL command or an OpenAPI file |
| [`export`](#export) | Export collections as OpenAPI, or the whole workspace |
| [`serve <upstream>`](#serve) | Run a local mTLS gateway that forwards to an upstream |
| [`grpc`](#grpc) | Discover and call a gRPC service (unary, server-, client-, or bidirectional-streaming) |
| [`mcp`](#mcp) | Run an MCP (Model Context Protocol) server so AI (artificial intelligence) agents can make mTLS calls |
| `help [command]` | Show help |

## Global options

Work on every command, anywhere on the line:

- **`--debug`** — rich diagnostics on stderr: resolved URLs (Uniform Resource Locators), headers
  (Authorization masked),
  certificate lookup, TLS details, timings, full stack traces.
- **`--log-file <path>`** — append everything (diagnostics + all stderr) to a log file.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Transport/request failure (or a failed `run`, or `send --fail` on 4xx/5xx) |
| `2` | Usage error (bad options) |
| `3` | Data error (missing file, bad workspace, unresolvable input) |

Response bodies go to **stdout**; metadata, notes, and errors go to **stderr** — so you can pipe the
body cleanly.

---

## send

`certapi send <url> [options]` — send a single request.

**Request**

- `-X, --method <m>` — HTTP (Hypertext Transfer Protocol) method (default GET)
- `-H, --header "k: v"` — add a header (repeatable)
- `-d, --data <body>` — request body (`--data-file <file>` reads it from disk)
- `-F, --form name=value` — `multipart/form-data` field; `name=@path` uploads a file
  (`;type=<ct>` sets its type). Repeatable; implies POST. Excludes `-d`.
- `--graphql <query>` / `--gql-variables <json>` — a GraphQL `{query, variables}` POST
- `--content-type <ct>` — body content type (default `application/json`)
- `--timeout <seconds>` — default 100

**Auth**

- `--bearer <token>` — `Authorization: Bearer …`
- `--basic <user:pass>` — `Authorization: Basic …`
- `--windows-auth` — Windows Integrated Auth with your signed-in account (aliases `--ntlm`,
  `--negotiate`)
- `--windows-user <DOMAIN\user>` / `--windows-password <p>` — explicit Windows credentials
- `--no-auto-token` — disable automatic attach/reuse of the captured bearer token **and** the
  captured session cookies (see [Session Capture](26-Session-Capture.md)) for this request

**TLS / certificates**

- `--cert <thumb|subject>` — client certificate from the Windows store
- `--store <location>` — `CurrentUser` (default); `LocalMachine` searches both
- `--cert-file <path>` / `--cert-password <pw>` / `--key-file <path>` — certificate from a file
- `--insecure` — ignore server-certificate errors

**Variables & capture**

- `--env <name>` — environment for `{{variables}}`; `--var k=v` overrides (repeatable)
- `--workspace <file>` — load environments from a workspace file
- `--strict-vars` — unresolved `{{tokens}}` become an error
- `--capture var=path` — save a response value into a variable (`header:Name` for a header)

**Testing**

- `--assert "<expr>"` — check the response and exit 1 if it fails (repeatable). Expression syntax:
  `status == 200`, `status < 300`, `time < 500`, `header <name> contains <v>`,
  `body <jsonpath> exists`, `body-text matches <regex>`. Operators: `==` `!=` `contains` `matches`
  `exists` `!exists` `<` `>`. Lets a single `certapi send` be a CI (continuous integration) smoke
  test without a saved workspace — the same checks the GUI's Tests tab and `certapi run` apply to
  saved requests.

**Diff**

- `--diff <baseline>` — compare the response against a baseline and print what changed. The baseline
  is an HTTP Archive (HAR) `.har` file, a `.json` response file (either the envelope `--json` writes
  or a saved response snapshot), or the literal `known-good` — the stored response of the saved
  request matching this method and URL.
- `--diff-fail` — exit 1 when anything differs (the CI form). Without it the diff is printed and the
  exit code is unchanged.
- `--diff-ignore <path>` — a JSON path to overlook, e.g. `data.timestamp` (repeatable; a trailing `*`
  on a segment matches by prefix, and naming a container suppresses everything beneath it)
- `--diff-ignore-header <name>` — a header to overlook **on top of** the volatile defaults (`Date`,
  `Set-Cookie`, `ETag`, `Age`, `X-Request-Id`, `X-Correlation-Id`, `Server-Timing`), not instead of
  them (repeatable)

Both bodies JSON gives a structural comparison that names each changed path (`data.items[0].id`) as
added, removed, changed, or type-changed, arrays compared by index; a non-JSON body falls back to a
one-line summary of lines and bytes; a binary body reports size and equality only. The diff goes to
stderr even under `-q`, and `--json` gains a `diff` object. A missing or unreadable baseline is exit
3, as is `known-good` with no recorded snapshot (it names the request to send once). The diff
modifiers need `--diff`, and `--diff` can't be combined with `--all-ips` (exit 2 for either).

**Proxy** (shared with [`run`](#run) and [`fuzz`](#fuzz))

- `--proxy <url>` — send through a specific proxy instead of the machine's configured one.
- `--no-proxy` — skip the proxy entirely for this request. Restores the TLS version, cipher, and
  client-certificate-presented diagnostics a proxy hides, since none of them are visible through
  one.
- `--proxy-user <user:pass>` — credentials for the proxy named by `--proxy`, or the machine's, when
  it requires authentication.
- `--noproxy <list>` — a comma-separated bypass list that narrows whichever proxy is in play (the
  machine's, or the one named by `--proxy`) rather than replacing it: a bare hostname
  (`internal.corp`) matches that host and its subdomains but not a look-alike (`notinternal.corp`);
  a leading-dot or wildcard suffix (`.corp`, `*.corp`) matches the domain and its subdomains and not
  a domain that merely contains it (`internal.corp.evil.com`); an IP address literal (`10.1.2.3`,
  `::1`) matches as an address, never as a suffix; a Classless Inter-Domain Routing (CIDR) range
  (`10.0.0.0/8`, `fd00::/8`) is accepted, but one written with host bits set (`10.0.0.5/8`) is
  refused rather than quietly masked; and `*` alone bypasses everything. Matching is
  case-insensitive, and an optional `:port` (`internal.corp:8443`, `[::1]:8443`, `*:8080`) must also
  match when present. An entry that can't be parsed is refused with a message naming it (exit 2),
  and combining `--noproxy` with `--no-proxy` is exit 2 too — there is no proxy left to narrow. When
  `--noproxy` isn't given, the `NO_PROXY` environment variable (falling back to `no_proxy`) is
  honored instead; precedence is an explicit `--noproxy`, then a saved request's own bypass list,
  then `NO_PROXY`.

**Revocation** (shared with [`run`](#run), [`fuzz`](#fuzz), [`bench`](#bench), and [`serve`](#serve);
also accepted by [`grpc`](#grpc))

- `--revocation none|offline|online` — check whether the server's certificate has been revoked by its
  issuer (default `none`, matching every release before this one, so nothing changes unless you opt
  in). `offline` consults cached certificate revocation lists (CRLs) only and never reaches the
  network; `online` may fetch a fresh CRL or query an Online Certificate Status Protocol (OCSP)
  responder.
- `--revocation-strict` — treat an undeterminable revocation status as fatal instead of merely
  reported. Requires `--revocation offline` or `--revocation online`; passing it with checking off
  (the default) is a usage error (exit 2), since there is no unknown status for it to make fatal.

A certificate the issuer has revoked is refused even past a pin from `certapi trust add` — revocation
is the issuer's later word against the pin's earlier one, and it wins; under the default `none` this
never triggers, and `--revocation-strict` is likewise not rescued by a pin. The outcome
(checked-and-good, revoked, unknown, or not checked) is always reported: `--debug` prints a
`revocation …` term on both the transport line and the connection line, and `--json` carries
`revocationMode` and `revocationStatus`. `--insecure` still overrides a revoked or unknown result, the
same as it always overrode "not trusted" — but now the diagnostics say so rather than implying a clean
check happened.

**Retries** (shared with [`run`](#run) and [`fuzz`](#fuzz))

- `--retry <n>` — retry a failed request up to n times (default `0`, off). A negative count is exit 2.
- `--retry-on <codes>` — comma-separated statuses that earn a retry (default `429,502,503,504`). A
  token that isn't a status code between 100 and 599 is exit 2 rather than a silently dropped typo.
- `--retry-delay <ms>` — the first backoff delay (default `500`). It doubles on each further attempt
  with ±10% jitter, capped at 30 seconds; a `Retry-After` header (delta seconds or an HTTP-date)
  overrides the computed wait.
- `--retry-unsafe` — also retry POST and PATCH. Only GET, HEAD, OPTIONS, PUT, and DELETE are retried
  by default, because re-sending a POST nobody confirmed can charge a card twice.
- `--no-retry-transport` — don't retry a request that never reached the server. By default a refused
  or reset connection, a name-resolution failure, a proxy failure, and a timeout each earn a retry; a
  refused or untrusted certificate never does (it would only fail slower), and neither does a
  redirect loop.

Cancellation is honored during the backoff wait. The stderr metadata line reports `N attempts` when
more than one was needed, and `--json` carries `attempts` when it is above 1.

**Output**

- `-o, --output <file>` — write the body to a file
- `--include` — print status line + headers before the body
- `--pretty` — pretty-print the body
- `--json` — a JSON (JavaScript Object Notation) result envelope instead of the raw body
- `--fail` — exit 1 on HTTP status ≥ 400
- `-q, --quiet` — no metadata line on stderr

---

## token

`certapi token [options]` — fetch an OAuth 2.0 token.

- `--grant client_credentials|password|refresh|device` (default client_credentials)
- `--token-url <url>` **(required)**, `--client-id`, `--client-secret`
- `--grant device` + `--device-url <url>` — the RFC 8628 device-code flow: prints a verification
  URL and code, then polls the token endpoint until you approve in a browser anywhere (another
  machine included); Ctrl+C abandons it. Made for headless boxes and SSH sessions.
- `--client-auth body|basic` — send client creds in the body (default) or a Basic header
- `--scope "<a b c>"`, `--username`/`--password` (password grant), `--refresh-token` (refresh grant)
- `--param k=v` — extra form parameter (repeatable)
- `--save` + `--for <api-url>` (repeatable) — store the token for that API (application programming
  interface) origin so later `send`
  attaches it; `--workspace <file>` to save into a workspace file
- `--json` — full result; `-q` quiet
- TLS/cert flags apply (the token endpoint itself may be mTLS); a pinned endpoint needs no
  `--insecure`, and the same `--proxy`/`--noproxy` and `--revocation` flags as [`send`](#send)
  apply to the fetch

The interactive **authorization-code** grant is app-only (see [Authentication](08-Authentication.md)).

---

## run

`certapi run <Collection[/Folder][/Request]> [options]`, `certapi run --all [options]`, or
`certapi run --chain <name> [options]`.

- `--all` — run every saved request in the workspace
- `--chain <name>` — run a saved chain: its requests, in the order the chain names them, as one unit,
  so a token captured by one step is usable by the next through its `{{variable}}` (see
  [Capturing Values](12-Capturing-Values.md)). Steps report PASS / FAIL; a failing step stops the
  chain unless it is marked to carry on, and the steps that never ran are listed as SKIP. Any failed
  step exits 1. Captures write into the environment the chain names (created on first use); an
  explicit `--env` wins over it. The same chain can also be run from the desktop app's CHAINS sidebar
  (see [Capturing Values](12-Capturing-Values.md)).
- `--diff-har <file.har>` — replay a HAR archive and compare each response against the one it
  recorded, which turns a captured session into a regression test. An entry passes only when its
  diff is identical (the status is part of the diff), and any difference exits 1 — there is no
  `--diff-fail` here, because diffing the capture is the whole point of the flag.
  `--diff-ignore <path>` and `--diff-ignore-header <name>` work as they do on [`send`](#send).
- `--workspace <file>` — collections from a workspace file (default: live GUI (graphical user
  interface) state)
- `--env <name>` / `--var k=v` — variables
- `--data <file>` — data-driven: repeat once per CSV (comma-separated values)/JSON row (see
  [Data-Driven Runs](13-Data-Driven-Runs.md))
- `--record` / `--no-record` — write known-good results back (default: on for live state, off for
  workspace files; skipped while the GUI is running)
- `--strict-vars` — unresolved `{{tokens}}` fail the request
- `--no-auto-token` — don't attach captured session tokens **or** captured session cookies
- `--cookies` — keep a per-run cookie jar so a login's `Set-Cookie` carries across the run (this is
  separate from captured cookies from [Session Capture](26-Session-Capture.md), which attach
  automatically)
- `--json` — JSON results instead of the table

A request passes when its assertions pass, or on any 2xx if it has none. Exit 1 if any request fails.

The [retry flags](#send) apply here too; a saved request keeps its own retry settings unless a flag
overrides them, and a flag overrides only what it names. `--chain` names its own requests, so it can't
be combined with `--all`, a positional, `--diff-har`, or `--data` (exit 2). An unknown chain is exit 3
and lists the chains that do exist; a step whose saved request has been deleted is exit 3 naming the
step, before anything is sent.

---

## fuzz

`certapi fuzz <base-url> [-w <wordlist>] [options]` — see [Endpoint Discovery](14-Endpoint-Discovery.md).

- `-w, --wordlist <file|->` — paths to probe (omit for the built-in list; `-` reads stdin)
- `-X, --methods <list>` — comma-separated methods (default GET)
- `--concurrency <n>` (1–50, default 8), `--delay <ms>`, `--timeout <seconds>`
- `--match <codes>` / `--hide <codes>` / `--all` — control the view
- `-H`, `--bearer`, `--env`/`--var`, `--no-auto-token`, cert flags, `--insecure`
- `--save-collection <name>`, `-o <file>`, `--json`, `-q`

---

## bench

`certapi bench <url> [options]` or `certapi bench <Collection[/Folder]/Request> [options]` — send one
request over and over and report how long it took. It uses the same client-certificate send path as
`send`, so what it measures is what the rest of the tool does. A saved-request positional must name
exactly one request.

**Load**

- `-n, --count <n>` — total requests to send (default 100)
- `-c, --concurrency <n>` — parallel workers (default 10; never more than `--count`)
- `--duration <seconds>` — run for a wall-clock period instead of a fixed count; `-n` is then unused,
  and the two can't be combined
- `--warmup <seconds>` — send for this long first and discard every result. Warm-up requests are
  **extra** — they don't come out of `--count`.
- `--bench-retries` — let the [retry flags](#send) apply during the bench (off by default)

**Request, TLS, and variables**

- `-X, --method <m>`, `-H, --header "k: v"`, `-d, --data <body>`, `--content-type <ct>`,
  `--bearer <token>`, `--timeout <seconds>`
- cert flags + `--insecure`; the [revocation flags](#send) (`--revocation`, `--revocation-strict`)
  apply too; `--env <name>` / `--var k=v` / `--workspace <file>`

On a saved request these override or add to what it already carries; everything else about it (its own
auth, headers, body, and transport settings) is used as saved. A multipart request can't be benched.

**Output**

- `--json` — a JSON envelope instead of the summary table

The report gives requests sent / succeeded / failed, elapsed, requests per second, the min / p50 / p90
/ p99 / max latencies, and the status and error counts. Percentiles come from the full retained
latency array, not an approximation.

| Behaviour | Why |
|---|---|
| Retries are forced off, even when the request or `--retry` asks for them | A retry turns a failure into a slow success and hides the failure rate a bench exists to measure. `--bench-retries` measures it anyway. |
| Nothing is written — no known-good markers, no captured tokens, no state file | A measurement isn't an observation worth keeping. The workspace is read for `{{variables}}`, saved requests, and pinned certificates only, and captured session tokens are not attached. |
| **Connections are pooled and reused**, so only the first request to an origin pays the TCP connect and the TLS handshake | Later requests that share the same client certificate and trust policy reuse that connection and measure only the request and response. `--warmup` discards the first-connection cost so the figures describe a warmed-up endpoint. A request routed through a proxy still opens its own connection every time — the proxied path can't be pooled. The command prints this as a note under every summary and carries it in the `--json` envelope. |

Exit codes: `0` whenever the bench measured anything — it reports numbers rather than passing
judgement, so an endpoint that answers 503 or 404 every time has still been measured and exits 0 ·
`1` only when no request got a response at all (every attempt failed at the transport level, so
there is nothing to report but that the endpoint could not be reached) · `2` usage (`-n` with
`--duration`, a concurrency of zero or less, or a concurrency greater than the count) · `3` data
error.

There is no window for the bench: it is a command-line concern.

---

## sse

`certapi sse <url> [options]` — stream Server-Sent Events.

- `-H "k: v"`, `--max-events <n>`, `--json` (ndjson — newline-delimited JSON), `-q`
- cert flags + `--insecure`; a pinned host needs no `--insecure`, a captured token attaches
  automatically (`--no-auto-token` turns that off, `--workspace <file>` names where pins and
  tokens come from), and the same `--proxy`/`--noproxy`/`--revocation` flags as [`send`](#send)
  apply

## ws

`certapi ws <url> [options]` — WebSocket console.

- `-m, --message <text>` (repeatable; stdin lines also sent), `--expect <n>`, `-H`, `-q`
- cert flags + `--insecure`; the same pin/token/proxy/revocation story as [`sse`](#sse) above

---

## certs

`certapi certs [--filter <text>] [--store CurrentUser|LocalMachine] [--json]` — list client
certificates. `--store LocalMachine` also searches the machine store.

## selftest

`certapi selftest [--json]` — stand up a loopback mTLS server with generated certificates and prove
the client-certificate path end to end.

## mock

`certapi mock [options]` — a standing local test server (see [Mock Server](18-Mock-Server.md)).

- `--port <n>` (default 8770; 0 picks free), `--http` (default) / `--tls` / `--mtls`,
  `--cert-dir <dir>`, `-q`

## import / export

- `certapi import curl "<curl command>" [--into <folder>] [--workspace <file>]`
- `certapi import openapi <file> [--into <folder>] [--workspace <file>]`
- `certapi import har <file> [--into <folder>] [--workspace <file>]`
- `certapi import postman <file> [--into <folder>] [--workspace <file>]` — a Postman Collection
  (v2.0/v2.1 export): folders, both URL forms, query rows, headers (disabled stays disabled),
  raw/urlencoded/formdata bodies, bearer/basic/apikey auth with request-level beating folder- and
  collection-level, and collection variables as an environment (Postman's "secret" type stays
  secret here). `{{variables}}` share syntax and import unchanged; anything unsupported is a named
  warning, never a silent drop (see [Import and Export](17-Import-and-Export.md))
- `certapi export openapi [<folder>] -o <file> [--workspace <file>]`
- `certapi export workspace -o <file> [--workspace <file>] [--include-secrets]` — secrets (captured
  tokens/cookies, saved auth values, secret variables) are stripped by default; `--include-secrets`
  keeps them, written encrypted for the current Windows user.

## serve

`certapi serve <upstream> --port <n> [options]` — local mTLS gateway (see
[Local Gateway](19-Local-Gateway.md)).

- An upstream pinned with [`trust add`](#trust) is reachable without `--insecure` — the gateway
  consults the same pins every other connection does
- `--upstream <prefix>=<url>` — mount another upstream at a path prefix behind the same port
  (repeatable; longest prefix wins; the positional `<upstream>` is the `/` fallback)
- `--token <value>` — require callers to send this bearer token before anything is forwarded
- `--tls` — serve the gateway itself over HTTPS with a generated certificate;
  `--tls-trust` also installs that certificate so a browser accepts it, and `--tls-untrust`
  removes a previously installed one and exits
- `--browser` — the bundle: all four browser accommodations at once (`--cors`,
  `--rewrite-cookies`, `--rewrite-location`, `--allow-upgrade`), each also available alone
- `--cors [<origins>]` — answer CORS preflights at the gateway, restricted to a comma-separated
  origin list if given
- `--cors-max-age <seconds>` — how long a browser may cache a preflight answer (default 600); only
  with `--cors`
- `--request-header "Name: value"` / `--remove-request-header <name>` — set or strip a header on
  the request before it reaches the upstream (repeatable)
- `--response-header "Name: value"` / `--remove-response-header <name>` — set or strip a header on
  the response before it reaches the caller (repeatable; applied after `--browser`'s own rewrites)
- `--revocation none|offline|online` / `--revocation-strict` — the [same revocation
  checking](#send) as `send`, enforced on the gateway's connection to the upstream (default `none`;
  see [Local Gateway](19-Local-Gateway.md))

## grpc

```
certapi grpc list <address> [options]
certapi grpc list --protoset <file> [options]
certapi grpc call <address> <Service/Method> [options]
```

Calls a gRPC service (HTTP/2) that requires a client certificate, using the same Windows-store
certificate handling as the rest of certapi. `list` shows the services and methods a server
advertises via server reflection (`grpc.reflection.v1alpha.ServerReflection`) — or, for a server that
doesn't implement reflection, the services and methods declared by a compiled descriptor set supplied
with `--protoset` (below); `call` invokes one — unary, server-streaming, client-streaming, or
bidirectional — with the kind discovered from the method's own definition, never chosen with a flag.
A short service name resolves when it's unambiguous (`Echo/Unary` finds `certapi.test.Echo`).

- `-d, --data <json>` — the request message as JSON (default `{}`); repeatable — for a
  client-streaming or bidirectional method, each `-d` value and each line read from standard input
  (one JSON object per line) is sent as one message, in order; standard input is read only for those
  two kinds. `--data-file <path>` reads one message from a file instead, the same as a single `-d`.
  No messages at all (no `-d`, no `--data-file`, empty or absent standard input) is legal for a
  client-streaming or bidirectional method — it sends an empty stream, not an error
- `--protoset <file>` — read services and methods from a compiled `FileDescriptorSet` instead of
  server reflection. Produce one with `protoc --descriptor_set_out=<file> --include_imports <proto>`
  — `--include_imports` isn't optional, or the set is missing the types it imports. Wins over server
  reflection when both are available (the two are never merged); `grpc list --protoset` needs no
  address and opens no connection at all
- `-H, --header "k: v"` — request metadata (repeatable)
- `--max-messages <n>` — stop a server-streaming or bidirectional call after n messages (exit 0, not
  a failure)
- `--timeout <seconds>` — default 100
- cert flags (see [`send`](#send)) + `--insecure` — a host pinned with `certapi trust add` needs
  no `--insecure`, exactly as `send`
- `--proxy <url>` / `--no-proxy` / `--proxy-user <u:pass>` / `--noproxy <list>` — apply to the
  channel; HTTP-version pinning, redirects, decompression, and retries do not apply to a gRPC
  channel and have no flags here
- `--revocation none|offline|online` / `--revocation-strict` — the [same revocation
  checking](#send) as `send`, enforced on the gRPC channel's TLS connection (default `none`)
- `--no-auto-token` — don't attach a captured bearer token as metadata for this call (one is
  attached automatically otherwise; `certapi grpc` never captures a *new* token)
- `--workspace <file>` — load pins and tokens from a workspace file instead of the live state
- `--json` — a JSON envelope instead of the plain rendering; `-q, --quiet` — no metadata line on
  stderr

`list` prints services to stdout, one per line, indented with their methods (`stream` marks a
streaming request or response); `call` prints the response — or, for a server-streaming or
bidirectional method, one compact JSON object per line as each message arrives — to stdout.

Exit codes: `0` on success (including a stream stopped early by `--max-messages`) · `1` when the
gRPC status returned is not OK (`--json` carries `{status, statusName, detail}`) · `2` on a bad
command line — an address whose scheme isn't `http`/`https`, a malformed `Service/Method`, or several
`-d`/`--data` values against a method that takes a single request message · `3` on a data problem —
server reflection unavailable, an unknown service/method/field naming the offending one, or a
`--protoset` file that's missing, unreadable, not a compiled `FileDescriptorSet`, or missing part of
its dependency closure (e.g. `protoc` was run without `--include_imports`).

Server reflection is the default source of descriptors; `--protoset` is the alternative for a server
that doesn't offer it, and wins over reflection when both are available. The well-known Protocol
Buffers (Protobuf) types render — and are accepted on the way in — in their canonical JSON forms
rather than as ordinary messages: `Timestamp` is an RFC 3339 string (`"2023-11-14T22:13:20Z"`),
`Duration` is seconds with an `s` suffix (`"1.500s"`), the wrapper types (`Int32Value`,
`StringValue`, and the rest) are the bare underlying value, `Struct`/`Value`/`ListValue` are a plain
JSON object/value/array, `FieldMask` is comma-joined lowerCamelCase paths, and `Empty` is `{}`. `Any`
expands to `{"@type":…, …fields}` when its type resolves against whichever descriptors are in play —
reflection-fetched or supplied with `--protoset` — and degrades to `{"@type":…,"value":"<base64>"}`
rather than failing the call when it doesn't. `certapi serve` does not proxy gRPC — `HttpListener` is
HTTP/1.1-only — so `certapi grpc` reaches the service directly with your certificate rather than
going through the gateway.

## mcp

`certapi mcp [options]` — MCP server for AI agents (see [MCP Server](20-MCP-Server.md)).

- Tools: `send_request`, `run_saved`, `run_chain`, `list_saved`, `list_environments`,
  `list_certificates`, `grpc_list`, `grpc_call`, `self_test`; saved requests, environments, and
  chains are also published as read-only resources with secrets redacted
- `--allow <host>` — allowed upstream host (repeatable), enforced on every call including each
  chain step; omit to allow any host (prints a warning)
- `--protoset <file>` — descriptor set for the gRPC tools, pinned at launch
- The same `--proxy`/`--noproxy`, `--revocation`/`--revocation-strict`, and `--retry` flags as
  [`send`](#send) apply to every call the tools make; redirects are never followed, a host pinned
  with `trust add` needs no `--insecure`, and the workspace is read once and never written back

---

`certapi --version` prints the version.

Next: [Keyboard Shortcuts](22-Keyboard-Shortcuts.md).
