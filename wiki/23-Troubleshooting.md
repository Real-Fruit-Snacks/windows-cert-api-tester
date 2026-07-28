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

## "It works in my browser but not here"

Nine times in ten this is the proxy — and specifically a PAC (proxy auto-config) script sending
the two down different routes. Ask:

```powershell
certapi proxy https://api.example.com/orders
```

It prints the machine's configuration (WPAD auto-detection, the configuration-script address, the
static proxy and its bypass list) and then, for that URL, **two answers**: the one Windows' own
engine computes by running your PAC script, and the one .NET computes — which is the one certapi
follows. When those disagree, the command says so, and you have your explanation.

A PAC script is JavaScript, so nothing can predict its answer by reading configuration alone;
this runs the real engine rather than guessing.

## "It's slow" — reading the timings

Three different measurements answer three different questions, and mixing them up wastes an
afternoon:

- **`certapi doctor <url>`** times each *stage* of one fresh connection — DNS, TCP, the proxy
  tunnel, the TLS handshake, the first byte. This is the one that tells you *where* the time
  goes. A slow TLS stage with fast everything else usually means revocation checking reaching for
  a network endpoint; a slow DNS stage means a resolver problem, not an API problem.
- **A redirect chain** is timed per hop (`--show-redirects`, and in the HTTP Archive (HAR) export).
  A request that "takes two seconds" is often four hops of five hundred milliseconds, and the
  destination is innocent.
- **`certapi bench`** measures a *warm* endpoint under load, which is the number to quote for
  throughput — it deliberately reuses connections.

`certapi send` reports one total elapsed time and no phase breakdown, on purpose: connections are
pooled, so a second request to the same host has no DNS lookup, no TCP connect, and no handshake
to measure. Printing zeros for those would suggest they were instant rather than absent. Use
`doctor` when you want the breakdown.

## Watching what the network stack actually did: `--trace`

`doctor` diagnoses one connection it makes itself. `--trace` is different: it reports what .NET's
own networking stack did during **any** command, as it happens.

```powershell
certapi send https://api.internal/orders --trace
```

```
trace [   109.4 ms] System.Net.NameResolution    ResolutionStart    hostNameOrAddress=api.internal
trace [   155.4 ms] System.Net.NameResolution    ResolutionStop
trace [   156.4 ms] System.Net.Sockets           ConnectStart       address=…
trace [   181.8 ms] System.Net.Sockets           ConnectStop
trace [   182.7 ms] System.Net.Security          HandshakeStart     isServer=False targetHost=api.internal
trace [   221.5 ms] System.Net.Security          HandshakeStop      protocol=12288
trace [   222.7 ms] System.Net.Http              ConnectionEstablished  versionMajor=1 versionMinor=1 …
```

- `--trace-filter <substrings>` narrows it (comma-separated) — it is genuinely a firehose.
- `--trace-file <path>` writes it out instead of streaming to stderr.
- `--trace-verbose` adds the runtime's *internal* diagnostics: far more detail, far less stable
  (free-text handler messages, security-context buffers). Useful when the normal level is not
  enough; never something to parse.
- Credentials in event payloads are **redacted** — a trace is a file people paste into tickets.
  `--trace-include-secrets` keeps them, for when you are the only reader.

**Reading a reused connection.** A request that reuses a pooled connection emits *no*
`ConnectStart` and *no* `HandshakeStart` at all — that absence is the signal. It is the quickest
way to answer "is connection pooling actually working here".

**Two honest limits.** This is in-process: it observes the connections **this process** makes, so
under `certapi mock` or `certapi serve` you will also see that server's own accepts and
handshakes. And it is not packet capture — capturing packets needs a kernel driver and
administrator rights, which this tool deliberately never requires. What it gives instead is the
decrypted, structured account of connections a sniffer could not read anyway.

## Seeing the actual bytes: `--wire`

`--trace` reports what the stack *did*. `--wire` shows what it actually *sent and received* —
after TLS, before any parsing:

```powershell
certapi send https://api.internal/orders --cert "CN=My Client" --wire
```

```
>> sent 73 bytes at 80.7 ms
   GET /orders HTTP/1.1
   Host: api.internal
   Accept-Encoding: gzip, deflate, br

<< received 632 bytes at 103.5 ms
   HTTP/1.1 200 OK
   Content-Type: application/json
   …
```

Anything that isn't text is shown as hex and ASCII side by side, so a binary body is still
readable. `--wire-file <path>` writes the transcript out instead; credential headers are
**redacted** (the header name stays, so you can still see it was sent) unless you pass
`--wire-include-secrets`.

**This is the thing a packet capture cannot give you.** On an encrypted connection a sniffer sees
ciphertext; this is the decrypted conversation, and it needs no driver and no administrator
rights, because the tool is one end of the connection.

**Direct connections only.** Through a proxy, or on HTTP/3, the TLS session belongs to the HTTP
handler rather than to this tool, so there is no plaintext stream to read. In those cases the
command says so in one line and sends the request normally — it never prints nothing and leaves
you guessing. Use `--trace` there instead.

One consequence worth knowing: a request with `--wire` does not reuse a pooled connection (the
tapped connection is not shared), so you see the handshake as well as the exchange.

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
