# Auto token reuse, collection defaults, and CLI polish — design

Date: 2026-07-17
Status: approved

## Goal

Make the mTLS API tester feel like a finished professional product in three areas:

1. **Automatic auth-token reuse** — a token returned by one request is captured and
   used by follow-on requests to the same host, with zero setup, in the GUI, the
   CLI, and the MCP server.
2. **Sticky website/cert in the GUI** — opening an endpoint from a collection never
   forces re-selecting the website and client certificate.
3. **CLI polish** — verbose examples in every help screen, plus `--debug` and
   `--log-file` diagnostics on every command.

Existing capture rules, `{{variables}}`, environments, and workspaces are unchanged;
everything below is additive.

## 1. Token engine (ApiTester.Core)

New `TokenService` with three responsibilities:

**Detect.** After every delivered response, scan for a bearer token:

- JSON bodies (content type json, valid parse): the first of `access_token`,
  `id_token`, `token`, `accessToken`, `jwt` — checked at the top level, then one
  level down under `data` or `result`. String values only; empty/whitespace ignored.
- Response headers: `X-Auth-Token`, then `X-Access-Token`.
- `token_type` is honored when present (only `Bearer`, case-insensitive, or absent
  qualifies — a `mac`-type token is not captured). `expires_in` (seconds, number or
  numeric string) sets the stored expiry.

**Store.** `AppState` gains `List<SessionToken>`:

```
SessionToken { Origin, Token, Source, CapturedUtc, ExpiresUtc? }
```

- `Origin` is scheme + host + port (the token's scope). One token per origin —
  newest replaces.
- `Source` records where it came from (`access_token` field / `X-Auth-Token`
  header + the request path), for display in GUI and debug output.
- Persisted with the rest of the state file / workspace, same as auth secrets
  already are today.

**Apply.** Given a request whose auth mode is `Auto` and a target URL:

- Exact-origin match against the store; expired tokens are skipped (surfaced as
  "token for <host> expired" in status/debug output).
- On match, add `Authorization: Bearer <token>` (never overriding an explicit
  Authorization header).
- Tokens never apply across origins. Cross-host redirects already drop the
  header (HttpClient default).

**Auth modes.** `RequestModel.AuthType` gains `Auto`:

- `Auto` — use the stored token for the target origin when one exists (new default
  for new tabs and requests).
- `None` — never send auth (explicit opt-out).
- `Bearer` / `Basic` — unchanged.
- Migration: requests loaded from existing state/workspaces with `None` become
  `Auto` (legacy `None` was "nothing configured", not "never send").
- Global toggle: `AppState.AutoTokens` (default true) disables apply everywhere;
  detect/store still runs so turning it back on works immediately.

## 2. Surfaces

**GUI.**

- Every response is scanned; captures update the store silently.
- Status-bar chip shows `Token: <host> · expires in 58m` when the active tab's
  target origin has a live token; click opens a small popup with the full origin,
  source, age, expiry, and buttons: Clear this token, Clear all, and the global
  auto-token on/off toggle.
- Auth tab gets the `Auto` option (new first/default entry) and keeps None,
  Bearer, Basic.
- Sends with `Auto` attach the token silently; the network trace / headers view
  shows the Authorization header that was sent (masked to the first and last 4
  characters).

**CLI (`send`, `run`).**

- Auto-capture after every delivered response; auto-apply when the request has no
  explicit auth (`send` with no `--bearer`/`--basic`/`Authorization` header; `run`
  requests whose auth is Auto).
- One-line stderr notes (suppressed by `--quiet`):
  `note: captured bearer token for api.example.com (access_token, expires in 60 min)` and
  `note: using captured token for api.example.com`.
- `--no-auto-token` disables both apply and capture for that invocation.
- Store writes follow the existing live-state rules: blocked with the usual note
  while the GUI is running; `--workspace` writes go to the workspace file.
- Precedence within one `run`: a token captured by request N applies to request
  N+1 onward (same origin), even before any state save.

**MCP server.**

- The request tool auto-captures and auto-applies with identical origin scoping,
  so agent chains (login → call API) work without token plumbing. Notes appear in
  the tool result metadata.

## 3. GUI collection stickiness

Opening a collection endpoint (double-click) fills only the blanks:

1. Website/cert saved on the request itself always win.
2. Otherwise the nearest ancestor folder/collection default wins.
3. Otherwise the active tab's current website/cert are inherited.

Collection defaults:

- `CollectionNode` gains `DefaultBaseUrl` and `DefaultCertThumbprint` (folders
  only; nearest ancestor with a value wins).
- Right-click menu on a folder/collection: "Set website & certificate…" opens a
  dialog (website text box with saved-websites dropdown, cert picker, Clear).
- Auto-remember: the first successful send of a collection-linked request whose
  root collection has no default stores that website+cert on the root collection,
  with a status-bar note ("Remembered website and certificate for <collection>").

## 4. CLI polish

**Examples in help.** Every command help gets an `Examples:` section with
realistic, copy-pasteable commands (auth-capture chains, cert selection by
subject and thumbprint, workspaces, piping to files/jq, JSON output, gateway and
MCP setups). The overview usage gets a short quick-start block. Verbose is fine;
formatting matches the existing help style (aligned, comment lines under
commands where useful).

**`--debug` (global flag, all commands).** Rich diagnostics to stderr:

- resolved URL and the headers actually sent (Authorization masked),
- certificate lookup detail (query → store searched → match subject/thumbprint),
- TLS detail from the existing ConnectionInfo (protocol, client cert presented),
- timing, response status/size,
- token engine decisions (captured / applied / skipped-expired / origin mismatch),
- full exception chains with stack traces instead of the one-line `error:` form.

**`--log-file <path>` (global flag, all commands).** Appends timestamped lines
(`2026-07-17T14:03:22Z [debug] …`) covering the same events plus the normal
notes/warnings, regardless of `--debug`. Failures to open/write the log are a
one-line stderr warning, never fatal.

Implementation: both flags are parsed centrally in `CliApp` before dispatch and
carried on `CliServices` as a small `CliLog` sink (debug writer + optional file
writer). Commands and the token/cert paths log through it. Exit codes and all
existing output contracts are unchanged.

## Error handling

- Token detection never throws: malformed JSON, non-string fields, and huge
  bodies (scan cap ~2 MB) simply skip detection.
- Expired token + Auto auth: request goes out with no auth; note explains why.
- Missing cert for a collection default: same behavior as a request cert that
  disappeared — cert combo falls back to "no certificate" and the status bar
  says the stored cert wasn't found.
- `--log-file` IO errors: warn once, continue without the file sink.

## Testing

Following the existing in-process test patterns (`CliApp.Run` with fake
`CliServices`, loopback mTLS server):

- TokenService detect: each field/header, precedence, `token_type` filtering,
  `expires_in`, nested `data`/`result`, malformed JSON, binary bodies.
- Store/apply: origin scoping (host and port differences), expiry, newest-wins,
  never-override-explicit-auth, `AutoTokens` off.
- CLI: capture note + follow-on apply across two `send` invocations against the
  loopback server; `run` intra-suite reuse; `--no-auto-token`; GUI-running guard;
  `--workspace` isolation.
- Auth migration: legacy `None` loads as `Auto`; explicit new `None` round-trips.
- Collection defaults: blank-fill precedence (request > ancestor > active tab),
  auto-remember on first success only.
- Debug/log: `--debug` prints stack traces; `--log-file` writes and appends;
  unwritable path warns but exits with the normal code.
- Help: every command help contains an `Examples:` section (guard test).
