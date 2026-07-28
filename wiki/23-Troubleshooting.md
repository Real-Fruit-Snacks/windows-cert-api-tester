# 23. Troubleshooting

Common problems and how to diagnose them.

## Start here: `certapi doctor`

When something won't connect and you don't yet know why, ask the doctor before reading anything
else on this page:

```powershell
certapi doctor https://api.example.com/health --cert "CN=My Client"
```

It makes the connection one stage at a time — URL, proxy decision, DNS (domain name system), TCP
(Transmission Control Protocol), the proxy tunnel, the TLS (Transport Layer Security) handshake,
then an HTTP (Hypertext Transfer Protocol) GET — and tells you **which stage broke**, with what it
saw at each. Every stage is timed, so "slow" is diagnosable too.

Four things it can tell you that an ordinary request never will:

- **Which certificate authorities the server accepts client certificates from**, matched against
  the certificates you actually have. "The server accepts certificates from `CN=Corp Issuing CA 2`
  — none of your 3 certificates are issued by any of those" answers the most common mTLS mystery
  in one line, and it is information only visible during a handshake.
- **Whether this network is decrypting TLS in the middle.** If the chain's root is an inspection
  appliance (or a private root this machine happens to trust), doctor says so — and says why it
  matters: a client certificate cannot survive an intercepting proxy.
- **Which proxy the machine picks for this URL**, including one chosen by a PAC (proxy
  auto-config) script, plus what the proxy said if it refused the tunnel — including the
  authentication schemes it offered on a 407.
- **Whether the internet is reachable at all**, or a captive portal (hotel or guest Wi-Fi
  sign-in page) is in the way, or the host simply needs the VPN (virtual private network).

`--json` prints the whole report for scripts; `-q` shows only what failed or carries advice.
Exit 0 when every stage passed, 1 when one failed.

## Self-test

Before blaming an endpoint, prove your machine can do mTLS at all:

```powershell
certapi selftest          # CLI
```

or click **Run Self-Test** in the app. It generates a CA (certificate authority) + server + client
certificate in memory,
stands up a loopback mTLS (mutual Transport Layer Security) server, and makes one authenticated
round-trip. If this fails, the problem
is local (certificate loading, the TLS stack) — not the target API (application programming
interface).

## Turn on diagnostics

Add `--debug` (optionally `--log-file diag.log`) to any CLI (command-line interface) command for a
full trace — resolved URL (Uniform Resource Locator),
headers (Authorization masked), certificate lookup, TLS version/cipher, timings, and full stack
traces. In the app, the **Diagnostics** response tab shows the connection details.

## "The server refused the certificate"

The TLS handshake completed but the server rejected your client certificate. Check:

- You picked the **right certificate** (`certapi certs` to list; match subject/thumbprint).
- The certificate has the **Client Authentication** EKU (Extended Key Usage) and a usable
  **private key**.
- The server actually **trusts** your certificate's issuer.
- For a file certificate, that the **private key loaded** — a keyless PEM (Privacy-Enhanced Mail)
  fails with a clear message;
  supply `--key-file` or use a `.pfx`.

## "The server's own certificate isn't trusted"

Your machine doesn't trust the **server's** certificate (common with internal/private CAs). This is
separate from your client cert. Tick **Ignore server certificate errors** (app) or add `--insecure`
(CLI) if you trust the server. To fix it properly, install the internal CA in your trust store.

## "The server's certificate was revoked"

This is a **different** finding from "isn't trusted" above: the chain built and would otherwise be
trusted, but the issuer has since revoked the certificate — a compromised key, or someone who left, are
the usual reasons. It only shows up when revocation checking is turned on (`--revocation offline` or
`--revocation online`; by default it's off — `--revocation none` — so this can't happen at all). A
pinned thumbprint (`certapi trust add`) doesn't rescue it either: revocation is the issuer's later word
against the pin's earlier one, and the later word wins. See
[Certificates & mTLS](06-Certificates-and-mTLS.md#checking-for-revocation).

## "Revocation status unknown"

Expected on a corporate network — not a bug. With `--revocation online`, the endpoint that answers the
check — the Online Certificate Status Protocol (OCSP) responder, or the certificate revocation list
(CRL) distribution point — is commonly blocked or unreachable from inside a locked-down network, and
certapi reports the status as **unknown** rather than treating it as a failure, because failing on it
would make `--revocation online` unusable on exactly the networks it targets. If you need an
indeterminate answer treated as a failure instead, add `--revocation-strict` — it only makes sense
together with `--revocation offline` or `--revocation online`; using it with checking off (the default)
is a usage error.

## A network / DNS error

The connection never reached TLS — DNS (Domain Name System) failure, wrong host/port, firewall, or
the service is down.
Verify the URL, and remember the app **honors your machine proxy** (WPAD/PAC — Web Proxy
Auto-Discovery / proxy auto-configuration) using your Windows
credentials; a misconfigured proxy shows up here. If a host should be reached directly instead —
an internal service the proxy can't route to, say — list it with `--noproxy` (or set `NO_PROXY`)
rather than turning the proxy off for everything. To tell whether a request went direct because a
bypass rule matched or because there was no proxy at all, check Diagnostics or `--debug`: a matched
rule prints `Bypassed by` naming it.

## A timeout

The request took longer than the timeout. Raise it on the request line or with `--timeout <seconds>`.

## No certificates in the dropdown

The list shows only certificates with **client-auth** capability and a private key in your store.
Press **F5** to refresh after importing one. Add the machine store with `--store LocalMachine`
(CLI) if your certificate lives there. You can always load one from a file with **From file…** /
`--cert-file`.

## Windows Integrated Auth isn't working

- For **SSO** (single sign-on), you must be signed in with an account the target accepts (usually
  domain-joined).
- **Kerberos** needs the target's SPN (service principal name) registered; otherwise it falls back to
  **NTLM** (NT LAN Manager) — try explicit
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

## My tokens disappeared after I copied state.json to another machine / logged in as another user

This is expected. Secrets in the workspace — captured tokens and cookies, a saved request's auth
secret, a variable marked **secret** — are encrypted with the Windows Data Protection API (DPAPI) for
the Windows user who saved them, and DPAPI deliberately can't decrypt a value for anyone else. Loading
the file as a different user, or on a different machine, names each secret it couldn't read (as a
`warning:` on stderr, or in the app's status line) and treats it as absent — dropped if it was a
captured token/cookie, left empty otherwise. Everything else in the workspace — requests, collections,
chains, history, environments — loads intact; just log back in / re-capture / re-enter the secret and
carry on.

## What is this `state.json.20260727-143005.bak` file?

That's a one-time backup of your previous workspace file, taken automatically right before the first
save that rewrites it in the new encrypted format — so upgrading to the encrypted format can't lose
the copy you had before. It's a plain snapshot of what the file looked like right before that save.
Safe to keep for a while as a fallback, or delete once you're satisfied the upgraded file is fine.

Next: [FAQ](24-FAQ.md).
