# 7. Building Requests

Method, URL (Uniform Resource Locator), query parameters, headers, and the body — plus multipart
uploads and GraphQL.

## Method and URL

Pick a method (GET/POST/PUT/PATCH/DELETE/HEAD/OPTIONS) and type the URL. A few conveniences:

- Type a `?query=string` directly in the URL box and it **splits into the Params grid** when you send,
  correctly encoded.
- If you've saved a **base website** (see [Collections & History](10-Collections-and-History.md)), the
  URL box holds just the path after it — fire `/api/orders` without retyping the host.
- `{{variables}}` in the URL are resolved against the active
  [environment](09-Environments-and-Variables.md).

On the CLI (command-line interface) the URL is a positional argument:

```powershell
certapi send "https://api.example.com/search?q=abc" -X GET
```

## Query parameters

The **Params** tab is a key/value grid. Ticked rows are included and recombined onto the URL, encoded,
at send time. On the CLI, just put them in the URL (quote it in PowerShell).

## Headers

The **Headers** tab is a key/value grid; tick a row to include it. On the CLI, repeat `-H`:

```powershell
certapi send https://api.example.com/x -H "Accept: application/json" -H "X-Trace: 1"
```

Some headers (like `Authorization`) are managed for you when you use an [auth type](08-Authentication.md)
— you don't have to set them by hand.

## The body

The **Body** tab offers two modes:

### Text body

Free-form text (JSON (JavaScript Object Notation), XML (Extensible Markup Language), plain text, …).
Choose the **Content-Type** (default `application/json`;
`(none)` sends no body/content-type). On the CLI:

```powershell
certapi send https://api.example.com/users -X POST -d '{"name":"Ada"}'
certapi send https://api.example.com/users -X PUT --data-file .\payload.json
certapi send https://api.example.com/x -X POST -d 'raw text' --content-type text/plain
```

`(Examples use PowerShell quoting; in cmd.exe write JSON as `"{\"name\":\"Ada\"}"`.)`

### Multipart form (file uploads)

Switch the Body tab to **Form** to build a `multipart/form-data` body: a grid of parts, each a text
field or a file (tick **File** and browse). This implies POST.

On the CLI use `-F` (repeatable; `name=@path` uploads a file, `;type=` sets its content type):

```powershell
certapi send https://api.example.com/upload -F "notes=cover page" -F "file=@.\report.pdf"
certapi send https://api.example.com/upload -F "img=@.\logo.png;type=image/png"
```

Multipart and `-d`/`--data` are mutually exclusive.

## GraphQL

Send a correctly-formed GraphQL request (a JSON `{ query, variables }` POST) with `--graphql`:

```powershell
certapi send https://api.example.com/graphql `
  --graphql "query(`$id:ID!){ user(id:`$id){ name } }" `
  --gql-variables '{"id":1}'
```

`--gql-variables` must be a JSON object; the query is escaped for you.

## Copy the request as code

In the response toolbar (next to **Copy body**), **Copy as ▾** turns the current request into a
ready-to-run snippet — **cURL**,
**PowerShell** (`Invoke-RestMethod`), **Python** (`requests`), or **C#** (`HttpClient`) — with
`{{variables}}` resolved and headers/body included. Handy for sharing a repro or moving a call into
code.

## Timeout

Set the per-request timeout (seconds) on the request line, or `--timeout <seconds>` on the CLI
(default 100).

Next: [Authentication](08-Authentication.md).
