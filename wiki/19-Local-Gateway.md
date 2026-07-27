# 19. Local Gateway (`serve`)

Some apps and tools can't present a client certificate — a browser tab, a quick script, a legacy
utility. The **local gateway** bridges that: it listens on a loopback port, and forwards everything to
a certificate-protected upstream **with your client certificate attached**. The calling app just talks
plain HTTP (Hypertext Transfer Protocol) to `localhost`.

## Start a gateway

```powershell
certapi serve https://internal-api.example.com --port 8443 --cert "CN=My Client"
```

Now point any tool at `http://localhost:8443/...` and it reaches the upstream protected by mTLS
(mutual Transport Layer Security):

```powershell
curl http://localhost:8443/api/orders            # curl needs no cert
# or set an app's base URL to http://localhost:8443
```

The gateway is **loopback only** (127.0.0.1) — it never listens on an external interface.

## Options

| Option | Purpose |
|---|---|
| `--port <n>` | Local port to listen on (127.0.0.1) |
| `--cert <thumb\|subject>` | Client certificate from the Windows store |
| `--cert-file <path>` / `--cert-password` / `--key-file` | Certificate from a file instead |
| `--store <location>` | `CurrentUser` (default) or `LocalMachine` |
| `--insecure` | Ignore the upstream's server-certificate errors (internal CAs — certificate authorities) |
| `--token <value>` | Require callers to send `Authorization: Bearer <value>` — a shared secret so only your tools can use the gateway |
| `--timeout <seconds>` | Per-request upstream timeout (default 100) |
| `--workspace <file>` | Resolve a saved-website `<upstream>` from a workspace file |
| `-q, --quiet` | No startup banner or per-request log |
| `--cors-max-age <seconds>` | How long a browser may cache a CORS (Cross-Origin Resource Sharing) preflight answer (default 600). Only with `--cors` |
| `--request-header "Name: value"` | Set a header on the request before it reaches the upstream — replace if already sent, add if not. Repeatable |
| `--remove-request-header <name>` | Strip a header from the request before it reaches the upstream. Repeatable |
| `--response-header "Name: value"` | Set a header on the response before it reaches the caller, same replace-or-add rule. Repeatable |
| `--remove-response-header <name>` | Strip a header from the response before it reaches the caller. Repeatable |

## Header rules

`--request-header`, `--remove-request-header`, `--response-header`, and `--remove-response-header`
apply to forwarded HTTP traffic, with or without `--browser` — a header rule is not a browser
concern. Setting a header replaces it if one was already present and adds it otherwise; naming the
same header to both a set flag and a remove flag on the same side removes it, since removal wins over
setting. On the response side these rules apply *after* `--browser`'s own rewrites (CORS, cookies,
`Location`), so a header you set here wins over one the gateway injected.

`Connection`, `Keep-Alive`, `Transfer-Encoding`, `Content-Length`, `TE`, `Trailer`, `Upgrade`,
`Proxy-Authenticate`, `Proxy-Authorization`, and `Host` are refused with a usage error naming the
header and why, rather than silently ignored — the first nine frame the HTTP message and the HTTP
stack manages them, and `Host` is set by the gateway's own HTTP client from the upstream URI, so a
rule for it would only ever half-apply. A header name that is missing, or that carries a character
an HTTP field name cannot hold — a space, an embedded colon — is a usage error too, for the same
reason: the header could never match, so the rule would be dropped rather than applied. These rules
never touch a CORS or PNA (Private Network Access) preflight the gateway answers itself, its own
error pages, or a relayed WebSocket upgrade.

## Browsers and Private Network Access

Chrome runs a further check, PNA, before it lets a page on a public origin
reach a private or loopback address at all: its preflight carries
`Access-Control-Request-Private-Network: true`, and Chrome blocks the request unless the response
answers `Access-Control-Allow-Private-Network: true`. `--cors` answers it, but only for an origin the
same allowlist already accepts — an origin outside an explicit `--cors <origins>` list still gets a
bare 403. Letting a public origin reach a loopback service at all is a real exposure even with PNA
answered, which is why naming the origins you develop from with `--cors <origins>` is safer than
leaving it echoing whoever asks.

## Add a shared secret

Because anything on your machine could hit the loopback port, you can require a token so only your own
tools get through:

```powershell
certapi serve https://internal-api.example.com --port 8443 --cert "CN=My Client" --token s3cret
# callers must send:  Authorization: Bearer s3cret
```

## Gateway vs. mock

- **`serve`** forwards to a **real** upstream, adding your certificate — for reaching a cert-protected
  service from a tool that can't do mTLS.
- **`mock`** *is* the endpoint — a fake server that echoes requests, for testing without a real API
  (application programming interface).
  See [Mock Server](18-Mock-Server.md).

Next: [MCP Server](20-MCP-Server.md).
