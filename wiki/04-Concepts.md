# 4. Core Concepts

A short mental model that makes the rest of the handbook click.

## Mutual TLS (mTLS) in one paragraph

Ordinary HTTPS (Hypertext Transfer Protocol Secure) authenticates the **server** to you: the server
presents a certificate, your machine checks it against trusted CAs (certificate authorities).
**Mutual** TLS (Transport Layer Security) adds the other direction — during the handshake, the
**client** also presents a certificate, and the server decides whether to trust it. That client
certificate *is* your identity. This app's core job is to present the right client certificate on
every request, and to show you exactly what happened in the handshake.

## Client certificates

A client certificate is a certificate whose private key you hold and whose **Extended Key Usage**
includes *Client Authentication*. It can live in two places:

- **The Windows certificate store** — your personal store (`CurrentUser\My`) or the machine store
  (`LocalMachine\My`). This is the usual home for corporate-issued certs. The app lists these and
  presents one without ever exporting the private key.
- **A file** — a `.pfx`/`.p12` (certificate + private key, optionally password-protected) or a PEM
  (Privacy-Enhanced Mail) `.crt`/`.pem` (with the key inline or in a separate `--key-file`).

See [Certificates & mTLS](06-Certificates-and-mTLS.md) for the details, including a subtle Windows
requirement (SChannel needs an *exportable, non-ephemeral* key).

## Server trust and `--insecure`

Presenting your certificate is separate from **trusting the server's** certificate. Internal APIs
(application programming interfaces) are
often behind a private CA your machine doesn't trust, which normally fails the handshake. The
**Ignore server certificate errors** toggle (`--insecure` on the CLI — command-line interface)
bypasses that check. It's
clearly labelled insecure because it is — use it for internal/self-signed servers you already trust,
not the public internet.

## The workspace

Everything you build lives in a **workspace**: tabs, collections, environments, request history,
saved session tokens, and your theme. By default it's a single JSON (JavaScript Object Notation) file at
`%AppData%\CertApiTester\state.json`, shared by the app and the CLI. You can also keep **separate
workspace files** (`--workspace suite.json`) to check a request suite into source control or hand it
to a teammate. See [Collections & History](10-Collections-and-History.md).

## Requests, collections, environments

- A **request** is a method + URL (Uniform Resource Locator) + params + headers + body + auth +
  certificate + timeout, plus any
  tests and capture rules.
- A **collection** is a tree of folders and requests — your saved library. A folder can carry
  **defaults** (a base website, a certificate) that its requests inherit.
- An **environment** is a named set of `{{variables}}` — e.g. `Staging` with `host = staging.corp` —
  substituted into URLs, headers, and bodies at send time. See
  [Environments & Variables](09-Environments-and-Variables.md).

## Tokens flow automatically

When a response hands back a bearer token (an `access_token` field, an `X-Auth-Token` header, and
friends), the app **captures** it and scopes it to that website. Later requests to the same host
attach it automatically. You rarely copy-paste a token. See
[Capturing Values](12-Capturing-Values.md) and [Authentication](08-Authentication.md).

## App and CLI are the same engine

The GUI (graphical user interface) and `certapi` are two faces of one core library. A request saved in the app runs headless
with `certapi run`; a fuzz run in the CLI can save discovered endpoints into a collection the app then
opens. Learn a concept once and it applies to both.

Next: [The Interface](05-The-Interface.md).
