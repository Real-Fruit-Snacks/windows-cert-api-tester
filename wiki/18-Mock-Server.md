# 18. Mock Server

A standing local server you can fire requests at — the persistent counterpart to the one-shot
[self-test](23-Troubleshooting.md#self-test). It echoes each request back as JSON and serves a handful
of fixed routes, over plain HTTP, HTTPS, or **mutual TLS**. Point the app at itself to exercise nearly
every feature without a real API.

## Start it

**App:** click **Mock server…** in the status bar. Pick a mode (Plain HTTP / HTTPS / Mutual TLS) and a
port, press **Start**, and watch a live request log. **Copy URL** drops the address into a request;
**Open certs** reveals the generated certificates (TLS modes).

**CLI:**

```powershell
certapi mock                       # plain HTTP on 8770
certapi mock --port 9000 --tls     # HTTPS with a generated self-signed cert
certapi mock --mtls --cert-dir .\c # mutual TLS; writes certs to .\c
```

It runs until `Ctrl+C` and logs each request.

## Routes

| Route | Response |
|---|---|
| `/` (any path) | Echoes the request as JSON: method, path, query, headers, body, and — under mTLS — the client certificate you presented |
| `/status/<code>` | Responds with that HTTP status (e.g. `/status/404`, `/status/503`) |
| `/sse` | A short `text/event-stream` — try it with `certapi sse` |
| `/token` | An OAuth 2.0 token response — try it with `certapi token` |
| `/windows-auth` | Challenges with `401 WWW-Authenticate: NTLM`, then accepts the handshake — try it with `--windows-auth` |
| *Upgrade: websocket* (any path) | A WebSocket echo — try it with `certapi ws` |

## Modes and certificates

- **`--http`** (default) — plain HTTP; hit it with anything (curl, a browser, the app), no
  certificates.
- **`--tls`** — HTTPS with a generated self-signed server certificate.
- **`--mtls`** — HTTPS that **requires** a client certificate (any presented cert is accepted, and its
  subject is echoed back).

For the TLS modes the server writes its certificates to `--cert-dir` (default `.\certapi-mock-certs`):

- `mock-server.cer`, `mock-ca.cer` — trust these, or use `--insecure`.
- `mock-client.pfx` (mTLS only) — a ready-to-use client certificate to present.

## Dogfood the whole app

Because the mock speaks the app's own protocols, you can drive `send`, `sse`, `ws`, and `token`
against it:

```powershell
certapi mock --port 8770
certapi send http://127.0.0.1:8770/orders -X POST -d '{"hi":1}'   # echoed back
certapi send http://127.0.0.1:8770/status/418 --include           # 418 I'm a teapot
certapi token --token-url http://127.0.0.1:8770/token --client-id demo
certapi sse http://127.0.0.1:8770/sse --max-events 3
certapi ws  ws://127.0.0.1:8770/ws -m "hello" --expect 1

# mutual TLS end to end
certapi mock --mtls --port 9443 --cert-dir .\c
certapi send https://localhost:9443/orders --cert-file .\c\mock-client.pfx --insecure
```

Next: [Local Gateway](19-Local-Gateway.md).
