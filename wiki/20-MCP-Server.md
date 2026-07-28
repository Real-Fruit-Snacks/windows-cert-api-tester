# 20. MCP Server (for AI agents)

`certapi mcp` runs a [Model Context Protocol (MCP)](https://modelcontextprotocol.io) server so an AI
(artificial intelligence) agent
can make **mutual-TLS (mTLS — Transport Layer Security)** API (application programming interface)
calls — using a certificate **you** pin at launch, bounded by a host
allowlist. The agent never handles the certificate itself; it just asks the server to make calls. It
speaks JSON-RPC (JavaScript Object Notation Remote Procedure Call) over **stdio** — nothing on the
network.

## Start it

```powershell
certapi mcp --cert "CN=Agent Client" --allow api.example.com
```

Wire that command into your MCP-capable client (an agent framework, an IDE (integrated development
environment) assistant, etc.) as a
stdio server. The agent then has a small, safe toolset for talking to your API.

## Tools exposed

| Tool | Does |
|---|---|
| `send_request` | Make an mTLS request (method, URL (Uniform Resource Locator), headers, body) to an allowed host. Redirects are never followed — a 3xx comes back as itself, so every hop is an explicit, allowlist-checked call |
| `run_saved` | Run a saved request by its path, exactly as `certapi run` would: its saved transport settings, auth, assertions, and capture rules all apply |
| `run_chain` | Run a saved chain by name — its requests in order as one unit, each step seeing what earlier steps captured; every step's URL is checked against the allowlist, even one built from an earlier capture |
| `list_saved` | List saved requests from the workspace |
| `list_environments` | List environment names for `run_saved`/`run_chain`'s `env` argument — variable values are never returned |
| `list_certificates` | List the client certificates available |
| `grpc_list` | List a gRPC (remote procedure call) server's services and methods, via reflection or a descriptor set pinned at launch |
| `grpc_call` | Invoke a gRPC method — unary, server-streaming, client-streaming, or bidirectional — with the pinned certificate |
| `self_test` | Prove the mTLS path end-to-end |

Bearer tokens seen in responses are captured in memory for the session and attached to later calls to
the same host (like the app's automatic tokens), unless you pass `--no-auto-token`. Values captured
by `run_saved` and `run_chain` resolve `{{variables}}` in later calls the same way. **Nothing is ever
written back to disk** — the workspace is read once at launch, and the session's captures die with
the process.

## Resources and protocol features

Saved requests, environments, and chains are also published as **read-only MCP resources**
(`certapi://requests/…`, `certapi://environments/…`, `certapi://chains`), so a host can show them to
the agent without a tool call. A request's auth secret reads as `(redacted)` and a secret variable's
value is withheld — the same stance `certapi export workspace` takes.

Tools carry the protocol's **behavioral annotations** (read-only, destructive, idempotent,
open-world hints) so a host's permission model has something structured to act on, results include
**structured content** alongside the text form, and server notes (tokens captured, assertions
failed) arrive as **logging notifications** whose minimum level the host sets with
`logging/setLevel`. Protocol revisions `2024-11-05` through `2025-06-18` are supported; an unknown
client version is answered with the newest supported one rather than echoed.

## Guardrails

The point of `mcp` is to give an agent **capability without keys**:

- **Pinned certificate** — `--cert` (or `--cert-file`) fixes the identity; the agent can't change it.
  A saved request that names its own certificate uses that one (an operator's earlier choice); one
  that names none uses the pinned certificate.
- **Host allowlist** — `--allow <host>` (repeatable). A request URL must match, or be a subdomain of,
  an allowed host. **Omit `--allow` and any host is permitted — with a printed warning.** Always set
  an allowlist for anything but local experimentation. Enforced per request, including every chain
  step and every gRPC call.
- **Pinned trust, not blanket trust** — a host pinned with `certapi trust add` is reachable without
  `--insecure`, exactly as it is for `send`/`run`. `--insecure` remains the blunt launch-wide switch.
- **No redirects** — a 3xx is returned as data, never followed, so the allowlist cannot be escaped
  by a server-controlled hop.
- **No secrets in the transcript** — the agent asks the server to authenticate; the certificate and
  captured tokens stay on your side. Resources redact auth secrets and secret variable values.
- **Read-only against your files** — the workspace is never written back; captures and tokens live
  in memory for the session only.

## Options

| Option | Purpose |
|---|---|
| `--cert <thumb\|subject>` | The certificate all tools use (pinned) |
| `--cert-file` / `--cert-password` / `--key-file` | Pin a certificate from a file instead |
| `--store <location>` | `CurrentUser` (default) or `LocalMachine` |
| `--allow <host>` | Allowed upstream host (repeatable) |
| `--insecure` | Ignore upstream server-certificate errors (internal CAs — certificate authorities) |
| `--timeout <seconds>` | `send_request` upstream timeout (default 100; a saved request keeps its own saved timeout) |
| `--workspace <file>` | Load saved requests / environments / chains from a workspace file (read once at launch) |
| `--no-auto-token` | Don't capture/reuse bearer tokens across the session |
| `--protoset <file>` | Compiled descriptor set for `grpc_list`/`grpc_call` against a server without reflection — pinned at launch; the agent cannot name files |
| `--proxy` / `--proxy-user` / `--no-proxy` / `--noproxy` | The same proxy control as [`send`](21-CLI-Reference.md#send), applied to every call the tools make |
| `--revocation <mode>` / `--revocation-strict` | The same [revocation checking](06-Certificates-and-mTLS.md) as `send`, applied to every call |
| `--retry <n>` / `--retry-on` / `--retry-delay` | The same retry rules as `send` |

## Examples

```powershell
certapi mcp --cert "CN=Agent Client" --allow api.example.com
certapi mcp --cert 4A8823… --allow api.example.com --allow auth.example.com --insecure
certapi mcp --cert "CN=Agent Client" --allow api.example.com --workspace .\suite.json
certapi mcp --cert "CN=Agent Client" --allow grpc.internal --protoset .\contracts.protoset
certapi mcp --cert "CN=Agent Client" --allow api.example.com --revocation online
```

The server runs until stdin closes. Exit 0 on a clean shutdown, 2 usage, 3 data error.

Next: [CLI Reference](21-CLI-Reference.md).
