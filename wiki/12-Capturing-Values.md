# 12. Capturing Values

Pull a value out of a response and reuse it in later requests — the classic "log in, then call the
API (application programming interface) with the token" pattern, without copy-paste.

## Automatic bearer tokens (zero setup)

The most common case is handled for you. When a response contains a bearer token, the app captures it
automatically — see [Authentication → Auto](08-Authentication.md). You usually don't need a capture
rule at all: call your login endpoint, then send to the same host, and the `Authorization: Bearer …`
goes out on its own.

Reach for explicit capture rules when you need a **different** value, or a value used somewhere other
than the Authorization header.

## Capture rules (app)

On the **Capture** tab, add a rule: after the request succeeds, save a value from the response into a
`{{variable}}`.

- **Variable** — the name to save into, e.g. `token` (reuse it as `{{token}}`).
- **From** — **Body** (a JSON (JavaScript Object Notation) path like `access_token` or
  `data.session_id`) or **Header** (a response
  header name).
- **Path** — the JSON path or header name.

The captured value becomes a variable in the current environment, so any later request can use
`{{token}}` in a header, URL (Uniform Resource Locator), or body.

## Capture on the CLI

`--capture var=path` (repeatable) saves a response value after the send. A `header:Name` path captures
a header instead of a body value:

```powershell
# Log in and capture the token into a workspace, then reuse it
certapi send https://api.example.com/login -X POST -d '{"user":"me","pass":"..."}' `
  --capture token=access_token --workspace team.json

certapi send https://api.example.com/orders --workspace team.json `
  -H "Authorization: Bearer {{token}}"

# Capture a header value
certapi send https://api.example.com/login --capture sid=header:X-Session-Id
```

## JSON paths

A body path walks the JSON: `access_token` (top level), `data.token`, `result.items` — dotted
navigation into objects. Non-JSON bodies or missing paths simply fail that capture (reported, never
fatal).

## Chaining requests

Capture is what makes a **suite** cohesive: one request logs in and captures `{{token}}` (or relies on
automatic tokens), and the rest of the collection uses it. In a `certapi run`, a token captured by an
early request is reused by the later ones — add `--cookies` to also carry a `Set-Cookie` session
across the run.

## Session cookies

Separately from tokens, the app keeps a **cookie jar** like a browser: a `Set-Cookie` in any response
is stored and sent back on later requests to that host, so cookie-based logins work across a session.
On the CLI (command-line interface), `certapi run --cookies` keeps a jar for the run.

Next: [Data-Driven Runs](13-Data-Driven-Runs.md).
