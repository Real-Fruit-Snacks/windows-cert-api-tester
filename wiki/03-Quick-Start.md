# 3. Quick Start

Two minutes to your first request, in the app and on the command line.

## In the app

1. **Open** `ApiTester.App.exe`.
2. On the request line, leave the certificate on *— no certificate —* (or pick one if your endpoint
   needs mTLS).
3. Choose a method (**GET**) and type a URL, e.g. `https://api.github.com/zen`.
4. Press **Send** (or `Ctrl+Enter`).
5. The response appears below. The **Pretty** tab formats JSON/XML; **Raw** shows the exact bytes;
   **Headers**, **Diagnostics**, and **Network** show the rest.

That's it. To send with a client certificate, pick it from the **CERTIFICATE** dropdown first — see
[Certificates & mTLS](06-Certificates-and-mTLS.md).

## On the command line

```powershell
# A plain GET
certapi send https://api.github.com/zen

# With a client certificate from the Windows store (by subject or thumbprint)
certapi send https://internal.corp/api/health --cert "CN=My Client"

# POST some JSON and pretty-print the reply
certapi send https://api.example.com/users -X POST -d '{"name":"Ada"}' --pretty
```

The response body goes to **stdout**; the status/timing line and any notes go to **stderr**, so you
can pipe the body cleanly:

```powershell
certapi send https://api.example.com/users | ConvertFrom-Json
```

Exit codes are script-friendly: **0** success, **1** transport failure, **2** usage error,
**3** data error. Add `--fail` to also exit 1 on HTTP 4xx/5xx.

## Try it against the built-in mock

No real endpoint handy? Start the local [mock server](18-Mock-Server.md) and fire at it:

```powershell
certapi mock --port 8770
# in another terminal:
certapi send http://127.0.0.1:8770/anything -X POST -d '{"hi":1}'
```

The mock echoes your request back as JSON — method, path, query, headers, body. It also serves
`/status/<code>`, `/sse`, `/token`, and a WebSocket echo, so you can exercise nearly every feature
without leaving your machine.

## Prove mTLS works end-to-end

```powershell
certapi selftest
```

This stands up a throwaway mutual-TLS server in memory, generates a CA + server + client certificate,
makes one authenticated round-trip, and reports pass/fail — no real endpoint required.

Next: [Core Concepts](04-Concepts.md) to understand what's happening under the hood, or jump straight
to [Building Requests](07-Building-Requests.md).
