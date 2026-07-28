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
| `--upstream <prefix>=<url>` | Mount an upstream at a path prefix, repeatable — with `/api=https://api.internal` a `GET /api/orders` reaches `https://api.internal/orders`. Longest prefix wins, and a path under no prefix is a 404 that contacts nothing. The positional `<upstream>` above is the same thing written `/=<url>` |
| `--port <n>` | Local port to listen on (127.0.0.1) |
| `--cert <thumb\|subject>` | Client certificate from the Windows store |
| `--cert-file <path>` / `--cert-password` / `--key-file` | Certificate from a file instead |
| `--store <location>` | `CurrentUser` (default) or `LocalMachine` |
| `--insecure` | Ignore the upstream's server-certificate errors (internal CAs — certificate authorities) |
| `--revocation none\|offline\|online` | Check whether the upstream's certificate has been revoked by its issuer (default `none`, unchanged from every release before this one) |
| `--revocation-strict` | Treat an undeterminable revocation status as fatal instead of merely enforced-and-logged; needs `--revocation offline` or `--revocation online` (usage error otherwise) |
| `--token <value>` | Require callers to send `Authorization: Bearer <value>` — a shared secret so only your tools can use the gateway |
| `--timeout <seconds>` | Per-request upstream timeout (default 100) |
| `--workspace <file>` | Resolve a saved-website `<upstream>` from a workspace file |
| `-q, --quiet` | No startup banner or per-request log |
| `--tls` | Serve the gateway itself over HTTPS (HTTP Secure) on 127.0.0.1 with a generated gateway certificate, so `Secure` and `__Host-`/`__Secure-` cookies work. Binding the port needs an elevated prompt the first time; the exact `netsh` command is printed if it isn't available |
| `--tls-trust` | Also install that certificate into `CurrentUser\Root` so the browser accepts it silently — explicit, logged, and reversible. Only with `--tls` |
| `--tls-untrust` | Remove a previously trusted gateway certificate and exit; a standalone action, run on its own |
| `--browser` | Turn on all four browser accommodations below at once — each also works on its own |
| `--cors [<origins>]` | Answer CORS (Cross-Origin Resource Sharing) preflights at the gateway and add the response headers a script needs to read the reply; echoes the caller's own `Origin` with no value, or takes a comma-separated allowlist |
| `--cors-max-age <seconds>` | How long a browser may cache a CORS preflight answer (default 600). Only with `--cors` |
| `--rewrite-cookies` | Strip `Domain=` and `Secure` from each `Set-Cookie`, and turn `SameSite=None` into `Lax`, so the browser stores the cookie against the gateway |
| `--rewrite-location` | Rewrite a 3xx `Location` aimed at the upstream to point at the gateway instead; one aimed elsewhere is left alone and logged |
| `--allow-upgrade` | Relay WebSocket connections to the upstream through your certificate |
| `--request-header "Name: value"` | Set a header on the request before it reaches the upstream — replace if already sent, add if not. Repeatable |
| `--remove-request-header <name>` | Strip a header from the request before it reaches the upstream. Repeatable |
| `--response-header "Name: value"` | Set a header on the response before it reaches the caller, same replace-or-add rule. Repeatable |
| `--remove-response-header <name>` | Strip a header from the response before it reaches the caller. Repeatable |

## Revocation checking

`--revocation none|offline|online` (default `none`) and `--revocation-strict` apply to the gateway's
own connection to the upstream, exactly as they do on [`certapi send`](21-CLI-Reference.md#send): a
certificate the upstream's issuer has revoked is refused even past a pinned thumbprint, an
indeterminate status is not fatal unless `--revocation-strict` asks for that, and `--insecure` still
overrides both. The gateway **enforces** the setting on every forwarded call — it just has no per-call
diagnostics object of its own to report a status back through, the way `send`'s `--json` envelope does.
See [Certificates & mTLS](06-Certificates-and-mTLS.md#checking-for-revocation) for what the modes mean.

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

## Browser accommodations

A plain relay hands a browser exactly the headers that make it refuse the response — a tab checks
things curl and a script never do, like origin policy, cookie attributes, and where a redirect
actually leads. Without any of the four flags below nothing changes: the gateway stays a
byte-faithful relay, which is exactly what protects every existing non-browser caller. `--browser` is
the bundle that turns on all four accommodations at once; each one is also usable entirely on its
own.

`--cors [<origins>]` answers a preflight at the gateway and adds the response headers a script needs
in order to read the reply. With no value the caller's own `Origin` is echoed back; give it a
comma-separated list to allow only those origins instead. See
[Browsers and Private Network Access](#browsers-and-private-network-access), below, for the further
check Chrome runs on top of CORS.

`--rewrite-cookies` is what lets the browser actually keep the cookie: a `Set-Cookie` loses its
`Domain=` attribute and its `Secure` attribute, and `SameSite=None` becomes `SameSite=Lax`, so the
cookie can be stored against the gateway rather than the upstream the browser never directly talked
to.

`--rewrite-location` keeps a redirect on the gateway: a 3xx `Location` aimed at the upstream comes
back pointing at the gateway instead. One aimed anywhere else is left exactly as the upstream wrote
it, and logged — because that hop leaves the gateway, and your client certificate leaves with it.

`--allow-upgrade` relays WebSocket connections to the upstream through your certificate, the same way
every other forwarded request is.

Over the default plaintext loopback origin, a cookie named `__Host-…` or `__Secure-…` cannot work at
all: it requires the `Secure` attribute, which no browser accepts over plaintext `http://127.0.0.1`.
Rather than dropping such a cookie behind your back, the gateway still relays it and names it in a
warning. `--tls` is the fix, because it serves the gateway itself over HTTPS.

## Record a session, replay it offline

Two ends of one HTTP Archive (HAR) format: capture what the real upstream says while it is up,
then answer from that capture when it is gone — a plane, a demo, an upstream that has been
decommissioned, or a test suite that must not hit production.

```powershell
# capture: every forwarded exchange is appended, written on Ctrl+C
certapi serve https://api.internal --port 8443 --cert "CN=My Client" --record session.har

# replay: the upstream is never contacted; answers come from the file
certapi serve https://api.internal --port 8443 --replay session.har
```

`--record` redacts `Authorization` and `Cookie` by default, since a recording is a file people
share; `--record-include-secrets` keeps them. `--replay` matches a request the way `mock --har`
does — method and path, query included when it disambiguates, in recorded order for a repeated
call — and answers a path the session never saw with 404. The two flags are mutually exclusive:
you cannot record a session you are inventing. Because the format is the same one `mock --har`
reads, a session recorded here can also be replayed by `certapi mock --har session.har` without a
gateway at all.

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
