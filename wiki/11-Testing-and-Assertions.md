# 11. Testing & Assertions

Turn a request into a pass/fail check. Assertions run against the response; a request with any failing
assertion **fails**, which drives exit codes and known-good markers.

## Adding assertions (app)

On the **Tests** tab, add one or more assertion rows. Each has:

- **Target** — what to check:
  - **Status** — the HTTP (Hypertext Transfer Protocol) status code
  - **Time** — the elapsed time in milliseconds
  - **Header** — a response header (name in **Path**)
  - **Body** — a value in the JSON (JavaScript Object Notation) body at a **Path** (e.g. `data.id`)
  - **Body-text** — the raw body as text
- **Operator** — `Equals`, `NotEquals`, `Contains`, `Matches` (regex), `Exists`, `NotExists`,
  `LessThan`, `GreaterThan`
- **Value** — the expected value (not needed for `Exists`/`NotExists`)
- **Enabled** — tick to include the rule

After a send, the Tests tab shows each rule's pass/fail and the actual value.

## Examples

| Target | Op | Path | Value | Checks |
|---|---|---|---|---|
| Status | Equals | | `200` | the call returned 200 |
| Status | LessThan | | `300` | any 2xx |
| Time | LessThan | | `500` | responded within 500 ms |
| Header | Contains | `Content-Type` | `json` | JSON response |
| Body | Equals | `data.ok` | `true` | a JSON field's value |
| Body-text | Matches | | `"id":\s*\d+` | a regex over the whole body |

## The rules never throw

Assertions are designed to **fail cleanly**, never crash the run:

- A malformed regex simply doesn't match (fails a `Matches`).
- A non-JSON body fails any Body-path check rather than erroring.
- A regex is **time-bounded** (2 seconds) so a catastrophic-backtracking pattern fails the assertion
  instead of hanging the run.

## Running assertions headless

`certapi run` evaluates a saved request's assertions:

- A request **passes** when all its assertions pass.
- A request with **no** assertions passes on any **2xx** response (a sensible default health check).
- Failed assertions are listed on **stderr**; the suite exits **1** if any request fails.

```powershell
certapi run "petstore/Get pet by id"     # pass/fail table, exit 1 if anything fails
certapi run --all --json                 # machine-readable results
```

See [Data-Driven Runs](13-Data-Driven-Runs.md) for running assertions across many rows, and the
[CLI Reference](21-CLI-Reference.md#run) for all `run` options.

## In CI (continuous integration)

Because a failing assertion exits non-zero, a saved suite is a ready-made smoke test:

```powershell
certapi run --all --workspace .\suite.json --no-record --json
if ($LASTEXITCODE -ne 0) { throw "API smoke tests failed" }
```

Next: [Capturing Values](12-Capturing-Values.md).
