# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.63.0] - 2026-07-27

### Added
- **A new `--noproxy <list>` flag lists hosts to reach directly instead of through the proxy**,
  using the entry forms the widely-understood `NO_PROXY` environment-variable conventions already
  establish, so existing knowledge transfers: a bare hostname (`internal.corp`) matches that host
  and its subdomains but not a look-alike (`notinternal.corp`); a leading-dot or wildcard suffix
  (`.corp`, `*.corp`) matches the domain and its subdomains and not a domain that merely contains it
  (`internal.corp.evil.com`); an IP address literal (`10.1.2.3`, `::1`) is matched as an address,
  never as a suffix; a Classless Inter-Domain Routing (CIDR) range (`10.0.0.0/8`, `fd00::/8`) is
  accepted, but one written with host bits set (`10.0.0.5/8`) is refused rather than quietly masked;
  `*` alone bypasses everything; matching is case-insensitive; and an optional `:port`
  (`internal.corp:8443`, `[::1]:8443`, `*:8080`) must also match when present. An entry that cannot
  be understood is refused with a message naming it (exit 2) rather than silently dropped, because a
  dropped entry would leave internal traffic leaking through the proxy while the user believed it
  was bypassed. It is shared by `send`, `run`, `fuzz`, `bench`, and `serve` (they share one
  transport-flag parser), and is also accepted by `grpc`.
- **The bypass list narrows an existing proxy rather than replacing it** — with the system proxy it
  narrows the machine's proxy (including Web Proxy Auto-Discovery (WPAD) and a proxy
  auto-configuration (PAC) script), and with `--proxy` it narrows the named proxy instead. Combining
  `--noproxy` with `--no-proxy` is a usage error (exit 2): there is no proxy left for `--noproxy` to
  narrow, so the combination is refused rather than one of the two flags being silently ignored.
- **`NO_PROXY` (falling back to `no_proxy`) is honored when `--noproxy` is not given**, because that
  is what the rest of the ecosystem does and a corporate machine often already sets it. Precedence
  when more than one source could apply: an explicit `--noproxy` wins, then a saved request's own
  bypass list, then `NO_PROXY`. The environment is not consulted at all when `--no-proxy` is given,
  since the user named no bypass list in that case.
- **A request that went direct because a bypass rule matched is now distinguishable from one that
  never had a proxy at all.** `--debug` prints `proxy no (bypassed by '<rule>')`, and the app's
  Diagnostics panel gains a `Bypassed by` line naming the rule. Going direct also restores the TLS
  diagnostics a proxy hides — version, cipher, and “client certificate presented” — so a bypassed
  host reports them where a tunnelled one cannot.
- **The bypass applies everywhere the proxy applies**: `send`, `run`, `fuzz`, `bench`, `mcp`, the
  app, `certapi serve`, and `certapi grpc` — all three of the places that configure an HTTP
  handler's proxy now share one implementation, so the bypass cannot apply on one path and not
  another.
- **A BYPASS box on the request editor's Transport tab** lets the app set the same per-host list —
  saved with the request and round-tripped through workspaces like the other transport settings.

## [1.62.0] - 2026-07-27

### Added
- **Secrets in the workspace file are now encrypted for the Windows user who saved them, instead of
  sitting in plain text.** A captured bearer token, a browser-captured session cookie, the auth
  secret saved on an open tab, a history entry, or a saved collection request (the Basic-auth
  password or bearer token), and an environment variable value marked **secret** are each encrypted
  in place — as the string `enc:v1:<base64>` in the same JSON property they always occupied. Keys,
  names, URLs, headers, and bodies are left exactly as they were, on purpose: the file stays
  diffable and readable for everything that isn't a credential.
- **Encryption is per current Windows user, through the Windows Data Protection API (DPAPI)**,
  called directly against `crypt32.dll` rather than through a package, so `ApiTester.Core` still
  carries zero NuGet references. The call is always made with prompting forbidden, so a headless
  `certapi` run can never hang waiting for a dialog that will never appear, and scope is always the
  user, never the machine.
- **A new `Secret` flag on an environment variable** marks a value for encryption; tick **secret**
  on any variable in the Environments window to opt it in. A captured value gets the flag
  automatically, because capturing a value into an environment is exactly the path that lands a
  token there in the first place.
- **The first time an older workspace is rewritten in the new format, the previous file is copied
  beside itself first**, as `state.json.<yyyyMMdd-HHmmss>.bak` — so upgrading a workspace can't cost
  you the plain-text copy you had before. The app says so in its status line, both before the
  upgrade (while the file still stores secrets in the clear) and again once the rewrite has
  happened, naming the backup it kept.
- **A secret that cannot be decrypted degrades instead of crashing anything.** Because protection is
  scoped to one Windows user, a workspace file carried to a different user or a different machine
  cannot have its secrets read back. Each one is treated as absent — a captured token or cookie is
  dropped, an auth secret or secret variable is left empty — a warning names the field, and every
  other part of the workspace still loads: requests, collections, chains, history, and environments
  are all unaffected. `certapi`'s exit codes are unchanged by this: it's a warning, not a failure, so
  the run still exits 0; only a workspace that cannot be read at all is still a data error (exit 3).

### Changed
- **`certapi export workspace` now strips secrets by default**, the same way HTTP Archive (HAR)
  export already redacts them, because an exported workspace is a file people end up emailing to
  each other. Captured tokens and cookies are removed, saved auth secrets are cleared, and a secret
  environment variable keeps its key and its flag but loses its value — the exported workspace still
  opens and works, just without the credentials. Pass `--include-secrets` to keep them; they are
  still written encrypted for the current Windows user, so even then there is no way to write a
  secret to disk in the clear. The desktop app's **Export workspace…** always writes secrets through
  that same encrypting path — the strip-by-default behavior is `certapi export workspace`'s alone.
- **The on-disk workspace format changed: schema version 2.** An older file loads exactly as before
  and is upgraded — encrypted, backed up first as described above — on its next save; nothing has to
  be done by hand. The one visible consequence: a workspace file copied to another Windows user or
  another machine now loses its secrets on that load (and only its secrets — everything else in it
  is intact), because encryption that traveled with the file would not really be protecting anything.

## [1.61.1] - 2026-07-27

### Fixed
- **`certapi serve` no longer accepts a header rule with an empty name and then silently drops
  it.** `--request-header " : v"` used to start the gateway and log `listening` while the name
  parsed to the empty string: nothing refused it, and at forward time the HTTP stack discarded the
  nameless header, so the upstream never saw it and the operator had no way to know the rule had
  not applied. An empty or whitespace-only name is now a usage error (exit 2) on all four flags —
  `--request-header`, `--remove-request-header`, `--response-header`, `--remove-response-header` —
  naming the flag and the shape it expected. A name carrying a character that is not legal in an
  HTTP field name, such as a space or an embedded colon, is refused the same way and for the same
  reason: the header could never match, so the rule would be dropped rather than applied. This is
  the rule the previous release already applied to the framing headers and `Host` — a rule that is
  accepted is a rule that takes effect.
- **A future edit can no longer make the gateway's two header sets disagree in silence.** The
  hop-by-hop names the gateway never relays are stripped *after* the header rules have been
  applied, so a name added to that list but not to the list of headers a user may not manage would
  have become a rule `serve` accepts on the command line and then throws away — the defect above,
  reintroduced by an edit nobody would connect to it. The two sets stay separate on purpose,
  because they answer different questions, but the hop-by-hop names are now exposed read-only and
  a test pins every one of them as a name the header rules refuse, failing with a message that
  explains the trap rather than inviting someone to silence it.

## [1.61.0] - 2026-07-27

### Added
- **`certapi serve --browser`'s `--cors` now answers Chrome's Private Network Access (PNA)
  preflight**, closing a failure that presented as "the gateway just doesn't work" with no visible
  cause: a page on a public origin calling a private or loopback address gets a further preflight
  carrying `Access-Control-Request-Private-Network: true`, and Chrome blocks the request before any
  gateway logic runs unless the response answers `Access-Control-Allow-Private-Network: true`. Since
  `certapi serve --browser` binds `127.0.0.1` specifically so a web page can call it, this was the
  gateway's headline use case failing silently in the most common browser.
- **The PNA header is emitted only when three things hold together**: Cross-Origin Resource Sharing
  (CORS) handling is on, the preflight actually carried the PNA request header, and the request's
  `Origin` passes the existing allowlist check (`--cors <origins>`, or echo mode when no list was
  given) — never unconditionally, and never for an origin the existing policy would refuse. The
  allowlist stays the one security boundary for both concerns; an origin outside an explicit
  allowlist still gets a bare 403 with no headers at all. Letting a public origin reach a loopback
  service at all is a real exposure regardless of PNA, which is why naming the origins you develop
  from with `--cors <origins>` is the safer form than leaving it echoing whoever asks.
- **New `--cors-max-age <seconds>`** replaces a hardcoded 600-second `Access-Control-Max-Age`; 600
  is still the default, so nothing changes for an existing `--cors` user. `0` is legal — it tells the
  browser not to cache the preflight answer at all — and a non-numeric or negative value, or the flag
  given without `--cors`/`--browser`, is a usage error (exit 2).
- **Four new repeatable flags manipulate headers on forwarded traffic**: `--request-header
  "Name: value"` and `--response-header "Name: value"` replace a header if one was already sent and
  add it otherwise; `--remove-request-header <name>` and `--remove-response-header <name>` strip one.
  Naming the same header to both a set flag and a remove flag on the same side removes it — removal
  wins over setting.
- **The rules work with or without `--browser`**, because a header rule is not a browser concern:
  they live in their own pure `ApiTester.Core` type, `HeaderRules`, applied on the forwarding path
  regardless of browser mode. On the response side they apply after `--browser`'s own rewrites (CORS,
  cookies, `Location`), so a header configured explicitly here wins over one the gateway injected —
  someone overriding `Access-Control-Allow-Origin` on purpose gets their value.
- **`Connection`, `Keep-Alive`, `Transfer-Encoding`, `Content-Length`, `TE`, `Trailer`, `Upgrade`,
  `Proxy-Authenticate`, `Proxy-Authorization`, and `Host` are refused with a usage error naming the
  header and why**, rather than silently ignored: the first nine frame the HTTP message and the HTTP
  stack manages them; `Host` is set by the gateway's own HTTP client from the upstream URI, so a rule
  for it would only ever half-apply.
- **The default relay stays byte-faithful, pinned by a characterization test**: with none of the new
  flags the gateway forwards exactly as it did before. The header rules act on forwarded HTTP traffic
  only — never on a CORS/PNA preflight the gateway answers itself, its own 404/400/502 error pages,
  or a relayed WebSocket upgrade.

## [1.60.0] - 2026-07-27

### Added
- **`certapi grpc call` now handles all four gRPC method kinds — unary, server-streaming,
  client-streaming, and bidirectional** — closing the one hole left after `--protoset` shipped in
  1.58.0. Which of the four a method is comes from the service's own definition, discovered from its
  descriptor (reflection-fetched or supplied with `--protoset`), never declared by the user with a
  flag: a flag for it would just move the tool's own bug — guessing wrong — onto the user instead of
  fixing it.
- **Multi-message input mirrors `certapi ws`, which had already solved this problem**: `-d`/`--data`
  is now repeatable, and for a client-streaming or bidirectional method each `-d` value, then a
  `--data-file` message if given, then each line read from standard input (one JSON object per line,
  a whitespace-only line skipped so a trailing newline in a pipe never becomes an empty message) is
  sent as one message, in that order. Standard input is consulted only for those two kinds — a unary
  or server-streaming call never reads it, so a single `-d` against a unary method behaves exactly as
  it did before, and piping something into one can neither hang the call nor change its request.
- **Two new rules at the command line make the four-kind dispatch honest rather than silently
  wrong.** Supplying several `-d` values to a method that takes a single request message (unary or
  server-streaming) is now a usage error — exit 2, naming the method and its actual kind — rather
  than silently sending only the first. Supplying no messages at all to a client-streaming or
  bidirectional method is legal: it sends an empty stream, not an error.
- **A bidirectional call sends and receives concurrently — sending never blocks receiving.**
  Responses print as they arrive, one compact JSON object per line, exactly like a server-streaming
  call; the call ends when the server completes, `--max-messages` is reached (exit 0, not a
  failure), or Ctrl+C. It is not an interactive read-eval loop. The interleaving is proved by an
  assertion on the order of observed events — sent m0, received m0, sent m1, received m1 — rather
  than by a clock, alongside failing variants of the two new kinds added to the test fixture.
- **A non-OK gRPC status reaches exit code 1 on every one of the four kinds.** Under the hood: unary
  and client-streaming return the status as data; server-streaming throws after yielding whatever
  arrived; bidirectional returns the status as data after every message that did arrive has already
  been printed. Exit codes are unchanged otherwise — 2 for a bad command line, 3 for a data problem,
  never a stack trace.
- **`--protoset` drives all four kinds identically to server reflection**, pinned by tests that call
  a client-streaming and a bidirectional method against a server with no reflection service at all —
  and the well-known Protobuf types keep their canonical JSON rendering through any of the new
  streaming kinds (a `Timestamp` is still `"2023-11-14T22:13:20Z"`, never
  `{"seconds":...,"nanos":...}`), whether the descriptors came from reflection or from `--protoset`.
- **The one remaining true limit is unchanged**: `certapi serve` does not proxy gRPC, because
  `HttpListener` (what the gateway is built on) is HTTP/1.1-only — `certapi grpc` reaches the service
  directly with your certificate instead of going through the gateway. `--protoset` still only helps
  if you already have, or can produce, the descriptor set yourself — certapi does not compile
  `.proto` sources.

## [1.59.0] - 2026-07-26

### Added
- **Chains now run in the app, through a "▶ Run chain" button in the CHAINS sidebar**, closing a gap
  that had been there since chains first shipped: you built the thing in the window, then
  switched to a terminal to use it. A characterization suite was written first, not after —
  `tests/ApiTester.Tests/Cli/ChainCharacterizationTests.cs`, 25 tests pinning `certapi run --chain`'s
  observable behavior (the per-step PASS/FAIL line shape, the SKIP line, the footer, the verbatim
  stop-on-failure message on stderr, every exit code — 0 all passed, 1 any step failed, 3 for an
  unknown chain listing the ones that exist, 3 for an empty chain, 3 for a step naming a deleted
  request before anything is sent — the exact property set of the `--json` envelope, known-good
  recording per step, captures landing in the chain's environment, and stop-on-failure in both
  directions) before a single line of the runner moved. That file was left unedited through the whole
  refactor that followed and is still green today — it is the evidence, not an assertion, that the
  command line did not change underneath this release.
- **Chain execution was extracted out of the command line into `ApiTester.Core`**, because the
  alternative — copying the send / capture / rebuild-variables path into the app so it could have its
  own "run" button — was rejected outright: two copies of that logic drift the moment either one is
  touched, and a chain that passes in one front end and fails in the other is exactly the defect a
  shared engine exists to rule out. The new public Core surface is `RequestRunner.RunAsync` for the
  per-request path (resolve variables, attach auth, find the certificate, apply transport, send,
  capture tokens, record known-good, evaluate assertions, apply capture rules, rebuild variables),
  `RequestRunContext` and `RunVariables` for the run-wide choices and the front end's own seams,
  `ChainRunner` (`Find` / `Resolve` / `PrepareCaptureEnvironment` / `RunAsync`) for the chain itself,
  `AssertionEvaluator.RequestPassed` as the single definition of "this request passed", and
  `RedirectReport.Lines` for the shared redirect-hop text. The runner reports progress through
  `IProgress<ChainRunProgress>` and honors a `CancellationToken` — it has no WPF and no `Console`
  reference, so `ApiTester.Core` still carries zero NuGet package references, and `certapi run
  --chain` became a thin caller of it: `RunCommand.cs` lost 228 lines and gained 46, with byte-identical
  behavior proved by the characterization suite above.
- **The app's run window shows a per-step verdict as the chain executes**, not just a final result:
  `ChainRunWindow` lists one row per step in run order, each turning PASS, FAIL, or SKIP with its
  status, time, and size as that step completes, so you can watch a five-step chain land instead of
  waiting on it blind. Selecting a row shows that step's actual response — pretty-printed with the
  same formatter and syntax highlighter the main response panel uses — alongside its notes and any
  failing assertions, so "what did step 3 actually get back" is a click, not a re-run with `--debug`.
  The run is asynchronous, so the window stays responsive while it works, and a Stop button (or
  closing the window) cancels a run in progress.
- **Captures and known-good behave in the app exactly as they do headless**, because both paths are
  now the same code: the chain's capture environment is created on first use and made active, a
  value captured by step one is visible to step two as a `{{variable}}`, a newly created environment
  is folded into the environment picker rather than silently vanishing, and known-good markers are
  recorded per step onto the saved requests the sidebar already holds. The run window never writes
  `state.json` itself — the app persists on close exactly as it always has, so nothing is lost or
  double-written by adding a second place that runs a chain.
- **One deliberate difference between the two front ends, stated here because a user will hit it and
  can't be expected to guess it:** when a chain names no capture environment, `certapi run --chain`
  resolves `{{variables}}` against nothing, while the app resolves them against whichever environment
  is selected in its own picker — because in the app that picker is the user's stated choice, and
  every other send in the window already honors it.

## [1.58.0] - 2026-07-26

### Added
- **`--protoset <file>` on both `certapi grpc list` and `certapi grpc call`**, so a service that does
  not implement server reflection — common in production, where it is often turned off deliberately —
  is no longer a hard wall. The file is a compiled `FileDescriptorSet`: the binary output of
  `protoc --descriptor_set_out=<file> --include_imports <proto>`. `--include_imports` is not optional
  — without it the set carries the root file but not the types it imports, including the well-known
  types — and the flag itself is named to match `grpcurl -protoset`, which consumes the identical
  interchange format, so the knowledge transfers directly. A descriptor set wins over server
  reflection whenever both are possible; the two are never merged, not even to fill in something the
  set happens to lack. Proved by test: against a reflection-enabled server — one that, by definition,
  advertises `grpc.reflection.v1alpha.ServerReflection` as one of its own services — a `--protoset`
  listing never includes that service, because the supplied set does not declare it. `certapi grpc
  list --protoset <file>` works entirely offline: the address argument is optional in that one
  combination, and one supplied alongside `--protoset` anyway is accepted and ignored rather than
  dialed. Proved by a test in which no listener is started anywhere in the process. The headline case
  is pinned directly: one in-process gRPC server started with reflection disabled — `certapi grpc
  call` against it fails with the reflection-unavailable error, and the identical call with
  `--protoset` succeeds. Every descriptor-set problem is exit 3 with a plain message, never a stack
  trace: a missing file, an unreadable file, a file that is not a parseable `FileDescriptorSet`,
  forgetting `--include_imports` (the message names the specific missing file and tells you to
  re-run `protoc` with the flag), passing the `.proto` source by mistake (detected and named: "looks
  like a .proto source file, not a compiled descriptor set", with the `protoc` command to compile
  it), and a `Service/Method` the set does not declare (naming the services the set does declare).
  The well-known Protocol Buffers (Protobuf) types — `Timestamp`, `Duration`, the wrapper types,
  `Struct`/`Value`/`ListValue`, `FieldMask`, `Empty`, and `Any` — render and are accepted in their
  canonical JSON forms identically through this path, because that handling keys off the descriptor's
  full type name, not off where the descriptor came from; a test round-trips a `Timestamp` and a
  `Duration` through `--protoset` against a reflection-disabled server to pin exactly that.

### Changed
- **The reflection-unavailable error was corrected, because this release made the old wording false.**
  It used to end "supplying a compiled descriptor set instead is not available in this version"; it
  now reads, verbatim: "The server does not implement gRPC server reflection
  (grpc.reflection.v1alpha.ServerReflection), so certapi grpc cannot learn the service's request and
  response message types by asking it. Supply a compiled descriptor set instead: --protoset <file>,
  produced by protoc --descriptor_set_out=<file> --include_imports <proto>."
- **The desktop application's Help window gRPC note was corrected to match**: it used to say "there's
  no way to supply a compiled descriptor set instead" for a server with reflection turned off; it now
  says that server can still be reached by supplying a compiled descriptor set with `--protoset`.
- **The documented gRPC limits were rewritten, not merely trimmed.** "Server reflection is required
  (there's no descriptor-set input in this version)" is no longer true and is gone from `README.md`,
  `docs/index.html`, and `wiki/21-CLI-Reference.md`; what remains stated as a limit is what is still
  actually true — client-streaming and bidirectional methods are still out of scope, `certapi serve`
  still does not proxy gRPC (`HttpListener` is HTTP/1.1-only), and `--protoset` still requires you to
  already have, or be able to produce, the descriptor set yourself — certapi does not compile `.proto`
  sources.

## [1.57.0] - 2026-07-26

### Changed
- **`ApiClient` now pools and reuses HTTP connections instead of opening a fresh one for every
  send.** It used to construct a brand-new `SocketsHttpHandler` and `HttpClient` inside every
  `SendAsync` call, so a 20-request suite performed 20 TLS (Transport Layer Security) handshakes
  and no connection was ever reused. That was never an oversight left for later — the handshake
  diagnostics (negotiated TLS protocol, cipher suite, whether the client certificate was presented,
  the server certificate and chain) were captured into closure variables created fresh for each
  call, which bound the handler carrying the connect callback to that one call; sharing a handler
  across calls would have meant one call's diagnostics silently overwriting another's. This release
  moves the capture from *per send* to *per connection*: a connect callback keyed by origin now
  runs once, when a connection is established, rather than once for every request that happens to
  use it, so the handler no longer has to be rebuilt to keep the diagnostics honest. `ApiClient` now
  owns a bounded (capacity 8) Least Recently Used (LRU) cache of handlers, keyed by everything baked
  into a handler — the client certificate (by SHA-256 hash, plus whether it carries a private key),
  proxy configuration, `IgnoreServerCertificateErrors`, the trust callback's identity (via
  `TrustPredicates`, which now memoizes one delegate instance per host — the reason repeated
  requests to the same host can be recognised as reusable at all), decompression, HTTP version,
  `--resolve` overrides, and Windows-auth credentials. Two requests share a connection only when
  every one of those matches exactly; the key is deliberately conservative, because a wrong answer
  here — reusing a connection across two different client certificates, say — would be a security
  defect, not a performance bug. A request never reuses a connection established with a different
  client certificate or a different trust policy. A request routed through a proxy still opens its
  own connection and is never pooled: the CONNECT tunnel's TLS is established by the handler itself,
  through a handler-wide callback that can't be attributed to the one connection it belongs to, so
  there is nothing safe to key a shared handler on for that path. Proved by test, not asserted: two
  equivalent sends to one origin through one `ApiClient` cause the server to complete exactly one
  TLS handshake while answering both requests; a `certapi run` suite of two requests against one
  host now opens one connection instead of two; a second request over a pooled connection still
  presents the client certificate (verified server-side); and two different client certificates, two
  different trust policies, two different sets of Windows-auth credentials, or two different
  decompression settings each open their own connection rather than share one.
- **What `ConnectionInfo` reports changed, on purpose, and it's user-visible.** With real pooling, a
  second request over an existing connection performs no handshake, so there is nothing fresh to
  report. `ConnectionInfo` now describes *the handshake that established the connection currently in
  use*, not a handshake that just happened for this particular request — a second send to an
  already-pooled origin shows the same negotiated protocol, cipher suite, and
  client-certificate-presented value as the first, because that is what is actually true of the
  connection carrying it, not because nothing was captured this time. This is more truthful than the
  old behavior, which only ever showed fresh handshake data because it forced a fresh handshake on
  every single request whether or not one was needed. Diagnostics are now a property of the
  connection rather than of the individual request; the app's Diagnostics tab and `certapi bench`'s
  own caveat are updated to say so, in `README.md`, `wiki/06-Certificates-and-mTLS.md`, and the
  in-app Help window.
- **Cookies moved off the handler to per-send handling**, so one caller's cookie jar can never leak
  into another caller's request by way of a handler now shared across sends.
- **`ApiClient` is now `IDisposable`.** The app disposes its instance when the main window closes,
  and `Bench` and `SelfTestRunner` dispose the ones they create — the handler cache a long-lived
  instance now holds is a real resource, not a stateless helper.
- **The bench caveat changed to match.** `certapi bench`'s old caveat — "each request opens its own
  connection, so these figures include connection setup" — is no longer true and is replaced
  everywhere it appeared (the CLI summary, the `--json` envelope, `certapi bench --help`,
  `Bench.RunAsync`'s remarks, the app's Help window, `wiki/21-CLI-Reference.md`, `README.md`, and
  `docs/index.html`): connections are pooled and reused, so only the first request to an origin pays
  the TCP connect and TLS handshake, `--warmup` exists to discard exactly that cost, and a request
  routed through a proxy still opens its own connection every time.

## [1.56.0] - 2026-07-26

### Added
- **`certapi grpc call` renders and accepts the well-known Protobuf types in their canonical
  JavaScript Object Notation (JSON) forms.** Google.Protobuf for C# has no `DynamicMessage`: a
  descriptor built at runtime from server-reflection bytes has no compiled .NET type behind it,
  so Google's own `JsonFormatter`/`JsonParser` cannot operate on it, which is why `certapi grpc`
  carries its own hand-rolled, descriptor-driven converter (`ProtoJsonWriter`/`ProtoJsonReader`
  in `ApiTester.Grpc`). Until now that converter treated every well-known type as an ordinary
  nested message; this release implements the canonical mapping from the Protocol Buffers
  (Protobuf) JSON specification (protobuf.dev, "ProtoJSON Format"), in both directions — a
  response renders in canonical form, and a request body supplied in canonical form encodes
  correctly onto the wire. `Timestamp` is an RFC 3339 string, always UTC `Z`, with 0, 3, 6, or 9
  fractional digits (`"2023-11-14T22:13:20.5Z"`), and accepts a numeric UTC offset on input and
  converts it; `Duration` is seconds with an `s` suffix (`"1.500s"`, `"-3s"`); the nine wrapper
  types (`DoubleValue`, `FloatValue`, `Int64Value`, `UInt64Value`, `Int32Value`, `UInt32Value`,
  `BoolValue`, `StringValue`, `BytesValue`) render as the bare underlying value —
  `Int64Value`/`UInt64Value` as a JSON string per the existing 64-bit-integer rule, `BytesValue`
  as base64 — and a wrapper holding its type's default still renders as that default (`0`, `""`,
  `false`), which is what distinguishes a present-but-default wrapper from an absent one;
  `Struct` is a plain JSON object, `Value` is any JSON value including `null`, and `ListValue` is
  a JSON array; `FieldMask` is comma-joined lowerCamelCase paths (`"user.displayName,photo"`),
  converted to and from the wire's snake_case; and `Empty` is `{}`. `Any` renders as an object
  with `"@type"` plus the payload's fields inlined, or the payload nested under `"value"` instead
  when it is itself a well-known type — and the expansion is best-effort: when the type URL
  resolves against the descriptors reflection fetched, the payload is expanded; when it doesn't,
  or its bytes don't decode, the field degrades to `{"@type":…,"value":"<base64>"}` rather than
  failing the call, so one unresolvable `Any` never sinks a whole response. Correctness is proved
  differentially rather than against hand-written expectations: the tests compare our writer's
  output against Google's own `JsonFormatter`, and our reader's encoding against `JsonParser`,
  over descriptors rebuilt at runtime from reflection bytes — the real production condition.
  Malformed input is a clean data error, not a crash: an unparseable timestamp, duration, or
  field mask in a request body exits 3 naming the offending field, for example `'all.fTimestamp':
  'not-a-time' is not a valid RFC 3339 timestamp (google.protobuf.Timestamp expects e.g.
  "2023-11-14T22:13:20Z").`

### Changed
- **This changes the shape of `certapi grpc call`'s output — a script parsing the old form will
  see something different.** A `google.protobuf.Timestamp` that rendered as
  `{"seconds":1700000000,"nanos":0}` now renders as `"2023-11-14T22:13:20Z"`; a `StringValue`
  that rendered as `{"value":"x"}` now renders as `"x"`; a `Duration` that rendered as
  `{"seconds":1,"nanos":500000000}` now renders as `"1.500s"`. The request side gains the same
  forms: a body may now supply these canonical shapes, and the old object forms are still
  accepted for backward compatibility — except for `Struct` and `Value`, where a plain JSON
  object is itself the canonical form, so `{"fStruct":{"fields":{...}}}` is now read as a
  `Struct` whose one field is literally named `"fields"`, not as the old object-wrapped shape.
  That exception isn't a shortcut taken here: it is exactly what Google's own `JsonParser` does
  for these two types, and it is worth stating plainly rather than leaving it to be discovered by
  surprise.

## [1.55.1] - 2026-07-26

### Fixed
- **Exporting a request escaped its `{{variable}}` tokens.** The collections export path and the
  app's **Copy as ▾** snippets (cURL, PowerShell, Python, C#) composed the URL through the same
  escaping query composer the wire path uses, so a saved parameter value of `{{tok}}` exported
  as `a=%7B%7Btok%7D%7D` — a token the active environment couldn't resolve arrived percent-escaped
  instead of literal, so a snippet copied for later use ran the wrong request the moment it was
  pasted and re-run. Exports now keep `{{…}}` sequences verbatim while escaping everything else
  exactly as before; a token the environment *can* resolve is still substituted first and its value
  escaped normally (resolve-then-escape, unchanged), and a value mixing a token with reserved
  characters, `{{tok}}&x=1`, exports as `{{tok}}%26x%3D1`, so the raw `&` can't split it into two
  parameters. This is the opposite-facing rule from the v1.54.0 fix in this changelog: there, an
  unresolved token had to resolve correctly before reaching the wire; here, a token that legitimately
  stays unresolved has to survive an export unescaped. The wire path — `EffectiveUrl`,
  `RequestUrl.Effective`, and the escaping `QueryString.Build`/`Compose` — is deliberately untouched,
  because a request actually being sent has no template to preserve, only a value to escape
  correctly. The emitted OpenAPI document was unaffected either way — it already percent-decodes the
  composed query before writing it — and that is now pinned by a test.
- **`certapi mock --http` was documented but not accepted.** `MockCommand.Help` has always documented
  `--http` ("Plain HTTP (default) — hit it with anything, no certificates"), but `MockCommand.Run`
  only consumed `--tls` and `--mtls`, so the leftover token reached the argument parser's
  unknown-option check and `certapi mock --http` failed with `Unknown option '--http'.` and exit code
  2 — following the tool's own documentation produced an error. `--http` is now consumed as an
  explicit selector for the default plain-HTTP mode, a no-op that resolves to the same mode as
  passing nothing, so a script can state the mode it wants instead of relying on the default by
  omission. The exclusivity check is now three-way: `--http`, `--tls`, and `--mtls` are mutually
  exclusive, and combining any two is a usage error (exit 2, `--http, --tls, and --mtls are mutually
  exclusive.`). Nothing else about the mock server changed.
- **Bench exit-code wording in the docs overstated its leniency.** `README.md` said `certapi bench`
  "exits 0 whatever the failure rate" and `docs/index.html` said it "exits 0 whatever the numbers
  say" — both omitted the one case it doesn't cover. The rule enforced by `BenchCommand.Run`,
  unchanged by this release, is: exit 0 whenever at least one request got a response, however bad —
  503s and 404s included — and exit 1 only when nothing answered at all. Both pages now state the
  exit-1 case explicitly. `wiki/21-CLI-Reference.md` and the in-app Help window (`HelpWindow.xaml.cs`)
  were checked against the same rule and were already correct, so neither was touched. The command's
  behavior did not change in this release — only the prose describing it did.

## [1.55.0] - 2026-07-26

### Added
- **gRPC calls, unary and server-streaming** — `certapi grpc` reaches a gRPC service (HTTP/2) that
  requires a client certificate, using the same Windows-store certificate handling as the rest of
  certapi, so this is the tool to reach for over `grpcurl` whenever the service sits behind mutual
  TLS. `certapi grpc list <address> --cert "CN=My Client"` discovers the services and methods a
  server advertises via server reflection (`grpc.reflection.v1alpha.ServerReflection`), printed
  indented under their service with `stream` marking a streaming request or response (`--json` for
  an array instead). `certapi grpc call <address> <Service/Method> -d '<json>'` invokes one —
  request bodies are supplied as JSON (`-d`/`--data`, default `{}`; or `--data-file <path>`),
  metadata as repeatable `-H "k: v"` headers, and the response prints back as indented JSON to
  stdout; a server-streaming method instead prints one compact JSON object per line as each message
  arrives, so it pipes straight into a line-oriented consumer, and `--max-messages <n>` stops it
  early (a clean exit 0, not a failure). A short service name resolves when it's unambiguous —
  `Echo/Unary` finds `certapi.test.Echo` — so you don't have to type the fully-qualified name every
  time. A host already pinned with `certapi trust add` is reached without `--insecure`, exactly as
  `certapi send` already allows, and a bearer token captured by an earlier `certapi send` to the
  same host is attached automatically as metadata (`--no-auto-token` opts out) — though `certapi
  grpc` never captures a *new* token itself. The proxy switches (`--proxy`/`--no-proxy`/
  `--proxy-user`) and `--insecure` apply to the channel; `--timeout <seconds>` defaults to 100.
  Exit codes follow the rest of certapi: 0 on success (including a stream stopped early by
  `--max-messages`), 1 when the gRPC status returned is not OK (`--json` carries `{status,
  statusName, detail}`), 2 on a bad command line (an address whose scheme isn't `http`/`https`, a
  malformed `Service/Method`, or a client-streaming/bidirectional method — out of scope, so asking
  for one is a usage error, not a data error), and 3 on a data problem (server reflection
  unavailable, or an unknown service/method/field, naming the offending one). A failure names its
  real cause the same way `certapi send` has since v1.50.0 — a refused or untrusted certificate
  reports `The remote certificate was rejected by the provided RemoteCertificateValidationCallback`,
  a closed port reports `No connection could be made because the target machine actively refused
  it` — rather than an unhelpful bare cancellation. **The honest limits.** Server reflection is
  required; a server that doesn't implement it can't be listed or called, and this version has no
  way to supply a compiled descriptor set instead. Client-streaming and bidirectional methods are
  out of scope for this version. `certapi serve` does not proxy gRPC — `HttpListener` is
  HTTP/1.1-only — so `certapi grpc` reaches the service directly with your certificate rather than
  going through the gateway. And the well-known Protocol Buffers (Protobuf) types
  (`google.protobuf.Timestamp`, `Duration`, `Struct`, `Any`, the wrapper types) render as ordinary
  messages rather than their special-cased JavaScript Object Notation (JSON) forms — a `Timestamp`
  shows as `{"seconds":"5","nanos":0}`, not an ISO 8601 string — and are supplied the same way on
  the way in. There is no window for this: it's a command-line concern, like `bench` and `serve`.

### Changed
- **The dependency posture, stated plainly.** This release takes `Grpc.Net.Client`, `Grpc.Reflection`,
  and `Google.Protobuf` — the one place the no-new-dependencies rule bends, because hand-rolling a
  Protocol Buffers (Protobuf) wire codec and a reflection client is a large, bug-prone surface for
  something two well-maintained packages already do correctly, and getting wire encoding subtly
  wrong in a *testing* tool would produce false results, which is the worst possible failure for
  this product. The containment is precise: the packages live in a new `src/ApiTester.Grpc` project
  referenced only by the command-line client; `ApiTester.Core` still has zero package references,
  and the desktop application is unchanged — a test (`GrpcContainmentTests`) now fails the build if
  either of those assemblies ever picks up a reference to `Grpc.*` or `Google.Protobuf`. The
  repository's "no external dependencies" claim is restated rather than quietly dropped: the
  application and `certapi` remain self-contained single-file executables with no *install*
  requirements — the packages are compiled in, exactly as the WebView2 loader already is, so there
  is still no installer, no admin rights, and no runtime to add. Measured, not guessed: the
  self-contained single-file `certapi.exe` grew from 36,732,361 to 37,242,477 bytes — **+510,116
  bytes (+0.49 MB)** — smaller than the spec's original ~5–8 MB estimate because
  `EnableCompressionInSingleFile` compresses the bundle; the added assemblies total about 1.0 MB
  (1,030,904 bytes) uncompressed. Building from source now restores these packages from NuGet in
  addition to the existing ones, which matters to anyone building offline or from an internal
  mirror.

## [1.54.0] - 2026-07-26

### Added
- **Response diffing** — the move from "did it respond" to "did it respond *the same*".
  `certapi send <url> --diff <baseline>` compares the response it just got against a baseline and
  prints what changed; the baseline is an HTTP Archive (HAR) file, a `.json` response file, or the
  literal `known-good` (the stored response of the saved request that matches this method and URL).
  A `.json` baseline may be either the envelope `certapi send --json` writes or a saved response
  snapshot, so recording one with `certapi send x --json > baseline.json` and comparing against it
  with `--diff baseline.json` works — a workflow the tool's own output could not feed would be a
  trap. `--diff-fail` turns any difference into exit 1, which is the continuous integration (CI)
  form; without it the diff is reported and the exit code is unaffected, because printing a
  difference and failing a build are different decisions. `--diff-ignore <path>` and `--diff-ignore-header <name>` are both
  repeatable, and a named header is *added to* the volatile defaults rather than replacing them —
  naming one noisy header says nothing about wanting `Date` and `Set-Cookie` compared from now on.
  The diff prints to stderr even under `--quiet` (the body still owns stdout), and `--json` gains a
  `diff` object. On `certapi run`, `--diff-har <file.har>` replays an archive and diffs every
  response against the one it recorded, which is what turns a captured session into a regression
  test: an entry passes only when its response is identical to the recorded one, status included,
  and any difference exits 1. There is deliberately no `--diff-fail` there, because a difference is
  always a failure in that mode. **The honest limits.** The body comparison picks its own mode: when
  both sides parse as JSON it is structural, naming each changed leaf path in the same `a.b[0].c`
  form capture rules use (added / removed / changed / type-changed, arrays by index); when either
  side is not JSON it falls back to a one-line text summary of lines and bytes on each side; and
  when either side is binary it reports size and equality only — it never pretends to diff a PDF.
  Seven headers that change on every response are ignored by default, or a real difference would
  drown in them: `Date`, `Set-Cookie`, `ETag`, `Age`, `X-Request-Id`, `X-Correlation-Id`, and
  `Server-Timing`. An ignore path may end a segment with `*`, so `data.token*` covers
  `data.tokenId`, and naming a container (`data`) suppresses everything beneath it. In the app, a
  **Diff** tab sits with the other response views and compares against the saved request's
  known-good response, or against an archive chosen with **Compare with HAR…** (**Clear** returns
  to the known-good baseline). Known-good baselines are recorded automatically on a successful
  (2xx) send by `certapi run` and by the app, capped at 1 MiB so the settings file does not become
  a blob store. A missing or unreadable baseline is a data error (exit 3), and `known-good` with no
  stored snapshot exits 3 naming the request that needs sending once.
- **Retry and backoff** — surviving a flaky internal endpoint in CI without wrapping every call in a
  shell loop. `--retry <n>` (0, the previous behavior, is still the default) retries a failed
  request, `--retry-on <codes>` picks the statuses that earn one (default `429,502,503,504`),
  `--retry-delay <ms>` sets the first wait (default 500), and `--no-retry-transport` switches off
  retrying a request that never reached the server at all. The delay doubles on each further
  attempt with ±10% jitter — jitter so a fleet of clients that all failed at the same moment does
  not come back in one synchronized wave — capped at 30 seconds, and a `Retry-After` header (delta
  seconds or an HTTP-date) overrides the computed wait, because a server that says when to come
  back knows better than any guess. Cancellation is honored during the wait, so Ctrl+C returns
  immediately instead of after the delay. The stderr metadata line gains `N attempts` when more
  than one was needed (and a failure reads `… (3 attempts)`: "it failed" and "it failed three
  times" are different facts), and the `--json` envelope gains `attempts` when it is above 1. The
  settings are additive on the transport options and mirrored on a saved request, so a request
  carries its own; on `run` a flag overrides only what it names. The app grows a **Retries** group
  on the request editor's Transport tab — count, statuses, first delay, "Also retry POST and PATCH",
  and "Retry connection failures and timeouts". **The honest limits.** Only idempotent methods
  retry by default — GET, HEAD, OPTIONS, PUT, DELETE — because re-sending a POST nobody confirmed
  can charge a card twice; `--retry-unsafe` opts POST and PATCH in explicitly. Among transport
  failures, only the ones a second attempt could plausibly survive earn a retry: connection
  refused, connection reset, name resolution, timeouts, and proxy failures. A refused or untrusted
  client or server certificate is **never** retried — it would only fail slower — and neither is a
  redirect loop. A negative `--retry`, or a `--retry-on` code that is not a number between 100 and
  599, is a usage error (exit 2) rather than a silently dropped typo that would leave you believing
  you configured a retry you never got.
- **Request chains** — "log in, then call the API" as a first-class, saved, runnable thing instead
  of an implicit convention about the order of a collection. A chain is a name, an ordered list of
  steps (each naming a saved request, each with its own stop-on-failure, on by default), and an
  optional environment, all persisted additively on the workspace. `certapi run --chain <name>`
  runs it, reporting PASS / FAIL per step and exiting 1 if any step failed. A chain reuses the
  ordinary run path exactly — resolve variables, send, evaluate assertions, apply capture rules,
  rebuild variables — so a token captured by step 1 is available to step 2 as a `{{variable}}`, and
  known-good recording applies per step just as in a normal run; running the same way is the whole
  point of a chain. When a failing step has stop-on-failure set, the chain halts and the remaining
  steps are reported **SKIP** rather than silently vanishing, since a step that never ran is not a
  step that passed. Captures write into the environment the chain names, created on first use, and
  an explicit `--env` wins over it — a flag you typed is a more specific instruction than a stored
  default. Every step is resolved before anything is sent: an unknown chain exits 3 listing the
  chains that do exist, and a step whose saved request has been deleted exits 3 naming the step.
  `--chain` cannot be combined with `--all`, a positional, `--diff-har`, or `--data` (exit 2) — a
  chain names its own requests in its own order, and whether a data row would repeat the whole
  chain or each step within it is not defined. **The honest limit.** A **CHAINS** section joins
  HISTORY and COLLECTIONS in the app sidebar, where a chain is created, its steps picked in order,
  reordered or removed, its per-step stop-on-failure and environment set, and **Copy run command**
  puts the matching `certapi run --chain "<name>"` on the clipboard — but *running* a chain is a
  command-line concern this release. The app builds and saves them; `certapi run --chain` runs
  them. Chains travel with an exported workspace and are merged or replaced on import like
  everything else.
- **Latency and load bench** — `certapi bench <url|saved request>` answers "how fast is this
  endpoint, and does it stay up under concurrency" over the same client-certificate send path as
  everything else, so what it measures is what the rest of the tool actually does. It defaults to
  `-n 100 -c 10` and reports requests sent / succeeded / failed, elapsed, requests per second, the
  min / p50 / p90 / p99 / max latencies, and the status and error counts; `--json` emits the same as
  a machine-readable envelope for a job that tracks latency over time. `--duration <seconds>` runs
  for a wall-clock period instead of a fixed count, and `--warmup <seconds>` sends and discards
  first so the figures describe a warmed-up endpoint — warm-up requests are extra, so `-n 20` still
  measures twenty. Percentiles come from the full retained latency array rather than an
  approximation, a bench never writes state (no known-good markers, no captured tokens, no
  attached session token), and it exits 0 whenever it measured anything, whatever the statuses came
  back: it reports numbers rather than passing judgement, so an endpoint that answers 503 or 404
  every time has still been measured. Exit 1 is reserved for no request getting a response at all,
  where there is nothing to report but that the endpoint could not be reached — a CI job benching an
  endpoint whose normal answer is 401 should not be handed a failure for it. `-n` together with
  `--duration`, a concurrency of zero or less, or a concurrency greater than the count are each usage
  errors (exit 2). **The honest limits.**
  Every request opens its own connection, because the client builds a fresh handler per send in
  order to capture that request's own handshake diagnostics, so the reported latencies include the
  TCP connect and the TLS handshake. Read them as "how long one request to this endpoint takes,
  from cold", not "how fast a warm client can stream requests at it" — the command prints that note
  under every summary, carries it in the `--json` envelope, and says it in its help text rather
  than quietly letting the numbers flatter the endpoint. Retries are forced off during a bench even
  when the request or a `--retry` flag asks for them, because a retry turns a failure into a slow
  success and hides the very failure rate the bench exists to measure; `--bench-retries` measures it
  anyway. And there is deliberately **no** app user interface for the bench: it is a command-line
  concern, and adding value in the window would need a chart.

### Fixed
- **`{{variables}}` in query-parameter values never resolved.** A saved request whose parameter grid
  held `api_key={{token}}` sent the literal placeholder, percent-escaped, as
  `api_key=%7B%7Btoken%7D%7D` — the parameter values were escaped while the URL was being composed,
  and only then was the text searched for `{{…}}`, by which point the pattern no longer existed.
  Variables in the base URL, path, headers, body, and auth were unaffected. `--strict-vars` could not
  catch it either, for the same reason: nothing that looked like a variable was left to report.
  Values are now resolved *before* the query is composed, so the substituted value is what gets
  escaped — a token containing `&`, `=`, a space, or non-ASCII text is now encoded correctly rather
  than corrupting the query. Unresolved query tokens are reported like every other kind, so
  `--strict-vars` fails on them and the ordinary path lists them as unresolved.
  This mattered most to the request chains added in this release, whose whole purpose is to capture a
  token in one step and use it in the next: feeding that token as a query parameter silently sent the
  placeholder, and the endpoint answered 401 with nothing to explain why.
- **`certapi run --json` reported a URL it had not sent.** The envelope recomputed each request's URL
  from the saved model instead of carrying the one that went on the wire, so a request using a
  variable in its query was reported as `?api_key=%7B%7Btoken%7D%7D` while it had actually sent the
  resolved value. A machine-readable envelope that misreports what was sent is worse than a cosmetic
  problem — a CI log would record the wrong URL — so the sent URL is now carried through, and a test
  pins the report and the wire to each other so they cannot drift apart again.

## [1.53.0] - 2026-07-26

### Added
- **HAR → OpenAPI** — `certapi export openapi --from-har session.har -o api.json` turns a captured
  HTTP Archive (HAR) session into an OpenAPI 3.0 document, the payoff for mapping an undocumented
  internal API: `fuzz` finds the paths, a HAR capture records what a real client actually sent, and
  this turns the recording into a specification. Repeated calls to the same endpoint collapse into
  one operation — headers common to every instance are kept (varying ones dropped), the first
  non-empty request body becomes the body example, and query keys seen across instances become query
  parameters. Path templating is deliberately conservative — a wrong `{param}` is worse than a
  literal path — so a segment becomes `{id}` only when it is all digits, a Universally Unique
  Identifier (UUID), or a hexadecimal string 16 characters or longer, *and* it actually varies between
  calls; one that happens to carry the same value in every entry stays literal. Responses of 400 and
  above are skipped, and a header whose value is exactly `[redacted]` (as `--har`'s default redaction
  writes it) is never exported — the existing OpenAPI-export rule still applies too: auth becomes a
  security scheme only, never the secret value. `--host <h>` keeps only one host's entries, and
  `--no-template-ids` keeps identifier-looking segments literal. In the app, Import ▾ → **Export
  OpenAPI from HAR file…** does the same from an `.har` file picker.
- **Mock from HAR** — `certapi mock --har session.har` serves a captured session back as a fake
  backend instead of the built-in echo routes, so an application, a test suite, or a teammate's client
  can develop against a recorded API offline. Matching precedence is exact method + path + query
  first, then method + path, then `--no-match-status` (default 404) for anything that matches
  neither; repeated calls to one route replay in recorded order and then repeat the last one.
  Hop-by-hop and framing headers (`Transfer-Encoding`, `Connection`, `Content-Length`) are never
  replayed, and a recorded `Set-Cookie` replays exactly as captured — a redacted capture replays
  `[redacted]`, which is correct and visible rather than silently wrong. The built-in `/`,
  `/status/<code>`, `/sse`, `/token`, `/windows-auth`, and `/cookie-auth` routes are not served while
  replaying. In the app, the Mock server window's **From HAR…** button starts the mock in replay mode
  the same way.
- **`serve --tls`** — closes the limitation the 1.51.0 gateway shipped with. `--tls` serves the
  gateway itself over HTTPS on `127.0.0.1`, using a generated `CN=127.0.0.1` certificate (a Subject
  Alternative Name (SAN) covering `127.0.0.1` and `localhost`, cached under
  `%AppData%\CertApiTester\gateway-tls\` and reused across runs) bound to the port through the
  Windows HTTP Server API — the same mechanism `netsh http add sslcert` configures. Binding is
  machine state, so nothing happens silently: the first bind needs an elevated (Run as administrator)
  prompt, and without one `serve --tls` exits 2 printing the exact `netsh` command to run; a port
  already bound to someone else's certificate is left alone and reported by its thumbprint rather
  than clobbered. `--tls-trust` additionally installs the certificate into `CurrentUser\Root` so the
  browser accepts it silently — explicit, logged, and reversible with `--tls-untrust`. Without
  `--tls-trust` the browser just warns once, and the startup banner says which situation applies.
  With `--tls` on, the `--browser` cookie rewriter stops stripping `Secure` from `Set-Cookie`, stops
  downgrading `SameSite=None` to `Lax`, and emits no `__Host-`/`__Secure-` warning — the known
  limitation recorded in the 1.51.0 section below, that those cookies could never survive a plaintext
  `http://127.0.0.1` origin, is now closed.

## [1.52.0] - 2026-07-26

### Added
- **HTTP Archive (HAR) capture** — `--har <file>` on `certapi send`, `run`, and `fuzz` records every
  request the command performs as a well-formed HAR archive, written once at the end of the run —
  including each redirect hop as its own entry, so a chain that changed origin is visible in the
  file, not just on screen. Secret redaction is on by default: the *values* of `Authorization`,
  `Proxy-Authorization`, `Cookie`, and `Set-Cookie` are written as `[redacted]`, so a trace is safe
  to hand to a teammate or attach to a bug report; `--har-include-secrets` opts out when you need the
  real values. In the app, Import ▾ → "Export Network trace as HAR…" writes the current tab's
  Network trace to a `.har` file with the same redact-by-default choice.
- **HAR replay, through mutual TLS** — `certapi import har <file>` turns a captured archive into a
  collection alongside `import curl`/`import openapi`, and `certapi run <file.har>` detects a `.har`
  positional directly and replays its entries as an ordered suite. The point of replay is what a
  browser's own HAR export can never do: the request is resent with a client certificate attached
  (`--cert`/`--cert-file`), so a session captured in a browser can be re-proven over mutual TLS
  without hand-rebuilding it. A HAR run never writes live state — no known-good markers, no
  captured tokens. A malformed HAR file is a one-line data error (exit 3); a well-formed HAR with
  zero entries is also exit 3, since there is nothing to replay. In the app, Import ▾ → "HAR
  file…" does the same import.
- **Per-site server-certificate trust** — a narrower alternative to the blanket "ignore server cert
  errors" toggle: `certapi trust add <host> [--thumbprint <t> | --from-url <https-url>]` pins one
  specific server-certificate thumbprint to one host, `certapi trust list` shows the pins, and
  `certapi trust remove <host> [--thumbprint <t>]` un-pins one or all of them. `--from-url` connects
  once, captures the certificate the server actually presents, and pins that thumbprint without you
  having to find it by hand. `send` and `run` consult the store automatically and note on stderr
  when a pinned certificate is what let the request through. In the app, a
  `ServerCertificateUntrusted` response offers a "Trust & retry" action, and a Trusted-certificates
  manager (Import ▾ → "Trusted certificates…") lists and removes pins. The existing blanket
  ignore-errors switch is unchanged and still available for cases pinning doesn't fit.

## [1.51.0] - 2026-07-25

### Added
- **Several upstreams behind one gateway port** — `--upstream /api=https://api.internal` mounts a host
  at a path prefix and repeats as often as you need, so a single `certapi serve` can stand in for a
  whole backend: `GET /api/orders` reaches `https://api.internal/orders`, with the prefix stripped
  before the request is forwarded. The longest matching prefix wins, a path under no prefix is a 404
  that contacts nothing, and two upstreams mounted on the same prefix is a usage error rather than a
  silent last-one-wins. The positional `certapi serve <upstream>` form still works — it is the same
  thing written `/=<url>`.
- **A gateway a browser can call — `certapi serve --browser`** — until now the gateway was usable only
  from an application whose base URL you control, because a faithful relay hands a browser exactly the
  headers that make it refuse the response. `--browser` turns on the whole bundle below at once, and
  every part of it also works on its own. None of it is on by default: without these flags the
  gateway is the same byte-faithful relay it has always been, adding and rewriting nothing.
  - `--cors` answers Cross-Origin Resource Sharing (CORS) preflights at the gateway instead of
    forwarding them upstream, and adds the response headers a browser insists on before it will let a
    script read the reply. With no value it echoes the caller's own `Origin`; given a comma-separated
    list it allows only those origins and refuses the rest with 403. Every `Access-Control-*` header
    the upstream sent is stripped before the gateway adds its own, because two allow-origin values
    are a hard browser failure rather than something it warns about.
  - `--rewrite-cookies` re-emits each `Set-Cookie` without `Domain=` and without `Secure`, and turns
    `SameSite=None` into `Lax`, so the browser will actually store the cookie against the gateway.
    `HttpOnly`, `Path`, `Max-Age`, and `Expires` pass through untouched.
  - `--rewrite-location` points a 3xx `Location` aimed at the upstream back at the gateway, so the
    browser stays on the local origin. One aimed anywhere else is relayed exactly as written and
    logged as a warning: that hop leaves the gateway, and your client certificate with it.
  - `--allow-upgrade` relays WebSocket connections through to the upstream with your client
    certificate attached, passing `Sec-WebSocket-Protocol` on so subprotocol negotiation still works.
- **Known limitation of `--rewrite-cookies`** — a cookie named `__Host-…` or `__Secure-…` requires the
  `Secure` attribute by specification, and no browser accepts `Secure` on a plaintext
  `http://127.0.0.1` origin, so such a cookie cannot be made to work through the gateway however it is
  rewritten. It is still passed on to the browser and named in a warning rather than dropped behind
  your back. Serving the gateway over HTTPS — a future `serve --tls` — is the real fix, and it does
  not exist yet.
- **Transport control on the gateway** — `certapi serve` now takes the transport flags the other
  commands gained in 1.50.0. `--proxy` / `--proxy-user` / `--no-proxy` and `--resolve` change how the
  gateway reaches an upstream; the redirect, decompression, and HTTP-version flags are accepted but do
  not apply, because the gateway always hands back the 3xx and the exact bytes the upstream sent.

## [1.50.0] - 2026-07-25

### Added
- **Proxy control** — send through a proxy you name instead of whatever the machine happens to be
  configured to use: `--proxy http://proxy.corp:8080` on `send`, `run`, and `fuzz` (with
  `--proxy-user user:pass` when it authenticates), or `--no-proxy` to ignore the configured proxy
  entirely, including an automatically detected or script-configured one. In the app, a new
  **Transport** tab on the request editor sets the same per request, and the Diagnostics view now
  names the proxy that was actually used.
- **Visible redirects** — 3xx hops are followed by the client itself and reported, so you can see
  where a request really ended up: `--show-redirects` prints the chain, `--json` includes it, and the
  app lists each hop as a row in the Network trace. `--no-redirect` stops at the 3xx,
  `--max-redirs <n>` changes the limit (default 20), and exceeding it is a clear "too many redirects"
  error carrying the hops collected so far. Each hop flags the two things that matter when a client
  certificate is in play — the `Authorization` header being dropped on a cross-origin hop, and a
  downgrade from `https` to `http` — because a redirect to another host means your certificate is
  about to be presented somewhere you didn't choose.
- **Pin a hostname to an address** — `--resolve <host>:<port>:<ip>` (repeatable) dials the address you
  give while still sending the original hostname in Server Name Indication (SNI) and the `Host`
  header, so you can test one node behind a load balancer, or verify a Domain Name System (DNS)
  cutover before it happens. `certapi send --all-ips` does the sweep for you: one send per address the
  host resolves to, with a per-address table of status, elapsed time, and body length. Pinning needs a
  direct connection, so `--resolve` together with an active proxy is a usage error rather than a
  silent no-op.
- **HTTP version pinning** — `--http1.1` or `--http2` forces the Hypertext Transfer Protocol (HTTP)
  version instead of negotiating it, which settles "is it the protocol or the endpoint?" quickly.
  Also available per request in the app's Transport tab.
- **The real reason a connection failed** — a transport error now carries the underlying exception
  chain and socket error code instead of collapsing to a single line. That chain is where the
  SChannel status for a refused client certificate actually lives, and it was previously discarded.
  `--debug` prints it, `--json` gains a structured `error` object (kind, message, detail), and the
  app's Diagnostics panel renders it. Exit codes are unchanged.

### Changed
- **Compressed responses are decoded automatically** — the client now sends `Accept-Encoding` and
  decompresses the response, matching every other HTTP client. This is a behavior change: if you
  relied on receiving the raw compressed bytes — to relay them byte-for-byte, say — add
  `--no-decompress` to get the previous behavior back.
- **A proxied connection still reports no handshake detail** — driving the handshake by hand would
  break proxy tunnelling, so the Transport Layer Security (TLS) version, the cipher, and whether your
  client certificate was presented are all blank whenever a proxy is in play. That was already true;
  what's new is the way out — `--no-proxy` bypasses the proxy *and* restores the full connection
  diagnostics.

## [1.49.0] - 2026-07-18

### Added
- **Ad-hoc assertions on `certapi send`** — `--assert "<expr>"` (repeatable) checks the response and
  exits 1 if it fails, so a single `send` is a CI smoke test without a saved workspace. Expressions:
  `status == 200`, `status < 300`, `time < 500`, `header <name> contains <v>`, `body <jsonpath> exists`,
  `body-text matches <regex>`; operators `== != contains matches exists !exists < >`. Reuses the same
  evaluator behind the GUI Tests tab and `certapi run`.

## [1.48.0] - 2026-07-18

### Changed
- **Professional starter wordlist** — the built-in endpoint list for `certapi fuzz` and the Discover
  window is now a curated ~270-entry list organized by category — operational/health, version & build,
  metrics (including Spring Actuator), API roots & versioning, API docs & specs, authentication &
  identity, users/accounts, admin, access control, common business resources, search, files/media,
  configuration, jobs/events, logs/audit, debug/dev, commonly-exposed sensitive paths
  (`.env`, `.git/config`, backups, …), and framework-specific paths — up from ~47. Key POST-only
  endpoints (login, token, register, graphql, …) are method-pinned. It's still a fast first pass; bring
  your own larger list with `-w <file>` for a thorough sweep.

## [1.47.2] - 2026-07-18

### Fixed
- **Maximize button glyph** — the title-bar maximize/restore button went blank after the window was
  first maximized or restored (its glyph was cleared on the window-state change). It now shows the
  correct maximize and restore icons.

## [1.47.1] - 2026-07-18

### Fixed
- **Session capture — address scheme** — a URL typed into the capture browser without a scheme is no
  longer forced to `https://`. Loopback/`localhost` hosts default to `http://`, so a local mock or dev
  server is reachable; other hosts still default to `https://`.
- **Session capture — visible errors** — a failed resource fetch in the capture browser now shows the
  error in the panel instead of leaving a blank page, and the window only auto-navigates to a genuine
  absolute URL.

### Changed
- **Mock route listing** — the in-app Help now lists the `/cookie-auth` route alongside the others
  (the README, Pages site, and handbook were already updated).

## [1.47.0] - 2026-07-18

### Added
- **Session capture** — a *Capture session…* button (next to *Mock server…*) opens a browser you can
  log in through; it presents your client certificate on every request, captures the resulting session
  **cookies** (including HttpOnly) and any **bearer token**, and scopes them per website. Captured
  sessions attach automatically to later requests — in the app and headless via `certapi send` /
  `certapi run`. Your password is typed into the site's own form and is never seen or stored. The
  finish step can also save the API calls observed during login as a ready-to-run collection.
- **Session chip controls** — the status-bar chip now shows captured cookies as well as tokens, with
  menu items to clear cookies for a site and to toggle *Automatically use captured cookies*.
- **Mock `/cookie-auth` route** — the local test server sets a session cookie and then reports the
  request as authenticated once the cookie comes back, so session capture can be exercised end to end.

## [1.46.1] - 2026-07-18

### Fixed
- **Theme toggle repaints the response** — switching themes now re-highlights whatever the Pretty view
  is showing. Previously the self-test detail (and anything else written outside a normal response)
  kept the old palette's colors — nearly unreadable pale-on-white after a switch to light.
- **Mock route list includes `/windows-auth`** — the route existed but was missing from the GUI mock
  console's routes line, the in-app Help, `certapi mock --help`, and the startup routes message.
- **"stopped" no longer shows in green** — the mock console's status line is muted while stopped and
  accent-green only while the server is live.

### Changed
- **Consistent window chrome** — every secondary window now uses the same title treatment (the accent
  `❯` glyph + bold title) and the same borderless caption close button. Previously the Mock server,
  Live stream, and OAuth windows had a small boxed close button, and the Environments, Discover, and
  pop-out windows were missing the brand glyph.

## [1.46.0] - 2026-07-18

### Added
- **Windows Integrated Authentication (Negotiate/NTLM)** — for internal sites that authenticate with
  your Windows identity. A new *Windows (integrated)* auth type uses your signed-in account for single
  sign-on by default, or explicit `DOMAIN\user` + password. Headless: `certapi send --windows-auth`
  (aliases `--ntlm` / `--negotiate`), or `--windows-user DOMAIN\user --windows-password …`. Saved
  requests carry it through `certapi run`. The handler negotiates Kerberos or NTLM automatically.
- **Mock `/windows-auth` route** — the local test server now emulates a Windows-auth-protected endpoint
  (challenges with `401 WWW-Authenticate: NTLM`, then accepts the handshake), so you can try your
  Windows-auth setup end to end against `certapi mock`.

## [1.45.0] - 2026-07-18

### Fixed
- **Assertion regex is bounded** — a `Matches` assertion runs a user-supplied pattern against the
  whole response body, so it now has a 2-second match timeout: a catastrophic-backtracking pattern
  fails the assertion instead of hanging `certapi run` or the GUI Tests tab.

### Changed
- **Releases include the full source** — each release now attaches `certapi-source-<tag>.zip`, a
  clean archive of the entire repository at that tag (all tracked files, no build output).

## [1.44.0] - 2026-07-18

### Added
- **Mock server in the app** — a *Mock server…* button (next to Run Self-Test) opens a console that
  starts and stops the local test server over plain HTTP, HTTPS, or mutual TLS, shows its base URL and
  routes, and logs each request as it arrives. For the TLS modes it generates the certificates and
  offers an *Open certs* shortcut; *Copy URL* drops the address straight into a request. Same server
  as `certapi mock` — now discoverable in the GUI.

## [1.43.0] - 2026-07-18

### Added
- **`certapi mock` — a standing local test server** to fire requests at (the persistent counterpart
  to `selftest`). It echoes each request back as JSON (method, path, query, headers, body, and, under
  mTLS, the client certificate you presented) and serves fixed routes: `/status/<code>`, `/sse`
  (a `text/event-stream`), `/token` (an OAuth 2.0 token), and a WebSocket echo on any path. Runs over
  plain HTTP (`--http`, default), HTTPS (`--tls`), or mutual TLS (`--mtls`, which requires and accepts
  any client certificate); `--tls`/`--mtls` write the generated server cert and a ready-to-use client
  `.pfx` to `--cert-dir`. So the app can be pointed at itself — `certapi send`, `sse`, `ws`, and
  `token` all work against it end to end.

## [1.42.0] - 2026-07-18

### Fixed
- **Stream console no longer leaks a socket per connection** — each WebSocket session is now disposed
  when it ends (and its cancellation source reused cleanly), so repeated connect/disconnect cycles
  don't accumulate `ClientWebSocket` handles.
- **`certapi token --save --workspace <file>`** now creates the workspace file when it doesn't exist
  yet, instead of warning and skipping the save.
- **`certapi ws --expect 0`** returns after sending instead of waiting for a reply that isn't coming.
- **OAuth token requests** report a clean "timed out" instead of surfacing a raw cancellation when the
  token endpoint is slow.

## [1.41.0] - 2026-07-18

### Added
- **OAuth 2.0 token acquisition** — fetch access tokens without leaving the app. A *Get OAuth 2.0
  token…* button on the Auth tab runs the client-credentials, password, and refresh-token grants
  directly, and the authorization-code grant interactively (opens your browser, catches the redirect
  on a loopback port, with PKCE). The token is stored for the API's host (so Auto auth attaches it)
  and dropped into the Bearer field. The token endpoint itself can require mTLS.
- **`certapi token`** — the same headless: `certapi token --token-url <url> --client-id … --client-secret …`
  for client-credentials (or `--grant password|refresh`), with `--save --for <api-url>` to store the
  token for later `certapi send` calls, `--client-auth basic`, `--scope`, `--param k=v`, and `--json`.

## [1.40.0] - 2026-07-18

### Fixed
- **Light theme now applies everywhere** — colours that were hardcoded for the dark palette and
  didn't follow a theme switch now do: the primary-button hover/pressed states, selected-row and
  secondary-button highlights, JSON/XML syntax highlighting (deeper, readable hues on white), the
  endpoint-discovery result colours, and the rendered-view error page. Toggling the theme also
  re-highlights the currently shown response.

### Changed
- **Stream console shows the live mode** — as you type the URL, the header indicates whether it will
  open a WebSocket (ws/wss) or stream Server-Sent Events (http/https).

## [1.39.0] - 2026-07-18

### Added
- **Live streaming (WebSocket & Server-Sent Events)** — a new *Stream* button on the request line
  opens a console that connects to a `ws://`/`wss://` endpoint (send messages, watch replies) or an
  `http(s)` `text/event-stream` endpoint (watch events arrive), using the selected client certificate
  and the insecure toggle. Two matching CLI commands: `certapi ws <url>` (send `--message`/stdin
  lines, print replies, `--expect <n>` for scripts) and `certapi sse <url>` (`--max-events`, `--json`
  ndjson output). Both honour `--cert`/`--cert-file`, `--insecure`, and custom `-H` headers.

## [1.38.0] - 2026-07-18

### Added
- **Light theme** — the Terminal Workbench palette now ships in light as well as dark. A sun/moon
  button in the title bar toggles between them; the choice is saved and restored on the next launch,
  and it applies live to the whole app (including the native window caption) without a restart.

## [1.37.0] - 2026-07-18

### Added
- **GraphQL** — `certapi send <url> --graphql "<query>" --gql-variables '{"id":1}'` posts a
  correctly-formed GraphQL request (JSON `{ query, variables }`, application/json, POST), with the
  query escaped and the variables validated.

## [1.36.0] - 2026-07-18

### Added
- **Find in response** — a search box above the response finds and selects the next match in the
  body (Enter for next, wrapping around), so you can locate a value in a large payload quickly.

## [1.35.0] - 2026-07-18

### Added
- **Session cookies** — the app now keeps a cookie jar for the session (like a browser), so a
  `Set-Cookie` in any response is sent back on later requests to that host; cookie-based logins
  carry across sends. Headless, add `--cookies` to `certapi run` to share a jar across a suite.

## [1.34.0] - 2026-07-18

### Added
- **Data-driven runs** — `certapi run <path> --data <file.csv|.json>` repeats the request(s) once
  per row of a dataset, each row's columns overriding `{{variables}}`. Results are labelled
  `[row N]`; combine with assertions to table-test an endpoint across many inputs in one command.

## [1.33.0] - 2026-07-18

### Added
- **Multipart body editor in the app** — the **Body** tab now has a **Form data (multipart)** mode:
  add fields, tick **File** to upload a file (with a file picker), and send. Multipart requests save
  into collections and run in suites like any other, so `certapi run` can exercise upload endpoints
  too. (The `certapi send -F` command line shipped in 1.31.0.)

## [1.32.0] - 2026-07-18

### Added
- **Copy as code** — the response toolbar's cURL button is now **Copy as ▾**: turn the current
  request into a snippet as cURL, PowerShell (`Invoke-RestMethod`), Python (`requests`), or C#
  (`HttpClient`), with `{{variables}}` resolved and headers and body included.

## [1.31.0] - 2026-07-18

### Added
- **File uploads (multipart/form-data)** — `certapi send -F "field=value" -F "file=@path"` posts a
  multipart form and uploads files (curl-style: `-F` is repeatable, implies POST, and
  `name=@path;type=<ct>` sets a part's content type; mutually exclusive with `-d`). Note: the CLI
  supports this today; a multipart editor in the app's Body tab is planned.

## [1.30.0] - 2026-07-18

### Added
- **Client certificate from a file** — use a certificate that isn't in the Windows store: load a
  `.pfx`/`.p12` (with an optional password) or a `.pem`/`.crt` (key in the same file or a separate
  one). In the app, **From file…** on the certificate row loads it for the session; headless, use
  `--cert-file` with `--cert-password` / `--key-file` on `send`, `fuzz`, `serve`, and `mcp`.

### Fixed
- The in-app Help was updated for the current UI: the request now has six tabs (Params, Headers,
  Body, Auth, Capture, Tests) and the command-line section documents `certapi fuzz`.

## [1.29.0] - 2026-07-18

### Added
- **Response assertions (tests)** — a **Tests** tab on any request lets you assert on the response:
  the **status**, **response time**, a **header**, a **JSON body path**, or the **body text**, with
  `==` / `!=` / contains / matches (regex) / exists / absent / `<` / `>`. `certapi run` now passes a
  request only when its assertions all pass — a request with no assertions still passes on any 2xx,
  so a collection becomes a real pass/fail test suite. Failed assertions are listed on stderr and in
  the `--json` output; the app shows a `✓ tests 3/3 passed` summary with the detail in Diagnostics.

## [1.28.1] - 2026-07-18

### Changed
- Clearer feedback while a request is in flight: the **Send** button now reads "Sending…", a slim
  indeterminate progress bar appears across the top of the response area, and the response views
  show "Waiting for response…" — so a previous response can't be mistaken for the new one.

## [1.28.0] - 2026-07-18

### Added
- **Built-in endpoint wordlist** — `certapi fuzz <base-url>` now works with no `-w`: it falls back
  to a built-in starter list of common endpoints (health, version, auth, admin, users, …), and the
  Discover window gains a **Use built-in list** button. Supply your own list with `-w` for a
  thorough sweep — the built-in list is only a quick look. The starter list also ships as
  `wordlists/common-api-endpoints.txt` and as a release asset.

### Fixed
- Endpoint discovery no longer drops a query string on a wordlist entry (e.g. `/search?q=1` used to
  be probed as `/search`).
- Saving discovered endpoints to a collection no longer reverts unsaved top-level collection changes
  made earlier in the same session.

## [1.27.0] - 2026-07-18

### Added
- **Endpoint discovery (fuzzing)** — point the tool at a website and a wordlist of candidate
  endpoints and it probes each one to show which exist. In the app, the new **Discover…** window
  streams colour-coded results (Found / Unauthorized / MethodNotAllowed / …), hides 404s and
  errors by default, opens any hit in a request tab on double-click, and can save all discoveries
  as a collection. Headless: `certapi fuzz <base-url> -w <wordlist>` with `--methods`,
  `--concurrency`, `--delay`, status `--match`/`--hide`/`--all`, `--json`, `-o`, `-w -` (stdin),
  and `--save-collection`. Captured auth tokens are attached automatically, so you can log in
  first and then discover authenticated endpoints. A starter wordlist ships in
  `wordlists/common-api-endpoints.txt`.

## [1.26.0] - 2026-07-17

### Added
- **Automatic bearer tokens** — a token returned by any response (`access_token`, `id_token`,
  `token`, `accessToken`, `jwt`, or an `X-Auth-Token`/`X-Access-Token` header) is captured with
  zero setup and scoped to the website it came from. Requests with the new **Auto** auth mode
  (the default) attach it automatically; explicit auth is never overridden, tokens never cross
  hosts, and expired tokens are never sent. Works in the app (with a status-bar token chip to
  inspect, clear, or disable), in `certapi send`/`run` (`--no-auto-token` to opt out), and in
  the MCP server (per-session store, so agent login flows chain naturally). Tokens persist in
  the workspace in plain text, like existing auth secrets.
- **Collection defaults** — a collection or folder can hold a default website and client
  certificate ("Set website & certificate…" on right-click, or auto-remembered from the first
  successful send). Endpoints opened from a collection fill their blanks from the nearest
  folder default or the active tab — no more re-picking the website and cert for every endpoint.
- **`--debug` and `--log-file <path>`** on every certapi command: resolved URLs, sent headers
  (Authorization masked), certificate lookup, TLS details, timings, and full stack traces on
  stderr and/or appended to a log file.
- **Examples in every help screen** — `certapi help <command>` now shows realistic, copy-paste
  command examples, including login-then-call token flows and CI patterns.

### Changed
- Requests saved with auth **None** by earlier versions are treated as **Auto** (that value
  used to mean "nothing configured"); the new explicit **None (never send auth)** is preserved.
  State files are stamped with a schema version so the migration runs exactly once.

## [1.25.0] - 2026-07-16

### Added
- **Capture & reuse auth tokens** — a request can now save a value from its response into an
  environment `{{variable}}`: a JSON body field (a dotted path like `data.access_token`) or a
  response header. Call an auth endpoint once and the token is stored (in the active environment,
  or a new **Captured** one); reuse it as `Authorization: Bearer {{token}}` on later requests with
  no copy-paste. Available in the app (a new **Capture** tab on each request) and headless
  (`certapi send --capture token=access_token`, and saved requests' rules are applied by
  `certapi run`).

### Changed
- The README now includes a task-oriented **Using it** guide covering sending requests,
  certificates, collections, environments, token capture, import/export, and the `certapi`
  command line, gateway, and MCP server.

## [1.24.0] - 2026-07-16

### Added
- **MCP server for AI agents** — `certapi mcp` speaks the Model Context Protocol (JSON-RPC over
  stdio) so an AI agent can use certapi as a tool: `send_request` (an mTLS call with the pinned
  certificate), `list_certificates`, `list_saved`, `run_saved`, and `self_test`. The operator pins
  one certificate with `--cert` and an allowed host set with `--allow` at launch — the agent never
  chooses a certificate, and every outbound URL is checked against the allowlist before it leaves
  the machine. Nothing is exposed on the network.

## [1.23.0] - 2026-07-16

### Added
- **Local mTLS gateway** — `certapi serve <upstream> --port <n> --cert <id>` runs a loopback
  reverse proxy: point any local application's base URL at `http://127.0.0.1:<port>` and every
  request is forwarded to the certificate-protected upstream with your Windows-store client
  certificate attached, then the response is relayed back unchanged. The application needs no
  mTLS code of its own — just a different base URL. Binds to loopback only; add `--token <value>`
  to require callers to present a shared secret. Stop with Ctrl+C.

## [1.22.1] - 2026-07-16

### Fixed
- `certapi send --timeout` now rejects non-numeric or non-positive values as a usage error
  instead of silently using the default.
- `certapi --var` rejects overrides with a blank key (e.g. `" =value"`).
- Ambiguous collection paths in `certapi run` now list the matching entries (marked folder or
  request) instead of only counting them, and a saved entry with no request reports a clear error.
- The `--json` envelope merges duplicate response headers that differ only in case.
- Help text: clarified that `--store LocalMachine` searches the machine store in addition to
  your user store.

### Internal
- Widened a short timeout in a network test that could flake under parallel test load;
  computing the state-file path no longer creates the settings directory as a side effect.

## [1.22.0] - 2026-07-16

### Added
- **Headless mode** — a new `certapi.exe` (separate download) drives the tester from the
  command line and scripts: `send` one-off requests with a client certificate from the
  Windows store, `run` saved requests and whole collections as pass/fail suites (recording
  their known-good markers — automatically for the live workspace, with `--record` for exported
  workspace files), `certs`, `selftest`, and `import`/`export` for cURL, OpenAPI, and workspaces.
  Response bodies go to stdout and diagnostics to stderr, with script-friendly exit codes
  (0 success · 1 failure · 2 usage · 3 data).

## [1.21.0] - 2026-07-16

### Added
- **Pop out the whole response panel** — the pop-out button now offers two choices: open just the
  selected view in a window (as before), or detach the **entire response panel — tabs and all —**
  into its own window. With the panel detached, the request editor gets the full main window and a
  slim bar offers “Bring it back”. The detached panel stays fully live (switch tabs, filter the
  Network trace, copy or save the body from there), and closing its window docks it back.

## [1.20.0] - 2026-07-16

### Added
- **Pop-out response views** — a pop-out button above the response opens the selected view
  (Pretty, Raw, Headers, Diagnostics, Rendered, or Network) in its own window, so you can keep —
  say — the live Network trace or a Rendered page visible beside the main window while you work.
  The popped-out view stays fully live, the tab shows a “Bring it back” shortcut meanwhile, and
  closing the window returns the view to its tab.

## [1.19.0] - 2026-07-16

### Added
- **Save / load workspaces** — “Export workspace…” in the Import ▾ menu writes everything to a
  single JSON file: open tabs, collections (including each saved request's known-good result),
  environments, saved websites, and history. “Import workspace…” loads a workspace file back and
  asks whether to **Merge** it into your current workspace or **Replace** it. Use it to move
  between machines, keep named snapshots of a project, or hand a teammate a ready-to-use setup.
  Workspace files include request auth values and history, so treat them as private.

## [1.18.0] - 2026-07-16

### Added
- **Export as OpenAPI** — a new button at the bottom of the collections sidebar writes the
  selected folder (or all collections when nothing is selected) as an **OpenAPI 3.0 JSON** file.
  Folders become tags, each saved request becomes an operation with its query parameters, headers,
  and body example, the most common website becomes the server, and a request's known-good note
  (when it was last checked and what it returned) becomes the operation description. The exported
  file re-imports cleanly — into this app or any OpenAPI-aware tool.
- Authentication is exported **as a security scheme only** — bearer tokens, usernames, and
  passwords are never written to the file, so exports are safe to share.
- Importing an OpenAPI file now also picks up each operation's `description`.

## [1.17.0] - 2026-07-16

### Added
- **Known-good endpoints** — every saved request in your collections now remembers its last
  result. Open a saved request and send it: a dot appears next to its name in the tree — **mint**
  when the send returned a 2xx (known good), **red** when it failed or returned an error status —
  and hovering shows when it was last checked and what it returned. Results persist between
  sessions, and are only recorded while the tab still targets the saved endpoint (same method and
  URL), so editing a request can't mislabel the entry it came from.

### Changed
- Tooltips throughout the app now use the dark theme instead of the light system default.

## [1.16.0] - 2026-07-16

### Changed
- **Network panel polish** — the Network tab now works like a proper browser network panel:
  - **Filter bar**: a text filter (matches URL, method, status, and content type), status-class
    filters (**All / 2xx / 3xx / 4xx / 5xx / ERR**), and a **cert only** toggle that shows just the
    calls made with your client certificate. The counter shows how many rows match and their
    combined size (e.g. “9 of 12 requests · 2.1 MB”).
  - **Details pane**: clicking a row opens a structured details pane — general facts (URL, status,
    type, size, time, start time, source, client certificate) and the request/response headers —
    with a **Copy** button and a drag handle to resize it. New rows scroll into view as they arrive.
  - **Right-click a row** to copy its URL or a matching `curl` command.

### Fixed
- Text typed into the Network filter box was invisible (clipped by the input's vertical padding).
- Opening a row's details no longer squeezes the request list to nothing at small window sizes.

## [1.15.0] - 2026-07-15

### Added
- **Network trace** — a **Network** response tab that logs every HTTP call, like a browser's
  network panel: the request you sent **and** every resource the Rendered view fetches (document,
  CSS, JS, images, XHR). Each row shows method, status, type, size, timing, and a marker when it
  was fetched with your client certificate; click a row for its request/response headers and cert
  detail. Clearable, and it keeps metadata only (no response bodies).

## [1.14.1] - 2026-07-15

### Fixed
- The new-tab **+** button now renders as a crisp, correctly-weighted plus (it was using a
  full-width character that some fonts don't have, so it looked off).

## [1.14.0] - 2026-07-15

### Added
- **In-app Help** — a **?** button in the title bar (and **F1**) opens a Help & Reference window
  covering every part of the app: getting started, requests & tabs, certificates & mTLS,
  collections & history, environments & variables, importing, the rendered website view, a full
  keyboard-shortcut reference, and an About panel (version, links, privacy, license). All content
  is built in, so it works with no web access.

## [1.13.1] - 2026-07-15

### Changed
- Documentation: added a screenshots gallery to the README and the documentation site, and
  clarified that the Rendered website view uses the Microsoft Edge WebView2 runtime included
  with Windows 11.

### Internal
- Added unit tests covering the request-model and collection mapping (cURL / OpenAPI import and
  the history round-trip). The application binary is unchanged from 1.13.0.

## [1.13.0] - 2026-07-15

### Added
- **Rendered website view** — a new **Rendered** tab in the response area opens the current
  request's URL as a web page. Every resource the page loads (the document, styles, scripts,
  images, and XHR) is fetched with your selected client certificate, so a certificate-protected
  internal site renders fully authenticated — not just the HTML. It renders on demand (nothing
  loads until you open the tab) and has its own address line and Reload button. Uses the Microsoft
  Edge WebView2 runtime that ships with Windows 11; if it isn't present, the tab explains that.

## [1.12.0] - 2026-07-15

### Added
- **Import from cURL** — paste a `curl` command (Import ▸ Paste cURL command) and it opens a new
  tab with the method, URL, query parameters, headers, body, and auth filled in. Understands
  `-X`, `-H`, `-d`/`--data`, `-u` (Basic), `-k` (insecure), an `Authorization: Bearer` header
  (mapped to the Bearer helper), quoting, and line continuations.
- **Import OpenAPI / Swagger** — import a JSON OpenAPI 3.x or Swagger 2.0 file
  (Import ▸ Import OpenAPI file) to generate a collection of requests, grouped into folders by
  tag, with the server/host used as each request's website.

## [1.11.3] - 2026-07-15

### Fixed
- The environment selector in the title bar is wider so “— no environment —” is no longer clipped.
- Gave the request area (Params / Headers / Body / Auth) more room by default so the Basic auth
  password field and additional parameter/header rows are no longer cut off; the Auth panel also
  scrolls if space is tight.

## [1.11.2] - 2026-07-15

### Fixed
- The Environments and name-prompt dialogs now render their dark themed frame all the way to the
  top edge, instead of showing a light OS-drawn strip above the window.

## [1.11.1] - 2026-07-15

### Fixed
- The Environments window's close button now shows the correct “✕” glyph instead of an empty box.
- Text in the name prompt (new environment, new folder, rename, save request) is no longer clipped.

## [1.11.0] - 2026-07-15

### Added
- **Collections** — save named requests into folders and reopen them in a tab. A
  HISTORY / COLLECTIONS switch in the sidebar, a tree with the current request's method badge,
  and buttons to save the current request, add a folder, rename, and delete. Double-click a
  saved request to open it in a new tab. Collections persist between sessions.
- **Environments & variables** — define `{{variable}}` values per environment (e.g. Dev,
  Staging, Prod) and switch between them from the **ENV** selector in the title bar. Variables
  are substituted in the URL, query parameters, headers, body, and auth **when you send**;
  stored requests keep the raw `{{tokens}}`. Any token with no value is reported in the status
  line so nothing is sent silently wrong. An Environments editor manages environments and their
  key/value variables.

## [1.10.0] - 2026-07-15

### Added
- **Request tabs** — keep several requests open at once, each with its own website, method,
  parameters, headers, body, auth, certificate, and response. New tab with the `＋` button or
  Ctrl+T; close with the tab's `✕`, middle-click, or Ctrl+W. Open tabs are remembered between
  sessions.
- **Query-parameter editor** — a new Params tab with an enable/key/value grid. Typing a `?query`
  in the URL box splits it into the grid; the grid is recombined into the URL when the request is
  sent. Values are percent-encoded correctly.

## [1.9.0] - 2026-07-14

### Changed
- New application icon — a clean, bold padlock — replacing the busier previous design so it stays
  crisp and professional at small (taskbar) sizes.
- Added spacing between the website field and the Forget button so they no longer crowd.

## [1.8.0] - 2026-07-14

### Added
- Faint placeholder / example text in the input fields (website, URL, certificate filter, header
  name/value, body, and auth) and guidance hints in the empty response tabs, to help first-time use.

## [1.7.0] - 2026-07-14

### Added
- An application icon — a terminal window with a lock badge, in the app's palette — used for the
  executable, taskbar, and Alt-Tab. Sizes below 32px use a simplified bold chevron-and-lock glyph
  so it stays crisp at 16px.

## [1.6.0] - 2026-07-14

### Added
- **Saved websites** — save a base URL and the URL box becomes just the path appended to it.

### Changed
- History entries now capture the *whole* request (website, certificate, ignore-cert toggle,
  timeout, headers, auth, body) **and the response** each one returned; clicking an entry fully
  replaces the current request and restores its stored response.
- History is labelled by path (with the host beneath) instead of the start of the URL.

## [1.5.0] - 2026-07-14

### Changed
- Documentation refresh: a comprehensive README and documentation site covering every feature,
  with an application screenshot, a quick-start walkthrough, endpoints to try, and a
  keyboard-shortcut reference.

## [1.4.0] - 2026-07-14

### Added
- **Request history** — a sidebar of recent requests (persisted); click one to reload it in full.
- **Connection diagnostics** — a Diagnostics tab and status-line summary showing the negotiated TLS
  version and cipher, whether the client certificate was actually presented, and the server
  certificate (subject, issuer, thumbprint, expiry, and chain).
- **Syntax highlighting** for JSON and XML in the Pretty response view.
- **Headers editor** as an enable/disable key-value grid.
- **Auth helpers** — Bearer-token and Basic auth that generate the `Authorization` header.
- **Request Content-Type selector** for the body.
- **Timeout field** and a **Cancel** button for in-flight requests.
- **Copy body** and **Copy as cURL** buttons; **Save** now suggests a file extension from the content type.
- **Certificate filter** box for quickly finding a certificate.
- **Keyboard shortcuts**: Ctrl+Enter / Enter to send, Ctrl+L focus URL, Ctrl+S save, Ctrl+H toggle
  history, F5 refresh certificates, Esc cancel.
- **Remembers** window size/position, the last certificate, the ignore-cert toggle, and the timeout.

## [1.3.0] - 2026-07-14

### Changed
- Replace the OS title bar with a custom in-app title bar that matches the theme, with its
  own minimize / maximize / close controls. The window still drags, snaps, resizes, and
  maximizes normally.

## [1.2.0] - 2026-07-14

### Added
- Follow the machine's configured proxy — including "Automatically detect settings" (WPAD)
  and a "Use automatic configuration script" (PAC) from Internet Options — authenticating to
  it with the signed-in user's Windows credentials when required.

### Changed
- Dark window title bar on Windows 11 so the OS caption matches the app theme.

## [1.1.0] - 2026-07-14

### Added
- A "— no certificate —" option (now the default) so the app works as a general API tester
  for endpoints that don't require mutual TLS, and can test the no-certificate path of ones
  that make a client certificate optional.

## [1.0.0] - 2026-07-14

Initial release.

### Added
- Pick a client certificate from the Windows certificate store (`CurrentUser\My`, optionally
  `LocalMachine\My`) with subject, issuer, thumbprint, and expiry; private keys are never exported.
- Mutual-TLS request engine over `SocketsHttpHandler` supporting GET/POST/PUT/PATCH/DELETE/HEAD/OPTIONS,
  custom headers, a request body, and a configurable timeout.
- Response viewer for unknown formats: pretty-prints JSON/XML, shows HTML/text, hex-dumps binary, and
  sniffs the body when the content type is missing or misleading. Pretty / Raw / Headers views.
- Distinct failure classification: certificate refused, server certificate untrusted, network, and timeout.
- Off-by-default "ignore server certificate errors" toggle for internal sites behind a private CA.
- Built-in *Run Self-Test* that stands up a local mutual-TLS server and proves the certificate path
  end to end with no real endpoint.
- Save any response (including binary) to a file.
- Self-contained single-file executable — no installer, no admin rights, no runtime dependency.

[Unreleased]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.63.0...HEAD
[1.63.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.62.0...v1.63.0
[1.62.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.61.1...v1.62.0
[1.61.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.61.0...v1.61.1
[1.61.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.60.0...v1.61.0
[1.60.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.59.0...v1.60.0
[1.59.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.58.0...v1.59.0
[1.58.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.57.0...v1.58.0
[1.57.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.56.0...v1.57.0
[1.56.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.55.1...v1.56.0
[1.55.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.55.0...v1.55.1
[1.55.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.54.0...v1.55.0
[1.54.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.53.0...v1.54.0
[1.53.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.52.0...v1.53.0
[1.52.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.51.0...v1.52.0
[1.51.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.50.0...v1.51.0
[1.50.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.49.0...v1.50.0
[1.49.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.48.0...v1.49.0
[1.48.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.47.2...v1.48.0
[1.47.2]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.47.1...v1.47.2
[1.47.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.47.0...v1.47.1
[1.47.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.46.1...v1.47.0
[1.46.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.46.0...v1.46.1
[1.46.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.45.0...v1.46.0
[1.45.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.44.0...v1.45.0
[1.44.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.43.0...v1.44.0
[1.43.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.42.0...v1.43.0
[1.42.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.41.0...v1.42.0
[1.41.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.40.0...v1.41.0
[1.40.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.39.0...v1.40.0
[1.39.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.38.0...v1.39.0
[1.38.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.37.0...v1.38.0
[1.37.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.36.0...v1.37.0
[1.36.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.35.0...v1.36.0
[1.35.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.34.0...v1.35.0
[1.34.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.33.0...v1.34.0
[1.33.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.32.0...v1.33.0
[1.32.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.31.0...v1.32.0
[1.31.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.30.0...v1.31.0
[1.30.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.29.0...v1.30.0
[1.29.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.28.1...v1.29.0
[1.28.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.28.0...v1.28.1
[1.28.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.27.0...v1.28.0
[1.27.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.26.0...v1.27.0
[1.26.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.25.0...v1.26.0
[1.25.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.24.0...v1.25.0
[1.24.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.23.0...v1.24.0
[1.23.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.22.1...v1.23.0
[1.22.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.22.0...v1.22.1
[1.22.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.21.0...v1.22.0
[1.21.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.20.0...v1.21.0
[1.20.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.19.0...v1.20.0
[1.19.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.18.0...v1.19.0
[1.18.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.17.0...v1.18.0
[1.17.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.16.0...v1.17.0
[1.16.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.15.0...v1.16.0
[1.15.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.14.1...v1.15.0
[1.14.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.14.0...v1.14.1
[1.14.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.13.1...v1.14.0
[1.13.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.13.0...v1.13.1
[1.13.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.12.0...v1.13.0
[1.12.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.11.3...v1.12.0
[1.11.3]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.11.2...v1.11.3
[1.11.2]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.11.1...v1.11.2
[1.11.1]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.11.0...v1.11.1
[1.11.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.10.0...v1.11.0
[1.10.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.9.0...v1.10.0
[1.9.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.8.0...v1.9.0
[1.8.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.7.0...v1.8.0
[1.7.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.6.0...v1.7.0
[1.6.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.5.0...v1.6.0
[1.5.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.4.0...v1.5.0
[1.4.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/Real-Fruit-Snacks/windows-cert-api-tester/releases/tag/v1.0.0
