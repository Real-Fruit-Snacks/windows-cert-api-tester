# 20. MCP Server (for AI agents)

`certapi mcp` runs a [Model Context Protocol](https://modelcontextprotocol.io) server so an AI agent
can make **mutual-TLS** API calls — using a certificate **you** pin at launch, bounded by a host
allowlist. The agent never handles the certificate itself; it just asks the server to make calls. It
speaks JSON-RPC over **stdio** — nothing on the network.

## Start it

```powershell
certapi mcp --cert "CN=Agent Client" --allow api.example.com
```

Wire that command into your MCP-capable client (an agent framework, an IDE assistant, etc.) as a
stdio server. The agent then has a small, safe toolset for talking to your API.

## Tools exposed

| Tool | Does |
|---|---|
| `send_request` | Make an mTLS request (method, URL, headers, body) to an allowed host |
| `list_certificates` | List the client certificates available |
| `list_saved` | List saved requests from the workspace |
| `run_saved` | Run a saved request by name |
| `self_test` | Prove the mTLS path end-to-end |

Bearer tokens seen in responses are captured in memory for the session and attached to later calls to
the same host (like the app's automatic tokens), unless you pass `--no-auto-token`.

## Guardrails

The point of `mcp` is to give an agent **capability without keys**:

- **Pinned certificate** — `--cert` (or `--cert-file`) fixes the identity; the agent can't change it.
- **Host allowlist** — `--allow <host>` (repeatable). A request URL must match, or be a subdomain of,
  an allowed host. **Omit `--allow` and any host is permitted — with a printed warning.** Always set
  an allowlist for anything but local experimentation.
- **No secrets in the transcript** — the agent asks the server to authenticate; the certificate and
  captured tokens stay on your side.

## Options

| Option | Purpose |
|---|---|
| `--cert <thumb\|subject>` | The certificate all tools use (pinned) |
| `--cert-file` / `--cert-password` | Pin a certificate from a file instead |
| `--store <location>` | `CurrentUser` (default) or `LocalMachine` |
| `--allow <host>` | Allowed upstream host (repeatable) |
| `--insecure` | Ignore upstream server-certificate errors (internal CAs) |
| `--timeout <seconds>` | Per-request upstream timeout (default 100) |
| `--workspace <file>` | Load saved requests / environments from a workspace file |
| `--no-auto-token` | Don't capture/reuse bearer tokens across the session |

## Examples

```powershell
certapi mcp --cert "CN=Agent Client" --allow api.example.com
certapi mcp --cert 4A8823… --allow api.example.com --allow auth.example.com --insecure
certapi mcp --cert "CN=Agent Client" --allow api.example.com --workspace .\suite.json
```

The server runs until stdin closes. Exit 0 on a clean shutdown, 2 usage, 3 data error.

Next: [CLI Reference](21-CLI-Reference.md).
