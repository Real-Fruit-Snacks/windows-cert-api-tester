# 23. Troubleshooting

Common problems and how to diagnose them.

## Self-test

Before blaming an endpoint, prove your machine can do mTLS at all:

```powershell
certapi selftest          # CLI
```

or click **Run Self-Test** in the app. It generates a CA + server + client certificate in memory,
stands up a loopback mTLS server, and makes one authenticated round-trip. If this fails, the problem
is local (certificate loading, the TLS stack) — not the target API.

## Turn on diagnostics

Add `--debug` (optionally `--log-file diag.log`) to any CLI command for a full trace — resolved URL,
headers (Authorization masked), certificate lookup, TLS version/cipher, timings, and full stack
traces. In the app, the **Diagnostics** response tab shows the connection details.

## "The server refused the certificate"

The TLS handshake completed but the server rejected your client certificate. Check:

- You picked the **right certificate** (`certapi certs` to list; match subject/thumbprint).
- The certificate has the **Client Authentication** EKU and a usable **private key**.
- The server actually **trusts** your certificate's issuer.
- For a file certificate, that the **private key loaded** — a keyless PEM fails with a clear message;
  supply `--key-file` or use a `.pfx`.

## "The server's own certificate isn't trusted"

Your machine doesn't trust the **server's** certificate (common with internal/private CAs). This is
separate from your client cert. Tick **Ignore server certificate errors** (app) or add `--insecure`
(CLI) if you trust the server. To fix it properly, install the internal CA in your trust store.

## A network / DNS error

The connection never reached TLS — DNS failure, wrong host/port, firewall, or the service is down.
Verify the URL, and remember the app **honors your machine proxy** (WPAD/PAC) using your Windows
credentials; a misconfigured proxy shows up here.

## A timeout

The request took longer than the timeout. Raise it on the request line or with `--timeout <seconds>`.

## No certificates in the dropdown

The list shows only certificates with **client-auth** capability and a private key in your store.
Press **F5** to refresh after importing one. Add the machine store with `--store LocalMachine`
(CLI) if your certificate lives there. You can always load one from a file with **From file…** /
`--cert-file`.

## Windows Integrated Auth isn't working

- For **SSO**, you must be signed in with an account the target accepts (usually domain-joined).
- **Kerberos** needs the target's SPN registered; otherwise it falls back to **NTLM** — try explicit
  `--windows-user DOMAIN\user --windows-password …` to isolate a credential problem.
- Test the mechanism locally against the mock's `/windows-auth` route (see [Mock Server](18-Mock-Server.md)).

## The Rendered tab is blank or errors

The Rendered view needs the **WebView2** runtime (ships with Windows 11 / current Windows 10). If it's
unavailable, the tab says so; the rest of the app is unaffected. Install the Evergreen WebView2 Runtime
from Microsoft if you need it.

## Headless runs don't save results

While the app is open, `certapi run` against the **live** workspace deliberately skips writing results
back (the app would overwrite them on close). Use a `--workspace` file with `--record`, or close the
app, for headless runs that persist.

## Where's my data?

Everything is in `%AppData%\CertApiTester\state.json`. Back it up before experimenting; delete it to
start fresh (the app recreates it).

Next: [FAQ](24-FAQ.md).
