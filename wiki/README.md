# Certificate API Tester — Handbook

A Windows-native API (application programming interface) client built around **mutual TLS
(Transport Layer Security, or mTLS) client-certificate authentication**, with a polished WPF
(Windows Presentation Foundation) app and a matching `certapi` command-line tool. This handbook is
the complete guide to both.

> New here? Read [Introduction](01-Introduction.md) → [Installation](02-Installation.md) →
> [Quick Start](03-Quick-Start.md), then dip into whatever you need.

## Contents

### Getting started
1. [Introduction](01-Introduction.md) — what it is and who it's for
2. [Installation](02-Installation.md) — download, requirements, first run
3. [Quick Start](03-Quick-Start.md) — your first request in two minutes
4. [Core Concepts](04-Concepts.md) — certificates, mTLS, and the workspace model
5. [The Interface](05-The-Interface.md) — a guided tour of the app

### Sending requests
6. [Certificates & mTLS](06-Certificates-and-mTLS.md)
7. [Building Requests](07-Building-Requests.md) — method, URL (Uniform Resource Locator), params, headers, body
8. [Authentication](08-Authentication.md) — Auto, Bearer, Basic, OAuth 2.0, Windows Integrated
9. [Environments & Variables](09-Environments-and-Variables.md)
10. [Collections & History](10-Collections-and-History.md)

### Beyond a single call
11. [Testing & Assertions](11-Testing-and-Assertions.md)
12. [Capturing Values](12-Capturing-Values.md)
13. [Data-Driven Runs](13-Data-Driven-Runs.md)
14. [Endpoint Discovery (fuzzing)](14-Endpoint-Discovery.md)
15. [Live Streaming](15-Live-Streaming.md) — WebSocket & Server-Sent Events (SSE)
16. [Response Views](16-Response-Views.md) — Pretty, Raw, Diagnostics, Rendered, Network
- Also: [Session Capture](26-Session-Capture.md) — log in once in a browser, reuse the session (cookies + tokens)

### Tooling
17. [Import & Export](17-Import-and-Export.md) — cURL and OpenAPI
18. [Mock Server](18-Mock-Server.md) — a local endpoint to fire requests at
19. [Local Gateway (`serve`)](19-Local-Gateway.md)
20. [MCP Server](20-MCP-Server.md) — the Model Context Protocol server for AI (artificial
    intelligence) agents
21. [CLI Reference](21-CLI-Reference.md) — every `certapi` command-line interface (CLI) command

### Reference
22. [Keyboard Shortcuts](22-Keyboard-Shortcuts.md)
23. [Troubleshooting](23-Troubleshooting.md)
24. [FAQ](24-FAQ.md) — frequently asked questions
25. [Building from Source](25-Building-from-Source.md)

## At a glance

| | |
|---|---|
| **Platform** | Windows 10 / 11 (x64) |
| **Runtime** | .NET 9 (bundled in the self-contained builds — nothing to install) |
| **Two front ends** | `ApiTester.App.exe` (GUI — graphical user interface) and `certapi.exe` (CLI) — same engine |
| **Signature feature** | Client-certificate (mTLS) auth from the Windows store or a file |
| **License** | See `LICENSE` in the repository |

Everything documented here is available in both the app and the CLI unless noted. The CLI's built-in
help (`certapi help <command>`) is always the authoritative source for options.
