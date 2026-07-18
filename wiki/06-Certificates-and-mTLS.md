# 6. Certificates & mTLS

The signature feature. This chapter covers picking a certificate, loading one from a file, ignoring
server-cert errors, and reading the connection diagnostics.

## Picking a certificate from the Windows store

In the app, the **CERTIFICATE** dropdown on the request line lists the client-auth certificates in
your store. Only certificates whose Extended Key Usage allows *Client Authentication* (and that have
a usable private key) are offered. Pick one and it's presented on every send until you change it.

Press **F5** to refresh the list if you've just imported a certificate.

On the CLI, pick by **subject** or **thumbprint**:

```powershell
certapi send https://internal.corp/api --cert "CN=My Client"
certapi send https://internal.corp/api --cert 4A8823F1C0...      # thumbprint
```

By default only your user store (`CurrentUser`) is searched. Add `--store LocalMachine` to also search
the machine store:

```powershell
certapi certs                       # list what's available
certapi certs --store LocalMachine  # include the machine store
certapi send https://internal.corp/api --cert "CN=Svc" --store LocalMachine
```

## Loading a certificate from a file

No store entry? Point at a file instead.

- **`.pfx` / `.p12`** (certificate + private key, often password-protected):

  ```powershell
  certapi send https://internal.corp/api --cert-file client.pfx --cert-password "secret"
  ```

- **PEM** (`.crt` / `.pem`) with the key inline, or with the key in a separate file:

  ```powershell
  certapi send https://internal.corp/api --cert-file client.pem                 # key inline
  certapi send https://internal.corp/api --cert-file client.crt --key-file client.key
  ```

In the app, use the **From file…** button next to the certificate dropdown.

> **Why files are re-imported internally:** on Windows, SChannel (the TLS stack) can't use *ephemeral*
> private keys. The app loads file-based keys through a temporary, exportable PKCS#12 container so the
> handshake works, then discards it. You don't have to do anything — it just means a PEM whose key is
> missing will fail with a clear "no private key" message rather than a cryptic handshake error.

## Trusting the server: `--insecure`

Presenting your certificate and **trusting the server's** certificate are two separate things. Internal
APIs sit behind private CAs your machine may not trust, which fails the handshake with
*"the server's own certificate isn't trusted."* To proceed anyway:

- **App:** tick **Ignore server certificate errors** (clearly labelled insecure).
- **CLI:** add `--insecure`.

Use it for internal/self-signed servers you already trust — not the public internet.

## Reading the diagnostics

After a send, the **Diagnostics** tab (app) shows what actually happened in the handshake:

- **TLS protocol** and **cipher suite** negotiated.
- **Client certificate** — whether yours was *presented to the server* (the real test that mTLS
  worked), or whether the server didn't ask for one.
- **Server certificate** — subject, issuer, thumbprint, expiry, and the chain.

On the CLI, add `--debug` to print the same TLS details (and much more) to stderr.

## Proving the whole path works

Not sure your setup is right? Run the **self-test**, which needs no real endpoint:

```powershell
certapi selftest
```

It generates a CA, a server certificate, and a client certificate in memory, stands up a loopback
mTLS server, and makes one authenticated round-trip — proving certificate loading, presentation, and
validation all work on this machine. In the app, click **Run Self-Test**.

Want a **standing** server to test against (not just a one-shot)? See the
[Mock Server](18-Mock-Server.md), which can require mTLS and even hands you a ready-to-use client
`.pfx`.

Next: [Building Requests](07-Building-Requests.md).
