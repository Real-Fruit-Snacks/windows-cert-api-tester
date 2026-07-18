# 15. Live Streaming (WebSocket & SSE)

Beyond request/response: connect to a **WebSocket** and exchange messages, or watch a **Server-Sent
Events (SSE)** stream arrive. Both reuse your selected client certificate and the insecure toggle, so
they work against mTLS (mutual Transport Layer Security) endpoints.

## In the app

The **Stream** button on the request line opens a live console. It picks the protocol from the URL
(Uniform Resource Locator) scheme:

- `ws://` or `wss://` → **WebSocket** — type a message and press Enter (or **Send**); every message
  the server sends back appears in the transcript, tagged with time and direction.
- `http://` or `https://` → **Server-Sent Events** — each event is appended as it arrives, with its
  event name (if any) and data.

**Connect** / **Disconnect** control the session; **Clear** empties the transcript. Closing the window
disconnects. A live indicator shows which mode the current URL will use.

## WebSocket on the CLI (command-line interface)

```powershell
# Send messages and wait for a set number of replies (good for scripts)
certapi ws wss://api.example.com/socket --cert "CN=My Client" -m '{"sub":"prices"}' --expect 3

# Pipe messages in on stdin
echo '{"ping":1}' | certapi ws wss://api.example.com/socket --expect 1
```

- **`-m, --message <text>`** — send this after connecting (repeatable). Lines piped on **stdin** are
  also sent.
- **`--expect <n>`** — stop after receiving `n` messages (deterministic for scripts). With no
  `--expect`, the client listens until the server closes or `Ctrl+C`.
- **`-H`** handshake headers, and the usual `--cert` / `--cert-file` / `--insecure`.

Received messages go to **stdout**; notices to **stderr**. Exit 0 on a clean close.

## Server-Sent Events on the CLI

```powershell
certapi sse https://api.example.com/events --cert "CN=My Client"
certapi sse https://api.example.com/stream --max-events 5 --json
```

- **`--max-events <n>`** — stop after `n` events.
- **`--json`** — print one JSON (JavaScript Object Notation) object per event
  (`{event,data,id,retry}`), i.e. ndjson (newline-delimited JSON).
- **`-H`** request headers, plus the usual cert/insecure flags.

Events go to stdout; the connecting/ended notices to stderr.

## Try it locally

The [mock server](18-Mock-Server.md) serves both: `/sse` streams a few events, and a WebSocket echo
runs on any path. So you can exercise both clients without a real service:

```powershell
certapi mock --port 8770
certapi sse http://127.0.0.1:8770/sse --max-events 3
certapi ws  ws://127.0.0.1:8770/ws -m "hello" --expect 1
```

Next: [Response Views](16-Response-Views.md).
