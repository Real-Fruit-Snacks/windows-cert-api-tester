# 19. Local Gateway (`serve`)

Some apps and tools can't present a client certificate — a browser tab, a quick script, a legacy
utility. The **local gateway** bridges that: it listens on a loopback port, and forwards everything to
a certificate-protected upstream **with your client certificate attached**. The calling app just talks
plain HTTP to `localhost`.

## Start a gateway

```powershell
certapi serve https://internal-api.example.com --port 8443 --cert "CN=My Client"
```

Now point any tool at `http://localhost:8443/...` and it reaches the mTLS-protected upstream:

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
| `--insecure` | Ignore the upstream's server-certificate errors (internal CAs) |
| `--token <value>` | Require callers to send `Authorization: Bearer <value>` — a shared secret so only your tools can use the gateway |
| `--timeout <seconds>` | Per-request upstream timeout (default 100) |
| `--workspace <file>` | Resolve a saved-website `<upstream>` from a workspace file |
| `-q, --quiet` | No startup banner or per-request log |

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
- **`mock`** *is* the endpoint — a fake server that echoes requests, for testing without a real API.
  See [Mock Server](18-Mock-Server.md).

Next: [MCP Server](20-MCP-Server.md).
