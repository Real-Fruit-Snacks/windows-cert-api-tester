# Endpoint discovery (fuzzing) + release polish — design

Date: 2026-07-17
Status: approved (autonomous — user delegated approval)

## Goal

Add **endpoint discovery** ("fuzzing") to both the CLI and the GUI: point the tool at a
base URL and a wordlist of candidate endpoints, send a request to each (with the client
certificate and any captured auth token), and report which ones exist and what they return.
This is the standard way to map an undocumented mTLS API. Plus a curated set of quality-of-life
upgrades, refreshed docs/screenshots, and a full 1.27.0 release.

Everything is additive; nothing already shipped changes behavior.

## 1. Core: `EndpointFuzzer` (ApiTester.Core)

A network-agnostic engine so it is unit-testable and reused by CLI and GUI.

**Wordlist parsing — `EndpointList.Parse(string text)` → `IReadOnlyList<EndpointEntry>`:**

- One entry per line. Trim each line; skip blank lines and lines starting with `#`.
- A line is either `PATH` or `METHOD PATH` (e.g. `POST /api/users`). A leading token that is a
  recognized HTTP method (GET/HEAD/POST/PUT/PATCH/DELETE/OPTIONS, case-insensitive) pins that
  entry to that one method; otherwise the entry uses the caller's method set.
- `PATH` may be an absolute path (`/api/users`), a bare path (`api/users`), or a full URL
  (`https://…`), which overrides the base URL for that entry.
- Duplicate (method, path) probes are de-duplicated.

**`EndpointEntry { string Path; string? Method }`** — `Method` null means "use the plan's methods".

**Plan — `FuzzPlan`:**

```
FuzzPlan {
  string BaseUrl;
  IReadOnlyList<EndpointEntry> Entries;
  IReadOnlyList<string> Methods;          // default ["GET"]
  IReadOnlyList<KeyValuePair<string,string>> Headers;   // extra headers (auth etc.)
  int Concurrency;                        // clamped 1..50, default 8
  int DelayMs;                            // per-probe pacing, default 0
}
```

**Result classification — `FuzzOutcome`:** `Found` (2xx), `Redirect` (3xx),
`Unauthorized` (401/403), `MethodNotAllowed` (405 — the endpoint exists), `NotFound` (404),
`ServerError` (5xx), `OtherStatus`, `Error` (transport). Helper `bool IsDiscovery` = true for
everything except `NotFound` and `Error` (i.e. the endpoint probably exists).

**`FuzzResult { string Method; string Path; string Url; int? StatusCode; string? ReasonPhrase;
long SizeBytes; TimeSpan Elapsed; FuzzOutcome Outcome; string? Error }`**

**Engine — `EndpointFuzzer.RunAsync(FuzzPlan plan,
Func<ApiRequest, CancellationToken, Task<ApiResponse>> send, IProgress<FuzzProgress>? progress,
CancellationToken ct)` → `FuzzReport`:**

- Expands entries × methods into probes; each probe composes its URL with the existing
  `RequestUrl.Effective(base, path, …)` logic, honoring full-URL overrides.
- Runs probes with bounded concurrency (`SemaphoreSlim`), optional `DelayMs` pacing, respecting
  `ct`. Ordered `FuzzResult` list returned (input order, stable).
- `FuzzProgress { int Completed; int Total; FuzzResult Last }` reported as each completes.
- `FuzzReport { IReadOnlyList<FuzzResult> Results; counts by outcome; int Discovered }`.

The `send` delegate is built by the caller (CLI/GUI) closing over `ApiClient` + cert + insecure +
timeout + the auto-token attach, so the engine never touches certificates or `TokenService`
directly.

## 2. CLI: `certapi fuzz`

```
certapi fuzz <base-url> -w <wordlist> [options]
```

Options: `-w, --wordlist <file|->` (required; `-` reads stdin), `-X, --methods GET,POST`,
`--cert`, `--store`, `--insecure`, `--timeout`, `--concurrency N`, `--delay <ms>`,
`-H, --header`, `--bearer`, `--env`, `--var`, `--no-auto-token`,
`--match <codes>` / `--hide <codes>` (default hide `404`) / `--all`, `--json`,
`-o, --output <file>` (writes discovered paths as a wordlist, or the full JSON report with `--json`),
`--save-collection <name>` (+ `--workspace <file>`) to save discovered endpoints as saved requests.

- Captured auto-tokens are attached per host exactly like `send`/`run` (unless `--no-auto-token`);
  a token returned by a probe is captured too, so a login endpoint in the list primes the rest.
- Live progress counter on stderr (suppressed by `-q`); results table on stdout sorted
  discovery-first then by status; a summary line (`N probed · D discovered · …`).
- `--json` emits `{ results:[…], summary:{…} }`.
- Exit 0 on completion; **1 only if every probe was a transport error** (host unreachable);
  2/3 usage/data.
- Verbose `Examples:` section and the global `--debug`/`--log-file` note, like every command.

## 3. GUI: Discover panel

- New **"Discover…"** button in the request toolbar (next to Import) opens a `FuzzWindow`
  (own file, dark chrome matching the app).
- Fields prefilled from the active tab: base URL, client certificate, insecure flag, timeout.
  Method checkboxes (GET default), a wordlist file picker (with a "Paste list" text box
  alternative), concurrency, delay.
- **Run** streams results into a grid (path · method · status · size · ms · outcome), color-coded
  by outcome, with a live "n of m" progress bar and a Stop button (cancellation).
- A "Hide 404s / errors" toggle (on by default).
- Double-click a row opens that endpoint in a new request tab (ready to send).
- "Save discovered to collection…" writes the discovery hits into a named collection folder.
- Uses the shared `AppState`, so captured tokens apply and newly captured ones persist.

## 4. Quality-of-life upgrades (curated)

1. **Endpoint discovery** (the above) — CLI + GUI.
2. **Save discovered endpoints → collection** (CLI `--save-collection`, GUI button) — discovery
   becomes reusable saved requests in one step.
3. **Wordlist from stdin** (`-w -`) so `certapi fuzz` composes with other tools in a pipeline.
4. **A bundled starter wordlist** `wordlists/common-api-endpoints.txt` (health, version, users,
   auth, admin, metrics, swagger/openapi, etc.) shipped in the repo and referenced by the docs.
5. **GUI: Ctrl+Enter sends** the active request (added if not already bound), a small but
   expected ergonomic.

Anything beyond this is out of scope for this release.

## Error handling

- Wordlist file missing/empty → data error (CLI exit 3; GUI status message). A wordlist with only
  comments/blanks is "no endpoints to probe".
- Unparseable base URL → usage error before any probe.
- Individual probe transport errors are recorded as `Error` outcomes, never abort the run.
- Cancellation (Ctrl+C / Stop) ends probing promptly and reports what completed.
- `--save-collection` honors the existing GUI-running guard and workspace rules like `import`.

## Testing

Following existing patterns (loopback mTLS server, in-process `CliApp.Run` with fake `CliServices`):

- `EndpointList.Parse`: paths, method prefixes, full-URL override, comments/blanks, dedupe.
- `EndpointFuzzer.RunAsync`: classification for each outcome (fake sender returning scripted
  statuses/errors), concurrency cap respected, order stable, cancellation, progress reported,
  auto-token priming (a probe capturing a token used by a later probe).
- CLI `fuzz`: end-to-end against the loopback server (a wordlist of real + bogus paths →
  discovered vs 404), `--methods`, `--hide/--match/--all`, `--json`, `-w -` stdin, `--output`,
  `--save-collection` writes a collection, `--no-auto-token`, all-errors → exit 1, GUI-running
  guard on save, help has `Examples:`.
- Guard test: `fuzz` help contains `Examples:`, `certapi fuzz`, `--debug`.

## Release (1.27.0)

- CHANGELOG entry, README section + a discovery screenshot/asset, `docs/index.html` Pages card
  and CLI docs, GUI HelpWindow "Discovering endpoints" section, CLI overview/`fuzz` help.
- Version bump to 1.27.0 in both csprojs.
- Run the full suite (including the opt-in store round-trip). Rebuild the GUI; refresh screenshots
  (retake the PNGs where a desktop session allows, and add a design-system SVG for discovery).
- Publish the self-contained CLI + GUI, tag `v1.27.0`, and cut a GitHub release with the binaries;
  the release reflects the entire repo at the tag.
