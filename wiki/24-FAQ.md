# 24. FAQ

**Is it free / open source?**
See the `LICENSE` file in the repository.

**Do I need to install anything?**
No. The released `.exe` files are self-contained — the .NET runtime is bundled. They're portable; no
installer, no admin rights. The only optional dependency is the WebView2 runtime, used solely by the
Rendered tab.

**Windows only?**
Yes. It's built on WPF and the Windows certificate store — that's the whole point. There's no
macOS/Linux build.

**Does it store my private keys?**
No. Certificates in the Windows store are used in place — the private key never leaves the store. For
file certificates, the key is loaded into a temporary in-memory container for the handshake and
discarded; nothing is written back out.

**Can I use it without any certificates?**
Yes. Leave the certificate on *— no certificate —* and it's an ordinary API client. mTLS is there when
you need it.

**How do the app and the CLI relate?**
They're two front ends over one engine, sharing the workspace at
`%AppData%\CertApiTester\state.json`. Build a request in the app and run it with `certapi run`, or the
reverse. See [Core Concepts](04-Concepts.md).

**Can I keep a request suite in source control?**
Yes — export a workspace (`certapi export workspace -o suite.json`) or manage a `--workspace` file, and
check it in. Note that variable values (possibly secrets) travel with a workspace.

**Does it do OAuth?**
Yes — client-credentials, password, refresh, and interactive authorization-code with PKCE, in the app
and (except the interactive flow) via `certapi token`. See [Authentication](08-Authentication.md).

**Does it do Windows Integrated Auth (NTLM/Kerberos)?**
Yes — a *Windows (integrated)* auth type with SSO or explicit credentials, and `--windows-auth` on the
CLI.

**Can an AI agent use it?**
Yes — `certapi mcp` exposes a safe toolset over MCP with a pinned certificate and a host allowlist. See
[MCP Server](20-MCP-Server.md).

**Can I test without a real API?**
Yes — `certapi mock` (or **Mock server…** in the app) is a standing local server that echoes requests
and serves `/status`, `/sse`, `/token`, `/windows-auth`, and a WebSocket echo, over HTTP/TLS/mTLS. See
[Mock Server](18-Mock-Server.md).

**How do I reach a cert-protected API from a tool that can't do mTLS?**
Run `certapi serve <upstream> --cert …` and point the tool at the local port. See
[Local Gateway](19-Local-Gateway.md).

**Is `--insecure` dangerous?**
It skips **server** certificate validation, so only use it for internal/self-signed servers you trust.
It has nothing to do with your client certificate, which is still presented.

**Where do I report a bug or ask for a feature?**
Open an issue on the repository.

Next: [Building from Source](25-Building-from-Source.md).
