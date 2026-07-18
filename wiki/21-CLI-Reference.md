# 21. CLI Reference (`certapi`)

Complete reference for the command-line client. The built-in help is authoritative — run
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
| [`sse <url>`](#sse) | Stream Server-Sent Events |
| [`ws <url>`](#ws) | Open a WebSocket, send messages, print what arrives |
| [`certs`](#certs) | List client certificates |
| [`selftest`](#selftest) | Prove the mTLS path end-to-end against a loopback server |
| [`mock`](#mock) | Run a local test server to fire requests at |
| [`import`](#import) | Import a cURL command or an OpenAPI file |
| [`export`](#export) | Export collections as OpenAPI, or the whole workspace |
| [`serve <upstream>`](#serve) | Run a local mTLS gateway that forwards to an upstream |
| [`mcp`](#mcp) | Run an MCP server so AI agents can make mTLS calls |
| `help [command]` | Show help |

## Global options

Work on every command, anywhere on the line:

- **`--debug`** — rich diagnostics on stderr: resolved URLs, headers (Authorization masked),
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

- `-X, --method <m>` — HTTP method (default GET)
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
- `--no-auto-token` — disable automatic bearer-token capture/reuse

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

**Output**

- `-o, --output <file>` — write the body to a file
- `--include` — print status line + headers before the body
- `--pretty` — pretty-print the body
- `--json` — a JSON result envelope instead of the raw body
- `--fail` — exit 1 on HTTP status ≥ 400
- `-q, --quiet` — no metadata line on stderr

---

## token

`certapi token [options]` — fetch an OAuth 2.0 token.

- `--grant client_credentials|password|refresh` (default client_credentials)
- `--token-url <url>` **(required)**, `--client-id`, `--client-secret`
- `--client-auth body|basic` — send client creds in the body (default) or a Basic header
- `--scope "<a b c>"`, `--username`/`--password` (password grant), `--refresh-token` (refresh grant)
- `--param k=v` — extra form parameter (repeatable)
- `--save` + `--for <api-url>` (repeatable) — store the token for that API origin so later `send`
  attaches it; `--workspace <file>` to save into a workspace file
- `--json` — full result; `-q` quiet
- TLS/cert flags apply (the token endpoint itself may be mTLS)

The interactive **authorization-code** grant is app-only (see [Authentication](08-Authentication.md)).

---

## run

`certapi run <Collection[/Folder][/Request]> [options]` or `certapi run --all [options]`.

- `--all` — run every saved request in the workspace
- `--workspace <file>` — collections from a workspace file (default: live GUI state)
- `--env <name>` / `--var k=v` — variables
- `--data <file>` — data-driven: repeat once per CSV/JSON row (see [Data-Driven Runs](13-Data-Driven-Runs.md))
- `--record` / `--no-record` — write known-good results back (default: on for live state, off for
  workspace files; skipped while the GUI is running)
- `--strict-vars` — unresolved `{{tokens}}` fail the request
- `--no-auto-token` — don't capture/attach session tokens
- `--cookies` — keep a cookie jar for the run
- `--json` — JSON results instead of the table

A request passes when its assertions pass, or on any 2xx if it has none. Exit 1 if any request fails.

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

## sse

`certapi sse <url> [options]` — stream Server-Sent Events.

- `-H "k: v"`, `--max-events <n>`, `--json` (ndjson), `-q`
- cert flags + `--insecure`

## ws

`certapi ws <url> [options]` — WebSocket console.

- `-m, --message <text>` (repeatable; stdin lines also sent), `--expect <n>`, `-H`, `-q`
- cert flags + `--insecure`

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
- `certapi export openapi [<folder>] -o <file> [--workspace <file>]`
- `certapi export workspace -o <file> [--workspace <file>]`

## serve

`certapi serve <upstream> --port <n> [options]` — local mTLS gateway (see
[Local Gateway](19-Local-Gateway.md)).

## mcp

`certapi mcp [options]` — MCP server for AI agents (see [MCP Server](20-MCP-Server.md)).

---

`certapi --version` prints the version.

Next: [Keyboard Shortcuts](22-Keyboard-Shortcuts.md).
