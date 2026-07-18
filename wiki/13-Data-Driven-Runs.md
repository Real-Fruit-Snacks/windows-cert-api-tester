# 13. Data-Driven Runs

Run the same request(s) once per row of a dataset, injecting each row's values as `{{variables}}`.
Great for testing an endpoint across many inputs, or a login-then-browse flow per user.

## The idea

Point `certapi run` at a **CSV or JSON** dataset with `--data`. For each row, the row's columns become
variables that fill `{{placeholders}}` in the request(s). One request × N rows = N sends, each
independently pass/fail.

## CSV datasets

The first line is the header (column names → variable names); each later line is a row:

```csv
id,expected
1,200
2,404
```

```powershell
certapi run "api/Get user" --data .\users.csv
```

A request whose URL is `https://{{host}}/users/{{id}}` runs once per row with `id` = 1, 2, … The run
output labels each iteration `[row 1]`, `[row 2]`, ….

The CSV parser is quote-aware, so values containing commas can be `"quoted, like this"`.

## JSON datasets

A JSON array of objects, each object a row:

```json
[
  { "id": 1, "name": "Ada" },
  { "id": 2, "name": "Alan" }
]
```

```powershell
certapi run "api/Create user" --data .\users.json
```

## Strict variables

Pair `--data` with `--strict-vars` so a request that references a column the dataset **doesn't**
supply fails cleanly instead of sending a literal `{{id}}`:

```powershell
certapi run --all --data .\users.csv --strict-vars
```

## Combining with assertions

Each row's send runs the request's [assertions](11-Testing-and-Assertions.md). So a dataset can encode
expected outcomes — e.g. an `expected` column checked by a `Status Equals {{expected}}` assertion —
turning one request into a table-driven test.

## Recording and CI

By default a live-state run records known-good results; a `--workspace` run doesn't unless you add
`--record`. For CI, run a workspace file read-only and emit JSON:

```powershell
certapi run --all --workspace .\suite.json --data .\cases.csv --no-record --strict-vars --json
```

Exit **1** if any row of any request fails.

See the [CLI Reference](21-CLI-Reference.md#run) for every `run` option.

Next: [Endpoint Discovery](14-Endpoint-Discovery.md).
