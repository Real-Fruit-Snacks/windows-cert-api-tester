# Endpoint Discovery (Fuzzing) + Release Polish — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Add endpoint discovery (fuzz a wordlist of candidate endpoints against a base URL, report what exists) to the CLI and GUI, plus curated QOL upgrades, refreshed docs, and a 1.27.0 release.

**Architecture:** A network-agnostic `EndpointFuzzer` engine in Core (fed a `send` delegate) drives probes with bounded concurrency; the CLI `fuzz` command and a GUI `FuzzWindow` both build the delegate over `ApiClient` + cert + auto-token. Everything is additive.

**Tech Stack:** .NET 9, WPF (net9.0-windows), System.Text.Json, xunit. No new packages.

**Spec:** `docs/superpowers/specs/2026-07-17-endpoint-fuzzing-design.md`

## Global Constraints

- Windows-only; build `dotnet build ApiTester.sln`, test `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj`. Zero new warnings.
- Commit messages: single imperative sentence, sentence case, **no AI attribution / no Co-Authored-By trailers**.
- HTTP method tokens recognized as line prefixes (case-insensitive): `GET HEAD POST PUT PATCH DELETE OPTIONS`.
- Concurrency clamp: 1..50, default 8. Delay default 0 ms.
- `FuzzOutcome`: `Found`(2xx), `Redirect`(3xx), `Unauthorized`(401/403), `MethodNotAllowed`(405), `NotFound`(404), `ServerError`(5xx), `OtherStatus`, `Error`(transport). `IsDiscovery` = not `NotFound` and not `Error`.
- Auto-token behavior matches `send`/`run`: attach captured token per host unless `--no-auto-token`; capture tokens from probe responses. Reuse `TokenService` — do not reimplement.
- CLI `fuzz` exit codes: 0 on completion, **1 only if every probe was a transport error**, 2 usage, 3 data.
- Existing output contracts, note wording, and exit codes for other commands stay unchanged.
- Version target: 1.27.0.

---

### Task 1: EndpointList parsing (Core)

**Files:**
- Create: `src/ApiTester.Core/EndpointList.cs`
- Test: `tests/ApiTester.Tests/EndpointListTests.cs`

**Interfaces:**
- Produces: `sealed record EndpointEntry(string Path, string? Method)`; `static IReadOnlyList<EndpointEntry> EndpointList.Parse(string text)`; `static readonly IReadOnlyList<string> EndpointList.HttpMethods`.

- [ ] **Step 1: Write the failing tests**

Create `tests/ApiTester.Tests/EndpointListTests.cs`:

```csharp
using ApiTester.Core;

namespace ApiTester.Tests;

public class EndpointListTests
{
    [Fact]
    public void Parses_bare_paths()
    {
        var e = EndpointList.Parse("/api/users\n/api/orders\nhealth");
        Assert.Equal(3, e.Count);
        Assert.Equal("/api/users", e[0].Path);
        Assert.Null(e[0].Method);
        Assert.Equal("health", e[2].Path);
    }

    [Fact]
    public void Skips_blanks_and_comments()
    {
        var e = EndpointList.Parse("# a comment\n\n  \n/api/x\n   # indented comment\n/api/y");
        Assert.Equal(2, e.Count);
        Assert.Equal("/api/x", e[0].Path);
        Assert.Equal("/api/y", e[1].Path);
    }

    [Theory]
    [InlineData("POST /api/users", "POST", "/api/users")]
    [InlineData("get /health", "GET", "/health")]
    [InlineData("Delete  /api/thing", "DELETE", "/api/thing")]
    public void Parses_method_prefix(string line, string method, string path)
    {
        var e = EndpointList.Parse(line);
        Assert.Equal(method, e[0].Method);
        Assert.Equal(path, e[0].Path);
    }

    [Fact]
    public void A_leading_non_method_word_is_part_of_the_path()
    {
        // "api/users" has no method prefix; the whole token is the path.
        var e = EndpointList.Parse("api/users");
        Assert.Null(e[0].Method);
        Assert.Equal("api/users", e[0].Path);
    }

    [Fact]
    public void Keeps_full_url_paths_intact()
    {
        var e = EndpointList.Parse("https://other.example.com/api/x\nGET https://h/y");
        Assert.Equal("https://other.example.com/api/x", e[0].Path);
        Assert.Null(e[0].Method);
        Assert.Equal("GET", e[1].Method);
        Assert.Equal("https://h/y", e[1].Path);
    }

    [Fact]
    public void Dedupes_identical_method_path_pairs()
    {
        var e = EndpointList.Parse("/a\n/a\nPOST /a\nPOST /a\nGET /a");
        // /a (no method), POST /a, GET /a  → 3 distinct
        Assert.Equal(3, e.Count);
    }

    [Fact]
    public void Trims_trailing_whitespace_and_carriage_returns()
    {
        var e = EndpointList.Parse("/api/x  \r\n/api/y\r");
        Assert.Equal("/api/x", e[0].Path);
        Assert.Equal("/api/y", e[1].Path);
    }

    [Fact]
    public void Empty_or_comment_only_text_yields_no_entries()
    {
        Assert.Empty(EndpointList.Parse(""));
        Assert.Empty(EndpointList.Parse("# nothing\n\n#more"));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~EndpointListTests"`
Expected: build error — `EndpointList` missing.

- [ ] **Step 3: Implement**

Create `src/ApiTester.Core/EndpointList.cs`:

```csharp
namespace ApiTester.Core;

/// <summary>One candidate endpoint to probe: a path (bare, absolute, or full URL) and an
/// optional method that overrides the fuzz plan's method set for this entry.</summary>
public sealed record EndpointEntry(string Path, string? Method);

/// <summary>Parses a wordlist of candidate endpoints, one per line.</summary>
public static class EndpointList
{
    public static readonly IReadOnlyList<string> HttpMethods =
        new[] { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" };

    /// <summary>Parse wordlist text into entries. Blank lines and lines starting with '#' are
    /// skipped; a line may be "PATH" or "METHOD PATH" (a recognized HTTP verb prefix pins the
    /// method); identical (method, path) pairs are de-duplicated, input order preserved.</summary>
    public static IReadOnlyList<EndpointEntry> Parse(string text)
    {
        var result = new List<EndpointEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            string? method = null;
            string path = line;
            int sp = line.IndexOf(' ');
            if (sp > 0)
            {
                var head = line[..sp];
                if (HttpMethods.Contains(head, StringComparer.OrdinalIgnoreCase))
                {
                    method = head.ToUpperInvariant();
                    path = line[(sp + 1)..].TrimStart();
                }
            }
            if (path.Length == 0) continue;

            var key = (method ?? "") + " " + path;
            if (seen.Add(key)) result.Add(new EndpointEntry(path, method));
        }
        return result;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~EndpointListTests"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ApiTester.Core/EndpointList.cs tests/ApiTester.Tests/EndpointListTests.cs
git commit -m "Add endpoint wordlist parsing to the core library"
```

---

### Task 2: EndpointFuzzer engine (Core)

**Files:**
- Create: `src/ApiTester.Core/EndpointFuzzer.cs`
- Test: `tests/ApiTester.Tests/EndpointFuzzerTests.cs`

**Interfaces:**
- Consumes: Task 1's `EndpointEntry`; existing `RequestUrl.Effective`, `ApiRequest`, `ApiResponse`.
- Produces:
  - `enum FuzzOutcome { Found, Redirect, Unauthorized, MethodNotAllowed, NotFound, ServerError, OtherStatus, Error }`
  - `static FuzzOutcome FuzzClassifier.Classify(ApiResponse r)`; `static bool FuzzClassifier.IsDiscovery(FuzzOutcome o)`
  - `sealed record FuzzResult(string Method, string Path, string Url, int? StatusCode, string? ReasonPhrase, long SizeBytes, TimeSpan Elapsed, FuzzOutcome Outcome, string? Error)`
  - `sealed record FuzzProgress(int Completed, int Total, FuzzResult Last)`
  - `sealed class FuzzPlan { string BaseUrl; IReadOnlyList<EndpointEntry> Entries; IReadOnlyList<string> Methods = ["GET"]; IReadOnlyList<KeyValuePair<string,string>> Headers = []; int Concurrency = 8; int DelayMs = 0; }`
  - `sealed record FuzzReport(IReadOnlyList<FuzzResult> Results) { int Total; int Discovered; IReadOnlyDictionary<FuzzOutcome,int> CountsByOutcome; bool AllErrored; }`
  - `static Task<FuzzReport> EndpointFuzzer.RunAsync(FuzzPlan plan, Func<ApiRequest,CancellationToken,Task<ApiResponse>> send, IProgress<FuzzProgress>? progress, CancellationToken ct)`

- [ ] **Step 1: Write the failing tests**

Create `tests/ApiTester.Tests/EndpointFuzzerTests.cs`:

```csharp
using System.Net.Http;
using ApiTester.Core;

namespace ApiTester.Tests;

public class EndpointFuzzerTests
{
    private static FuzzPlan Plan(string list, IReadOnlyList<string>? methods = null, int conc = 8) => new()
    {
        BaseUrl = "https://api.example.com",
        Entries = EndpointList.Parse(list),
        Methods = methods ?? new[] { "GET" },
        Concurrency = conc
    };

    private static ApiResponse Resp(int status) => new()
    {
        StatusCode = status,
        ReasonPhrase = "x",
        Body = new byte[10],
        Elapsed = TimeSpan.FromMilliseconds(1)
    };

    [Fact]
    public void Classify_maps_status_ranges()
    {
        Assert.Equal(FuzzOutcome.Found, FuzzClassifier.Classify(Resp(200)));
        Assert.Equal(FuzzOutcome.Redirect, FuzzClassifier.Classify(Resp(302)));
        Assert.Equal(FuzzOutcome.Unauthorized, FuzzClassifier.Classify(Resp(401)));
        Assert.Equal(FuzzOutcome.Unauthorized, FuzzClassifier.Classify(Resp(403)));
        Assert.Equal(FuzzOutcome.MethodNotAllowed, FuzzClassifier.Classify(Resp(405)));
        Assert.Equal(FuzzOutcome.NotFound, FuzzClassifier.Classify(Resp(404)));
        Assert.Equal(FuzzOutcome.ServerError, FuzzClassifier.Classify(Resp(500)));
        Assert.Equal(FuzzOutcome.OtherStatus, FuzzClassifier.Classify(Resp(418)));
        Assert.Equal(FuzzOutcome.Error, FuzzClassifier.Classify(new ApiResponse { Error = new ApiError(ApiErrorKind.Network, "boom") }));
    }

    [Fact]
    public void IsDiscovery_excludes_notfound_and_error()
    {
        Assert.True(FuzzClassifier.IsDiscovery(FuzzOutcome.Found));
        Assert.True(FuzzClassifier.IsDiscovery(FuzzOutcome.MethodNotAllowed));
        Assert.True(FuzzClassifier.IsDiscovery(FuzzOutcome.Unauthorized));
        Assert.False(FuzzClassifier.IsDiscovery(FuzzOutcome.NotFound));
        Assert.False(FuzzClassifier.IsDiscovery(FuzzOutcome.Error));
    }

    [Fact]
    public async Task Runs_every_probe_and_classifies_results()
    {
        var plan = Plan("/found\n/missing\n/secure");
        var report = await EndpointFuzzer.RunAsync(plan, (req, ct) =>
        {
            int status = req.Url.EndsWith("/found") ? 200
                : req.Url.EndsWith("/secure") ? 401 : 404;
            return Task.FromResult(Resp(status));
        }, progress: null, CancellationToken.None);

        Assert.Equal(3, report.Total);
        Assert.Equal(2, report.Discovered);   // found + secure
        Assert.Contains(report.Results, r => r.Path == "/found" && r.Outcome == FuzzOutcome.Found);
        Assert.Contains(report.Results, r => r.Path == "/secure" && r.Outcome == FuzzOutcome.Unauthorized);
        Assert.False(report.AllErrored);
    }

    [Fact]
    public async Task Expands_methods_and_honors_per_entry_method()
    {
        var plan = Plan("/a\nPOST /b", methods: new[] { "GET", "HEAD" });
        var seen = new List<string>();
        var report = await EndpointFuzzer.RunAsync(plan, (req, ct) =>
        {
            lock (seen) seen.Add($"{req.Method.Method} {req.Url}");
            return Task.FromResult(Resp(200));
        }, null, CancellationToken.None);

        // /a → GET,HEAD ; /b → POST only (per-entry override)
        Assert.Equal(3, report.Total);
        Assert.Contains(seen, s => s == "GET https://api.example.com/a");
        Assert.Contains(seen, s => s == "HEAD https://api.example.com/a");
        Assert.Contains(seen, s => s == "POST https://api.example.com/b");
        Assert.DoesNotContain(seen, s => s.StartsWith("GET") && s.EndsWith("/b"));
    }

    [Fact]
    public async Task Full_url_entry_overrides_base()
    {
        var plan = Plan("https://other.example.com/x");
        string? sent = null;
        await EndpointFuzzer.RunAsync(plan, (req, ct) => { sent = req.Url; return Task.FromResult(Resp(200)); },
            null, CancellationToken.None);
        Assert.Equal("https://other.example.com/x", sent);
    }

    [Fact]
    public async Task Reports_progress_for_each_probe()
    {
        var plan = Plan("/a\n/b\n/c");
        var ticks = new List<FuzzProgress>();
        var progress = new Progress<FuzzProgress>(p => { lock (ticks) ticks.Add(p); });
        var report = await EndpointFuzzer.RunAsync(plan, (r, ct) => Task.FromResult(Resp(200)), progress, CancellationToken.None);
        // Progress<T> marshals async; wait until all three arrive.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (ticks.Count < 3 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(3, report.Total);
        Assert.All(ticks, t => Assert.Equal(3, t.Total));
    }

    [Fact]
    public async Task Concurrency_is_bounded()
    {
        int active = 0, peak = 0;
        var plan = Plan(string.Join("\n", Enumerable.Range(0, 30).Select(i => $"/p{i}")), conc: 4);
        await EndpointFuzzer.RunAsync(plan, async (req, ct) =>
        {
            int now = Interlocked.Increment(ref active);
            lock (plan) peak = Math.Max(peak, now);
            await Task.Delay(15, ct);
            Interlocked.Decrement(ref active);
            return Resp(200);
        }, null, CancellationToken.None);
        Assert.True(peak <= 4, $"peak concurrency {peak} exceeded 4");
    }

    [Fact]
    public async Task All_transport_errors_sets_AllErrored()
    {
        var plan = Plan("/a\n/b");
        var report = await EndpointFuzzer.RunAsync(plan,
            (r, ct) => Task.FromResult(new ApiResponse { Error = new ApiError(ApiErrorKind.Network, "down") }),
            null, CancellationToken.None);
        Assert.True(report.AllErrored);
        Assert.Equal(0, report.Discovered);
    }

    [Fact]
    public async Task Cancellation_stops_probing()
    {
        var plan = Plan(string.Join("\n", Enumerable.Range(0, 200).Select(i => $"/p{i}")), conc: 2);
        using var cts = new CancellationTokenSource();
        int calls = 0;
        var task = EndpointFuzzer.RunAsync(plan, async (req, ct) =>
        {
            Interlocked.Increment(ref calls);
            if (calls == 3) cts.Cancel();
            await Task.Delay(5, ct);
            return Resp(200);
        }, null, cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.True(calls < 200);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~EndpointFuzzerTests"`
Expected: build error — types missing.

- [ ] **Step 3: Implement**

Create `src/ApiTester.Core/EndpointFuzzer.cs`:

```csharp
using System.Net.Http;

namespace ApiTester.Core;

public enum FuzzOutcome { Found, Redirect, Unauthorized, MethodNotAllowed, NotFound, ServerError, OtherStatus, Error }

/// <summary>Classifies a probe response into a discovery outcome.</summary>
public static class FuzzClassifier
{
    public static FuzzOutcome Classify(ApiResponse r)
    {
        if (r.Error is not null) return FuzzOutcome.Error;
        return r.StatusCode switch
        {
            >= 200 and < 300 => FuzzOutcome.Found,
            >= 300 and < 400 => FuzzOutcome.Redirect,
            401 or 403 => FuzzOutcome.Unauthorized,
            405 => FuzzOutcome.MethodNotAllowed,
            404 => FuzzOutcome.NotFound,
            >= 500 => FuzzOutcome.ServerError,
            _ => FuzzOutcome.OtherStatus
        };
    }

    /// <summary>True when the endpoint probably exists (anything but a 404 or a transport error).</summary>
    public static bool IsDiscovery(FuzzOutcome o) => o is not (FuzzOutcome.NotFound or FuzzOutcome.Error);
}

public sealed record FuzzResult(
    string Method, string Path, string Url, int? StatusCode, string? ReasonPhrase,
    long SizeBytes, TimeSpan Elapsed, FuzzOutcome Outcome, string? Error);

public sealed record FuzzProgress(int Completed, int Total, FuzzResult Last);

public sealed class FuzzPlan
{
    public string BaseUrl { get; set; } = "";
    public IReadOnlyList<EndpointEntry> Entries { get; set; } = Array.Empty<EndpointEntry>();
    public IReadOnlyList<string> Methods { get; set; } = new[] { "GET" };
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; set; } = Array.Empty<KeyValuePair<string, string>>();
    public int Concurrency { get; set; } = 8;
    public int DelayMs { get; set; }
}

public sealed record FuzzReport(IReadOnlyList<FuzzResult> Results)
{
    public int Total => Results.Count;
    public int Discovered => Results.Count(r => FuzzClassifier.IsDiscovery(r.Outcome));
    public bool AllErrored => Results.Count > 0 && Results.All(r => r.Outcome == FuzzOutcome.Error);
    public IReadOnlyDictionary<FuzzOutcome, int> CountsByOutcome =>
        Results.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>Drives endpoint-discovery probes with bounded concurrency. Network-agnostic: the
/// caller supplies the <paramref name="send"/> delegate (which owns the client certificate,
/// timeout, and any auto-token handling).</summary>
public static class EndpointFuzzer
{
    public static async Task<FuzzReport> RunAsync(
        FuzzPlan plan,
        Func<ApiRequest, CancellationToken, Task<ApiResponse>> send,
        IProgress<FuzzProgress>? progress,
        CancellationToken ct)
    {
        // Expand entries × methods into an ordered probe list.
        var probes = new List<(string Method, string Path, string Url)>();
        foreach (var entry in plan.Entries)
        {
            var methods = entry.Method is not null ? new[] { entry.Method } : plan.Methods;
            foreach (var method in methods)
            {
                string url = RequestUrl.Effective(plan.BaseUrl, entry.Path, Array.Empty<KeyValuePair<string, string>>());
                probes.Add((method.ToUpperInvariant(), entry.Path, url));
            }
        }

        int concurrency = Math.Clamp(plan.Concurrency, 1, 50);
        var results = new FuzzResult?[probes.Count];
        int completed = 0;
        using var gate = new SemaphoreSlim(concurrency);

        var tasks = new List<Task>(probes.Count);
        for (int i = 0; i < probes.Count; i++)
        {
            int index = i;
            var (method, path, url) = probes[i];
            await gate.WaitAsync(ct);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (plan.DelayMs > 0) await Task.Delay(plan.DelayMs, ct);
                    var request = new ApiRequest
                    {
                        Method = new HttpMethod(method),
                        Url = url,
                        Headers = plan.Headers,
                        Timeout = TimeSpan.FromSeconds(100)
                    };
                    var response = await send(request, ct);
                    var outcome = FuzzClassifier.Classify(response);
                    var result = new FuzzResult(method, path, url, response.StatusCode, response.ReasonPhrase,
                        response.Body?.LongLength ?? 0, response.Elapsed, outcome, response.Error?.Message);
                    results[index] = result;
                    int done = Interlocked.Increment(ref completed);
                    progress?.Report(new FuzzProgress(done, probes.Count, result));
                }
                finally { gate.Release(); }
            }, ct));
        }

        await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();
        return new FuzzReport(results.Where(r => r is not null).Select(r => r!).ToList());
    }
}
```

Note: the `send` delegate sets the effective timeout; the `Timeout` on `ApiRequest` here is a
default the CLI/GUI closure overrides by constructing its own request — but to keep the plan's
timeout authoritative, the CLI/GUI `send` closure applies the real timeout on the `ApiClient`
call. (The engine passes the request through; the closure owns transport.)

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~EndpointFuzzerTests"`
Expected: all PASS. If `Cancellation_stops_probing` is flaky under load, that indicates the
`await gate.WaitAsync(ct)` in the dispatch loop isn't observing cancellation — verify the loop
throws when `ct` is cancelled (it will, via `WaitAsync(ct)`), which is the intended stop path.

- [ ] **Step 5: Commit**

```bash
git add src/ApiTester.Core/EndpointFuzzer.cs tests/ApiTester.Tests/EndpointFuzzerTests.cs
git commit -m "Add the endpoint-discovery fuzzing engine to the core library"
```

---

### Task 3: `certapi fuzz` command (CLI)

**Files:**
- Create: `src/ApiTester.Cli/Commands/FuzzCommand.cs`
- Modify: `src/ApiTester.Cli/CliApp.cs` (Usage overview + dispatch + help)
- Test: `tests/ApiTester.Tests/Cli/FuzzCommandTests.cs`

**Interfaces:**
- Consumes: Task 1-2 Core types; existing `CertPicker`, `CliWorkspace`, `TokenService`, `OutputText`, `CliServices` (incl. `.Client`, `.Log`, `.ListCertificates`, `.IsGuiRunning`, `.LiveStatePath`).
- Produces: `FuzzCommand.Run(Args, TextReader, TextWriter stdout, TextWriter stderr, CliServices)` and `FuzzCommand.Help`; a `fuzz` case in `CliApp` dispatch and `Help`.

- [ ] **Step 1: Write the failing tests**

Create `tests/ApiTester.Tests/Cli/FuzzCommandTests.cs`:

```csharp
using System.IO;
using System.Text;
using ApiTester.Cli;
using ApiTester.Core;

namespace ApiTester.Tests.Cli;

public class FuzzCommandTests
{
    // The loopback server returns 200 for every path; we distinguish "found" vs "not found"
    // by pointing the wordlist at the server and using bogus absolute-URL entries for misses.
    private static async Task<(int Code, string Out, string Err)> RunAsync(
        string[] args, string wordlist, string statePath, string? stdin = null)
    {
        using var ca = SelfSignedCertificateFactory.CreateCertificateAuthority("CA");
        using var serverCert = SelfSignedCertificateFactory.CreateSignedCertificate("localhost", ca, true, false, new[] { "localhost" });
        using var clientCert = SelfSignedCertificateFactory.CreateSignedCertificate("CliClient", ca, false, true);
        await using var server = await LoopbackMtlsServer.StartAsync(serverCert, clientCert.Thumbprint!, "{\"ok\":true}");

        var wlPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        File.WriteAllText(wlPath, wordlist);
        try
        {
            var services = new CliServices
            {
                LiveStatePath = statePath,
                IsGuiRunning = () => false,
                ListCertificates = _ => new[]
                {
                    new CertificateInfo
                    {
                        Subject = "CN=CliClient", Issuer = "CN=CA", Thumbprint = clientCert.Thumbprint!,
                        NotBefore = DateTime.Now.AddDays(-1), NotAfter = DateTime.Now.AddDays(30),
                        HasClientAuthEku = true, Certificate = clientCert
                    }
                }
            };
            var so = new StringWriter();
            var se = new StringWriter();
            var reader = new StringReader(stdin ?? "");
            var full = args.Select(a => a.Replace("{URL}", server.BaseUrl).Replace("{WL}", wlPath)).ToArray();
            int code = CliApp.Run(full, reader, so, se, new MemoryStream(), services);
            return (code, so.ToString(), se.ToString());
        }
        finally { File.Delete(wlPath); }
    }

    private static string TempState() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    [Fact]
    public async Task Discovers_reachable_paths_and_hides_404s_by_default()
    {
        var state = TempState();
        try
        {
            // "/" reaches the loopback (200). A bogus off-host absolute URL fails to connect (Error).
            var r = await RunAsync(new[] { "fuzz", "{URL}", "-w", "{WL}", "--cert", "CliClient", "--insecure" },
                "/\nhttps://127.0.0.1:1/nope", state);
            Assert.Equal(0, r.Code);
            Assert.Contains("200", r.Out);
            Assert.Contains("discovered", r.Err.ToLowerInvariant() + r.Out.ToLowerInvariant());
        }
        finally { if (File.Exists(state)) File.Delete(state); }
    }

    [Fact]
    public async Task Json_output_lists_results_and_summary()
    {
        var state = TempState();
        try
        {
            var r = await RunAsync(new[] { "fuzz", "{URL}", "-w", "{WL}", "--cert", "CliClient", "--insecure", "--json", "--all" },
                "/", state);
            Assert.Equal(0, r.Code);
            using var doc = System.Text.Json.JsonDocument.Parse(r.Out);
            Assert.True(doc.RootElement.GetProperty("results").GetArrayLength() >= 1);
            Assert.True(doc.RootElement.GetProperty("summary").GetProperty("total").GetInt32() >= 1);
        }
        finally { if (File.Exists(state)) File.Delete(state); }
    }

    [Fact]
    public async Task Reads_wordlist_from_stdin()
    {
        var state = TempState();
        try
        {
            var r = await RunAsync(new[] { "fuzz", "{URL}", "-w", "-", "--cert", "CliClient", "--insecure", "--all" },
                wordlist: "unused", statePath: state, stdin: "/\n/health");
            Assert.Equal(0, r.Code);
            Assert.Contains("/health", r.Out);
        }
        finally { if (File.Exists(state)) File.Delete(state); }
    }

    [Fact]
    public async Task Save_collection_writes_discovered_endpoints()
    {
        var state = TempState();
        try
        {
            var r = await RunAsync(new[] { "fuzz", "{URL}", "-w", "{WL}", "--cert", "CliClient", "--insecure",
                "--save-collection", "Discovered" }, "/\n/health", state);
            Assert.Equal(0, r.Code);
            var saved = AppState.LoadFrom(state);
            var folder = saved.Collections.FirstOrDefault(c => c.Name == "Discovered");
            Assert.NotNull(folder);
            Assert.True(CountLeaves(folder!) >= 1);
        }
        finally { if (File.Exists(state)) File.Delete(state); }

        static int CountLeaves(CollectionNode n) => n.IsFolder ? n.Children.Sum(CountLeaves) : 1;
    }

    [Fact]
    public async Task All_transport_errors_exits_1()
    {
        var state = TempState();
        try
        {
            // Base URL is the loopback but every entry overrides with an unreachable absolute URL.
            var r = await RunAsync(new[] { "fuzz", "{URL}", "-w", "{WL}", "--cert", "CliClient", "--insecure" },
                "https://127.0.0.1:1/a\nhttps://127.0.0.1:1/b", state);
            Assert.Equal(1, r.Code);
        }
        finally { if (File.Exists(state)) File.Delete(state); }
    }

    [Fact]
    public async Task Missing_wordlist_file_is_a_data_error()
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(new[] { "fuzz", "https://x.example", "-w", "C:\\no\\such\\file.txt" },
            new StringReader(""), so, se, new MemoryStream(), new CliServices { LiveStatePath = TempState() });
        Assert.Equal(3, code);
    }

    [Fact]
    public void Missing_wordlist_option_is_a_usage_error()
    {
        var so = new StringWriter();
        var se = new StringWriter();
        int code = CliApp.Run(new[] { "fuzz", "https://x.example" }, new StringReader(""), so, se, new MemoryStream(),
            new CliServices { LiveStatePath = TempState() });
        Assert.Equal(2, code);
    }

    [Fact]
    public void Help_has_examples()
    {
        Assert.Contains("Examples:", FuzzCommand.Help);
        Assert.Contains("certapi fuzz", FuzzCommand.Help);
        Assert.Contains("--debug", FuzzCommand.Help);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~FuzzCommandTests"`
Expected: build error — `FuzzCommand` missing.

- [ ] **Step 3: Implement `FuzzCommand`**

Create `src/ApiTester.Cli/Commands/FuzzCommand.cs`:

```csharp
using System.Net.Http;
using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class FuzzCommand
{
    public const string Help = """
        Usage: certapi fuzz <base-url> -w <wordlist> [options]

        Probes every endpoint in a wordlist against <base-url> and reports which ones exist —
        the fastest way to map an undocumented API. A line is "PATH" or "METHOD PATH"; blank
        lines and #comments are ignored; a full https:// line overrides the base URL.

        Wordlist:
          -w, --wordlist <file|->  Endpoints to probe ('-' reads them from stdin)
          -X, --methods <list>     Comma-separated methods to try per path (default GET)

        TLS / certificates:
          --cert <thumb|subject>   Client certificate from the Windows store
          --store <location>       CurrentUser (default); LocalMachine searches both stores
          --insecure               Ignore server certificate errors
          --timeout <seconds>      Per-probe timeout (default 100)

        Auth & variables:
          -H, --header "k: v"      Add a header to every probe (repeatable)
          --bearer <token>         Authorization: Bearer … on every probe
          --env <name> / --var k=v Resolve {{variables}} in the base URL and paths
          --no-auto-token          Don't attach or capture session tokens

        Discovery:
          --concurrency <n>        Parallel probes, 1–50 (default 8)
          --delay <ms>             Pause between probes (be polite; default 0)
          --hide <codes>           Hide these status codes (default 404)
          --match <codes>          Show only these status codes
          --all                    Show every probe, including 404s and errors

        Output:
          --json                   JSON { results, summary } instead of the table
          -o, --output <file>      Write discovered paths as a wordlist (or the JSON report)
          --save-collection <name> Save discovered endpoints as requests in a collection
          --workspace <file>       Use a workspace file instead of the live GUI state
          -q, --quiet              No progress counter on stderr

        Global: --debug (verbose diagnostics) and --log-file <path> work here too.

        Examples:
          # Probe a wordlist with a client certificate
          certapi fuzz https://api.example.com -w .\endpoints.txt --cert "CN=My Client"

          # Try several methods, show everything, go faster
          certapi fuzz https://api.example.com -w .\endpoints.txt -X GET,POST,PUT --all --concurrency 16

          # Log in first (token is captured), then discover authenticated endpoints
          certapi send https://api.example.com/login -X POST -d '{"user":"me"}'
          certapi fuzz https://api.example.com -w .\endpoints.txt

          # Pipe a wordlist in and save what you find as a collection
          type .\big-list.txt | certapi fuzz https://api.example.com -w - --save-collection Discovered

          # Machine-readable, only interesting results, into a file
          certapi fuzz https://api.example.com -w .\endpoints.txt --match 200,401,403 --json -o hits.json

        Exit 0 on completion, 1 if every probe failed to connect, 2 usage, 3 data error.
        """;

    public static int Run(Args args, TextReader input, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        string? wordlist = args.Value("-w", "--wordlist");
        string? methodsRaw = args.Value("-X", "--methods");
        var headers = args.Values("-H", "--header");
        string? bearer = args.Value("--bearer");
        string? certQuery = args.Value("--cert");
        string store = args.Value("--store") ?? "CurrentUser";
        bool insecure = args.Flag("--insecure");
        string? timeoutRaw = args.Value("--timeout");
        string? envName = args.Value("--env");
        var varOverrides = args.Values("--var");
        bool noAutoToken = args.Flag("--no-auto-token");
        string? concurrencyRaw = args.Value("--concurrency");
        string? delayRaw = args.Value("--delay");
        string? hideRaw = args.Value("--hide");
        string? matchRaw = args.Value("--match");
        bool all = args.Flag("--all");
        bool json = args.Flag("--json");
        string? outFile = args.Value("-o", "--output");
        string? saveCollection = args.Value("--save-collection");
        string? workspace = args.Value("--workspace");
        bool quiet = args.Flag("-q", "--quiet");

        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);
        string baseUrl = positionals[0];
        if (wordlist is null) throw new CliUsageException("fuzz needs -w <wordlist> (a file, or '-' for stdin).\n" + Help);

        int timeout = ParsePositive(timeoutRaw, 100, "--timeout");
        int concurrency = ParsePositive(concurrencyRaw, 8, "--concurrency");
        int delay = concurrencyRaw is null && delayRaw is null ? 0 : ParseNonNegative(delayRaw, 0, "--delay");
        var methods = (methodsRaw ?? "GET").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(m => m.ToUpperInvariant()).ToArray();
        if (methods.Length == 0) methods = new[] { "GET" };

        // ---- variables ----
        var state = LoadState(workspace, services);
        var vars = CliWorkspace.BuildVars(state, envName, varOverrides);
        string R(string s) => VariableResolver.Resolve(s ?? "", vars).Result;   // (Result, Unresolved) tuple
        baseUrl = R(baseUrl);

        // ---- wordlist ----
        string listText;
        if (wordlist == "-") listText = input.ReadToEnd();
        else if (!File.Exists(wordlist)) throw new CliDataException($"Wordlist not found: {wordlist}");
        else listText = File.ReadAllText(wordlist);
        var entries = EndpointList.Parse(listText);
        if (entries.Count == 0) throw new CliDataException("The wordlist has no endpoints (only blanks/comments).");

        // ---- headers / auth ----
        var headerPairs = new List<KeyValuePair<string, string>>();
        foreach (var raw in headers)
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0) throw new CliUsageException($"Header must be \"Name: value\", got '{raw}'.");
            headerPairs.Add(new(R(raw[..colon].Trim()), R(raw[(colon + 1)..].Trim())));
        }
        if (bearer is not null) headerPairs.Add(new("Authorization", "Bearer " + R(bearer)));

        // ---- certificate ----
        bool localMachine = store.Equals("LocalMachine", StringComparison.OrdinalIgnoreCase);
        if (!localMachine && !store.Equals("CurrentUser", StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException("--store must be CurrentUser or LocalMachine.");
        var cert = certQuery is null ? null
            : CertPicker.Resolve(services.ListCertificates(localMachine), certQuery, stderr).Certificate;

        var plan = new FuzzPlan
        {
            BaseUrl = baseUrl,
            Entries = entries,
            Methods = methods,
            Headers = headerPairs,
            Concurrency = concurrency,
            DelayMs = delay
        };

        // The send delegate owns transport: per-request auto-token attach + capture, the cert,
        // insecure, and the timeout.
        var captureLock = new object();
        async Task<ApiResponse> Send(ApiRequest request, CancellationToken ct)
        {
            var reqHeaders = request.Headers.ToList();
            if (!noAutoToken) { lock (captureLock) TokenService.AutoAttach(state, request.Url, reqHeaders, out _); }
            var probe = request with { Headers = reqHeaders, Timeout = TimeSpan.FromSeconds(timeout) };
            var response = await services.Client.SendAsync(probe, cert, insecure, followRedirects: false, cancellationToken: ct);
            if (!noAutoToken && response.Error is null)
                lock (captureLock) TokenService.Capture(state, request.Url, response.Body, response.ContentType, response.Headers);
            return response;
        }

        int lastReported = 0;
        var progress = quiet ? null : new Progress<FuzzProgress>(p =>
        {
            if (p.Completed - lastReported >= 10 || p.Completed == p.Total)
            { lastReported = p.Completed; stderr.Write($"\r  probing {p.Completed}/{p.Total}…"); stderr.Flush(); }
        });

        services.Log.Debug($"fuzz {baseUrl} · {entries.Count} entries × {methods.Length} method(s) · concurrency {concurrency}");
        FuzzReport report;
        try { report = EndpointFuzzer.RunAsync(plan, Send, progress, services.Cancel).GetAwaiter().GetResult(); }
        catch (OperationCanceledException)
        {
            stderr.WriteLine("\ncancelled.");
            return ExitCodes.Ok;
        }
        if (!quiet) stderr.WriteLine();

        // ---- persist captured tokens / discovered collection ----
        bool dirty = false;
        if (saveCollection is not null)
        {
            SaveDiscovered(state, report, baseUrl, cert, saveCollection);
            dirty = true;
        }
        if (dirty)
        {
            if (workspace is null && services.IsGuiRunning())
                stderr.WriteLine("note: the GUI is running — the discovered collection was not saved (it would overwrite it on close).");
            else
                try { state.SaveTo(workspace ?? services.LiveStatePath); }
                catch (Exception ex) { stderr.WriteLine($"warning: could not save: {ex.Message}"); }
        }

        // ---- output ----
        var shown = Filter(report.Results, all, matchRaw, hideRaw);
        if (json)
        {
            string js = BuildJson(report, shown);
            if (outFile is not null) { File.WriteAllText(outFile, js); stderr.WriteLine($"wrote {outFile}"); }
            else stdout.WriteLine(js);
        }
        else
        {
            foreach (var r in shown.OrderByDescending(r => FuzzClassifier.IsDiscovery(r.Outcome)).ThenBy(r => r.StatusCode ?? 999))
                stdout.WriteLine(
                    $"{Label(r.Outcome),-8} {(r.StatusCode?.ToString() ?? "ERR"),4}  {r.Method,-6} {OutputText.Size(r.SizeBytes),9}  {r.Path}{(r.Error is not null ? $"  ({r.Error})" : "")}");
            stdout.WriteLine($"----\n{report.Total} probed · {report.Discovered} discovered · " +
                string.Join(" · ", report.CountsByOutcome.OrderBy(k => k.Key).Select(k => $"{Label(k.Key)} {k.Value}")));
            if (outFile is not null)
            {
                var paths = report.Results.Where(r => FuzzClassifier.IsDiscovery(r.Outcome)).Select(r => r.Path).Distinct();
                File.WriteAllLines(outFile, paths);
                stderr.WriteLine($"wrote discovered paths to {outFile}");
            }
        }

        return report.AllErrored ? ExitCodes.Failure : ExitCodes.Ok;
    }

    private static AppState LoadState(string? workspace, CliServices services) =>
        workspace is not null && !File.Exists(workspace) ? new AppState()
        : workspace is null && !File.Exists(services.LiveStatePath) ? new AppState()
        : CliWorkspace.Load(workspace, services.LiveStatePath);

    private static void SaveDiscovered(AppState state, FuzzReport report, string baseUrl,
        System.Security.Cryptography.X509Certificates.X509Certificate2? cert, string name)
    {
        var folder = state.Collections.FirstOrDefault(c => c.IsFolder && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (folder is null) { folder = new CollectionNode { Name = name, IsFolder = true }; state.Collections.Add(folder); }
        foreach (var r in report.Results.Where(x => FuzzClassifier.IsDiscovery(x.Outcome)))
        {
            var model = new RequestModel { Method = r.Method, BaseUrl = baseUrl, Path = r.Path, CertThumbprint = cert?.Thumbprint };
            folder.Children.Add(new CollectionNode { Name = $"{r.Method} {r.Path}", IsFolder = false, Request = model });
        }
    }

    private static IReadOnlyList<FuzzResult> Filter(IReadOnlyList<FuzzResult> results, bool all, string? match, string? hide)
    {
        if (all) return results;
        if (match is not null)
        {
            var codes = ParseCodes(match);
            return results.Where(r => r.StatusCode is { } s && codes.Contains(s)).ToList();
        }
        var hidden = hide is not null ? ParseCodes(hide) : new HashSet<int> { 404 };
        return results.Where(r => r.StatusCode is not { } s || !hidden.Contains(s)).ToList();
    }

    private static HashSet<int> ParseCodes(string csv) =>
        csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
           .Select(x => int.TryParse(x, out var n) ? n : -1).Where(n => n > 0).ToHashSet();

    private static string BuildJson(FuzzReport report, IReadOnlyList<FuzzResult> shown)
    {
        var obj = new
        {
            results = shown.Select(r => new
            {
                method = r.Method, path = r.Path, url = r.Url, status = r.StatusCode,
                outcome = r.Outcome.ToString(), discovered = FuzzClassifier.IsDiscovery(r.Outcome),
                sizeBytes = r.SizeBytes, elapsedMs = Math.Round(r.Elapsed.TotalMilliseconds), error = r.Error
            }),
            summary = new
            {
                total = report.Total, discovered = report.Discovered,
                byOutcome = report.CountsByOutcome.ToDictionary(k => k.Key.ToString(), v => v.Value)
            }
        };
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Label(FuzzOutcome o) => o switch
    {
        FuzzOutcome.Found => "found",
        FuzzOutcome.Redirect => "redirect",
        FuzzOutcome.Unauthorized => "auth",
        FuzzOutcome.MethodNotAllowed => "method",
        FuzzOutcome.NotFound => "404",
        FuzzOutcome.ServerError => "5xx",
        FuzzOutcome.OtherStatus => "other",
        _ => "error"
    };

    private static int ParsePositive(string? raw, int fallback, string opt)
    {
        if (raw is null) return fallback;
        if (!int.TryParse(raw, out var n) || n <= 0) throw new CliUsageException($"{opt} expects a positive number, got '{raw}'.");
        return n;
    }

    private static int ParseNonNegative(string? raw, int fallback, string opt)
    {
        if (raw is null) return fallback;
        if (!int.TryParse(raw, out var n) || n < 0) throw new CliUsageException($"{opt} expects a non-negative number, got '{raw}'.");
        return n;
    }
}
```

Note on `VariableResolver.Resolve` return shape: it returns `(string Result, IReadOnlyList<string> Unresolved)` in this codebase (verified). The `.Result` access above matches.

- [ ] **Step 4: Wire into `CliApp`**

In `src/ApiTester.Cli/CliApp.cs`:

Add to `Usage` Commands list (after the `run` line):

```
          fuzz <base-url>   Discover endpoints from a wordlist (which ones exist?)
```

Add a dispatch case. `fuzz` needs stdin (for `-w -`), so route it through the input-aware path like `mcp`. In the **outer** `Run` overload (the one with `TextReader input`), extend the mcp special-case block to also handle fuzz, OR add fuzz to the inner switch passing `TextReader.Null`. Cleanest: handle `fuzz` in the outer overload alongside `mcp` so it gets real stdin:

```csharp
        if (args.Length > 0 &&
            (args[0].Equals("mcp", StringComparison.OrdinalIgnoreCase) || args[0].Equals("fuzz", StringComparison.OrdinalIgnoreCase)))
        {
            bool isMcp = args[0].Equals("mcp", StringComparison.OrdinalIgnoreCase);
            (string[] Remaining, bool Debug, string? LogFile) g;
            try { g = GlobalOptions.Extract(args.Skip(1).ToArray()); }
            catch (CliUsageException ex) { stderr.WriteLine(ex.Message); return ExitCodes.Usage; }

            using var log = CliLog.Create(g.Debug, g.LogFile, stderr);
            services.Log = log;
            var err = log.WrapStderr(stderr);
            try
            {
                return isMcp
                    ? Commands.McpCommand.Run(new Args(g.Remaining), input, stdout, err, services)
                    : Commands.FuzzCommand.Run(new Args(g.Remaining), input, stdout, err, services);
            }
            catch (CliUsageException ex) { err.WriteLine(ex.Message); return ExitCodes.Usage; }
            catch (CliDataException ex) { err.WriteLine(ex.Message); return ExitCodes.Data; }
            catch (Exception ex) { err.WriteLine("error: " + log.Describe(ex)); return ExitCodes.Failure; }
        }
```

Add `fuzz` to the `Help` per-command switch:

```csharp
            "fuzz" => Commands.FuzzCommand.Help,
```

The inner `Run` overload's dispatch switch does not need a `fuzz` case (the outer overload catches it first). But `CliApp.Run(string[], TextWriter, TextWriter, …)` — the overload without a reader — is used by some tests; ensure `fuzz` there still works by adding a case that passes `TextReader.Null`:

In the inner switch add:

```csharp
                "fuzz" => Commands.FuzzCommand.Run(new Args(rest), TextReader.Null, stdout, err, services),
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj --filter "FullyQualifiedName~FuzzCommandTests"`
Expected: all PASS. Then the full suite:
Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj`
Expected: PASS, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/ApiTester.Cli/Commands/FuzzCommand.cs src/ApiTester.Cli/CliApp.cs tests/ApiTester.Tests/Cli/FuzzCommandTests.cs
git commit -m "Add the certapi fuzz endpoint-discovery command"
```

---

### Task 4: GUI Discover window

**Files:**
- Create: `src/ApiTester.App/FuzzWindow.xaml`
- Create: `src/ApiTester.App/FuzzWindow.xaml.cs`
- Modify: `src/ApiTester.App/MainWindow.xaml` (toolbar "Discover…" button near Import; Ctrl+Enter InputBinding)
- Modify: `src/ApiTester.App/MainWindow.xaml.cs` (open handler + open-discovered-in-tab)

No new unit tests (WPF wiring; the engine is tested in Core). Verification = build + smoke.

**Interfaces:**
- Consumes: Core `EndpointFuzzer`/`FuzzPlan`/`EndpointList`/`FuzzResult`/`TokenService`, `ApiClient`, `_state`, cert options.
- Produces: nothing downstream.

- [ ] **Step 1: Create `FuzzWindow.xaml`**

Create `src/ApiTester.App/FuzzWindow.xaml` (match the app's dark chrome, following `CollectionDefaultsDialog.xaml`/`InputDialog.xaml`; verify resource keys `Bg.Window`/`Bg.Panel`/`Bg.Input`/`Border`/`Text.Soft`/`Text.Muted`/`Accent` exist, else mirror InputDialog):

```xml
<Window x:Class="ApiTester.App.FuzzWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Discover endpoints" Width="900" Height="640"
        WindowStartupLocation="CenterOwner" Background="{StaticResource Bg.Window}">
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <StackPanel Grid.Row="0">
            <TextBlock Text="DISCOVER ENDPOINTS" FontWeight="Bold" Foreground="{StaticResource Accent}" Margin="0,0,0,8"/>
            <DockPanel Margin="0,0,0,6">
                <TextBlock Text="WEBSITE" Width="80" Foreground="{StaticResource Text.Muted}" VerticalAlignment="Center"/>
                <TextBox x:Name="BaseUrlBox" VerticalContentAlignment="Center"/>
            </DockPanel>
            <DockPanel Margin="0,0,0,6">
                <TextBlock Text="CERT" Width="80" Foreground="{StaticResource Text.Muted}" VerticalAlignment="Center"/>
                <ComboBox x:Name="CertCombo"/>
            </DockPanel>
            <DockPanel Margin="0,0,0,6">
                <TextBlock Text="WORDLIST" Width="80" Foreground="{StaticResource Text.Muted}" VerticalAlignment="Center"/>
                <Button x:Name="BrowseButton" Content="Choose file…" Width="110" DockPanel.Dock="Right" Click="Browse_Click"/>
                <TextBox x:Name="WordlistPathBox" VerticalContentAlignment="Center" Margin="0,0,6,0"/>
            </DockPanel>
            <TextBlock Text="…or paste endpoints (one per line, # comments ok):" Foreground="{StaticResource Text.Muted}" Margin="80,2,0,2"/>
            <TextBox x:Name="PasteBox" Height="70" Margin="80,0,0,6" AcceptsReturn="True"
                     VerticalScrollBarVisibility="Auto" FontFamily="Consolas"/>
        </StackPanel>

        <WrapPanel Grid.Row="1" Margin="80,0,0,8">
            <TextBlock Text="Methods:" Foreground="{StaticResource Text.Muted}" VerticalAlignment="Center" Margin="0,0,8,0"/>
            <CheckBox x:Name="Mget" Content="GET" IsChecked="True" Margin="0,0,8,0"/>
            <CheckBox x:Name="Mhead" Content="HEAD" Margin="0,0,8,0"/>
            <CheckBox x:Name="Mpost" Content="POST" Margin="0,0,8,0"/>
            <CheckBox x:Name="Mput" Content="PUT" Margin="0,0,8,0"/>
            <CheckBox x:Name="Mdelete" Content="DELETE" Margin="0,0,16,0"/>
            <TextBlock Text="Concurrency:" Foreground="{StaticResource Text.Muted}" VerticalAlignment="Center" Margin="0,0,4,0"/>
            <TextBox x:Name="ConcurrencyBox" Text="8" Width="44" Margin="0,0,12,0"/>
            <CheckBox x:Name="HideNoise" Content="Hide 404s / errors" IsChecked="True" VerticalAlignment="Center"
                      Checked="HideNoise_Toggle" Unchecked="HideNoise_Toggle"/>
        </WrapPanel>

        <DataGrid Grid.Row="2" x:Name="ResultsGrid" AutoGenerateColumns="False" IsReadOnly="True"
                  HeadersVisibility="Column" GridLinesVisibility="Horizontal"
                  Background="{StaticResource Bg.Panel}" MouseDoubleClick="Results_DoubleClick">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Outcome" Binding="{Binding OutcomeLabel}" Width="90"/>
                <DataGridTextColumn Header="Status" Binding="{Binding StatusText}" Width="70"/>
                <DataGridTextColumn Header="Method" Binding="{Binding Method}" Width="70"/>
                <DataGridTextColumn Header="Size" Binding="{Binding SizeText}" Width="80"/>
                <DataGridTextColumn Header="ms" Binding="{Binding Ms}" Width="60"/>
                <DataGridTextColumn Header="Path" Binding="{Binding Path}" Width="*"/>
            </DataGrid.Columns>
        </DataGrid>

        <DockPanel Grid.Row="3" Margin="0,10,0,0">
            <TextBlock x:Name="StatusText" DockPanel.Dock="Left" VerticalAlignment="Center"
                       Foreground="{StaticResource Text.Soft}" Text="Ready."/>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                <Button x:Name="SaveCollectionButton" Content="Save discovered to collection…" Width="200"
                        Margin="0,0,8,0" Click="SaveCollection_Click" IsEnabled="False"/>
                <Button x:Name="StopButton" Content="Stop" Width="80" Margin="0,0,8,0" Click="Stop_Click" IsEnabled="False"/>
                <Button x:Name="RunButton" Content="Discover" Width="100" Click="Run_Click"/>
            </StackPanel>
        </DockPanel>
    </Grid>
</Window>
```

- [ ] **Step 2: Create `FuzzWindow.xaml.cs`**

Create `src/ApiTester.App/FuzzWindow.xaml.cs`:

```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ApiTester.Core;

namespace ApiTester.App;

public partial class FuzzWindow : Window
{
    public sealed record CertChoice(string Label, X509Certificate2? Cert, string? Thumbprint);

    /// <summary>A discovered endpoint the caller can open in a tab.</summary>
    public sealed record DiscoveredEndpoint(string Method, string BaseUrl, string Path, string? CertThumbprint);

    public sealed class Row
    {
        public string OutcomeLabel { get; init; } = "";
        public string StatusText { get; init; } = "";
        public string Method { get; init; } = "";
        public string SizeText { get; init; } = "";
        public string Ms { get; init; } = "";
        public string Path { get; init; } = "";
        public FuzzResult Result { get; init; } = null!;
    }

    private readonly AppState _state;
    private readonly ApiClient _client;
    private readonly IReadOnlyList<CertChoice> _certs;
    private readonly bool _insecure;
    private readonly int _timeout;
    private readonly ObservableCollection<Row> _rows = new();
    private readonly List<FuzzResult> _all = new();
    private CancellationTokenSource? _cts;

    /// <summary>Set when the user double-clicks a row to open it in a tab; the owner reads it after close.</summary>
    public DiscoveredEndpoint? OpenRequested { get; private set; }

    public FuzzWindow(AppState state, ApiClient client, string baseUrl, string? certThumbprint,
        IReadOnlyList<CertChoice> certs, bool insecure, int timeout)
    {
        InitializeComponent();
        _state = state;
        _client = client;
        _certs = certs;
        _insecure = insecure;
        _timeout = timeout;
        BaseUrlBox.Text = baseUrl;
        CertCombo.ItemsSource = certs.Select(c => c.Label).ToList();
        int idx = certs.ToList().FindIndex(c => c.Thumbprint == certThumbprint);
        CertCombo.SelectedIndex = idx >= 0 ? idx : 0;
        ResultsGrid.ItemsSource = _rows;
    }

    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyDarkTitleBar(this);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Wordlist (*.txt)|*.txt|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == true) WordlistPathBox.Text = dlg.FileName;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        string listText = !string.IsNullOrWhiteSpace(PasteBox.Text) ? PasteBox.Text
            : !string.IsNullOrWhiteSpace(WordlistPathBox.Text) && System.IO.File.Exists(WordlistPathBox.Text)
                ? System.IO.File.ReadAllText(WordlistPathBox.Text) : "";
        var entries = EndpointList.Parse(listText);
        if (entries.Count == 0) { StatusText.Text = "Add a wordlist file or paste some endpoints first."; return; }

        var methods = new List<string>();
        if (Mget.IsChecked == true) methods.Add("GET");
        if (Mhead.IsChecked == true) methods.Add("HEAD");
        if (Mpost.IsChecked == true) methods.Add("POST");
        if (Mput.IsChecked == true) methods.Add("PUT");
        if (Mdelete.IsChecked == true) methods.Add("DELETE");
        if (methods.Count == 0) methods.Add("GET");

        int concurrency = int.TryParse(ConcurrencyBox.Text, out var c) && c > 0 ? c : 8;
        var cert = CertCombo.SelectedIndex >= 0 && CertCombo.SelectedIndex < _certs.Count ? _certs[CertCombo.SelectedIndex].Cert : null;
        string baseUrl = BaseUrlBox.Text.Trim();

        _rows.Clear();
        _all.Clear();
        SaveCollectionButton.IsEnabled = false;
        RunButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _cts = new CancellationTokenSource();

        var plan = new FuzzPlan { BaseUrl = baseUrl, Entries = entries, Methods = methods, Concurrency = concurrency };
        var captureLock = new object();
        async Task<ApiResponse> Send(ApiRequest request, CancellationToken ct)
        {
            var headers = request.Headers.ToList();
            lock (captureLock) TokenService.AutoAttach(_state, request.Url, headers, out _);
            var probe = request with { Headers = headers, Timeout = System.TimeSpan.FromSeconds(_timeout) };
            var response = await _client.SendAsync(probe, cert, _insecure, followRedirects: false, cancellationToken: ct);
            if (response.Error is null) lock (captureLock) TokenService.Capture(_state, request.Url, response.Body, response.ContentType, response.Headers);
            return response;
        }

        var progress = new Progress<FuzzProgress>(p =>
        {
            AddRow(p.Last);
            StatusText.Text = $"probing {p.Completed}/{p.Total}…";
        });

        try
        {
            var report = await EndpointFuzzer.RunAsync(plan, Send, progress, _cts.Token);
            StatusText.Text = $"{report.Total} probed · {report.Discovered} discovered.";
            SaveCollectionButton.IsEnabled = report.Discovered > 0;
        }
        catch (System.OperationCanceledException) { StatusText.Text = $"Stopped. {_all.Count} probed."; }
        finally { RunButton.IsEnabled = true; StopButton.IsEnabled = false; _cts?.Dispose(); _cts = null; }
    }

    private void AddRow(FuzzResult r)
    {
        _all.Add(r);
        if (HideNoise.IsChecked == true && !FuzzClassifier.IsDiscovery(r.Outcome)) return;
        _rows.Add(ToRow(r));
    }

    private static Row ToRow(FuzzResult r) => new()
    {
        OutcomeLabel = r.Outcome.ToString(),
        StatusText = r.StatusCode?.ToString() ?? "ERR",
        Method = r.Method,
        SizeText = r.Error is null ? Human(r.SizeBytes) : "—",
        Ms = r.Elapsed.TotalMilliseconds.ToString("F0"),
        Path = r.Path,
        Result = r
    };

    private static string Human(long b) => b < 1024 ? $"{b} B" : b < 1048576 ? $"{b / 1024.0:F1} KB" : $"{b / 1048576.0:F1} MB";

    private void HideNoise_Toggle(object sender, RoutedEventArgs e)
    {
        if (_rows is null) return;
        _rows.Clear();
        foreach (var r in _all)
        {
            if (HideNoise.IsChecked == true && !FuzzClassifier.IsDiscovery(r.Outcome)) continue;
            _rows.Add(ToRow(r));
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Results_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsGrid.SelectedItem is Row row)
        {
            var cert = CertCombo.SelectedIndex >= 0 && CertCombo.SelectedIndex < _certs.Count ? _certs[CertCombo.SelectedIndex].Thumbprint : null;
            OpenRequested = new DiscoveredEndpoint(row.Method, BaseUrlBox.Text.Trim(), row.Path, cert);
            Close();
        }
    }

    private void SaveCollection_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Show(this, "Save discovered", "Collection name", "Discovered");
        if (string.IsNullOrWhiteSpace(name)) return;
        var cert = CertCombo.SelectedIndex >= 0 && CertCombo.SelectedIndex < _certs.Count ? _certs[CertCombo.SelectedIndex].Thumbprint : null;
        var folder = _state.Collections.FirstOrDefault(c => c.IsFolder && c.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
        if (folder is null) { folder = new CollectionNode { Name = name, IsFolder = true }; _state.Collections.Add(folder); }
        int added = 0;
        foreach (var r in _all.Where(x => FuzzClassifier.IsDiscovery(x.Outcome)))
        {
            folder.Children.Add(new CollectionNode
            {
                Name = $"{r.Method} {r.Path}", IsFolder = false,
                Request = new RequestModel { Method = r.Method, BaseUrl = BaseUrlBox.Text.Trim(), Path = r.Path, CertThumbprint = cert }
            });
            added++;
        }
        StatusText.Text = $"Saved {added} endpoint(s) to “{name}”. Reopen Collections to see them.";
        DiscoveredSaved = true;
    }

    /// <summary>True if the user saved discovered endpoints into the shared state (owner should refresh).</summary>
    public bool DiscoveredSaved { get; private set; }
}
```

- [ ] **Step 3: Wire the toolbar button + Ctrl+Enter in MainWindow**

`src/ApiTester.App/MainWindow.xaml`: next to `ImportButton` (line ~183) add:

```xml
                <Button x:Name="DiscoverButton" DockPanel.Dock="Right" Content="Discover…" Width="92"
                        VerticalAlignment="Bottom" Margin="4,0,0,0" Click="DiscoverButton_Click"
                        ToolTip="Probe a wordlist of endpoints against this website to see which ones exist"/>
```

Add a Ctrl+Enter binding for Send. In the `<Window …>` open tag add (if no `Window.InputBindings` block exists yet):

```xml
    <Window.InputBindings>
        <KeyBinding Key="Return" Modifiers="Ctrl" Command="{x:Static ApplicationCommands.NotACommand}"/>
    </Window.InputBindings>
```

That placeholder is wrong for our use — instead bind to a handler via code. Simplest reliable approach: in the `MainWindow` constructor (end), add:

```csharp
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => { if (SendButton.IsEnabled) _ = SendRequestAsync(); }),
            new KeyGesture(Key.Return, ModifierKeys.Control)));
```

If no `RelayCommand` exists in the project, add this small class to `MainWindow.xaml.cs` (bottom of the file, inside the namespace):

```csharp
internal sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly System.Action<object?> _run;
    public RelayCommand(System.Action<object?> run) => _run = run;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _run(parameter);
    public event System.EventHandler? CanExecuteChanged { add { } remove { } }
}
```

(Do not add the XAML `Window.InputBindings` block if you use the code-behind approach — pick one; code-behind is recommended since it reuses `SendRequestAsync`.)

- [ ] **Step 4: Add the open handler in MainWindow.xaml.cs**

Add near the other button handlers:

```csharp
    private void DiscoverButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveRequest is { } m) CaptureControlsInto(m);
        var certs = _allOptions.Select(o => new FuzzWindow.CertChoice(o.Label, o.Cert, o.Thumbprint)).ToList();
        var win = new FuzzWindow(_state, _apiClient,
            ActiveRequest?.BaseUrl ?? BaseUrlBox.Text.Trim(),
            SelectedThumbprint(), certs,
            IgnoreServerCertCheck.IsChecked == true, ParseTimeout()) { Owner = this };
        win.ShowDialog();

        if (win.OpenRequested is { } d)
        {
            var model = new RequestModel { Method = d.Method, BaseUrl = d.BaseUrl, Path = d.Path, CertThumbprint = d.CertThumbprint };
            var tab = new RequestTab(model);
            _tabs.Add(tab);
            TabStrip.SelectedItem = tab;
            StatusText.Text = $"Opened {d.Method} {d.Path} from discovery.";
        }
        if (win.DiscoveredSaved)
        {
            // Rebuild the collections view from the shared state so saved endpoints appear.
            _collections.Clear();
            foreach (var c in _state.Collections) _collections.Add(c);
            UpdateCollectionsHint();
            SetSidebarMode(history: false);
        }
    }
```

Verify field names against the file: `_allOptions` (CertOption list with `.Label/.Cert/.Thumbprint`), `_apiClient` (the `ApiClient` field used by `SendRequestAsync`), `_state`, `_collections`, `_tabs`, `SelectedThumbprint()`, `ParseTimeout()`, `IgnoreServerCertCheck`, `UpdateCollectionsHint()`, `SetSidebarMode(bool)`, `RequestTab`. If the `ApiClient` field has a different name, use that. Adjust to the real names.

- [ ] **Step 5: Build + smoke**

Run: `dotnet build ApiTester.sln` → 0 warnings.
Run: `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj` → all pass.

Manual smoke (human, later): open Discover, paste `/\n/nope`, Discover → grid fills; double-click opens a tab; Ctrl+Enter sends; Save-to-collection populates the sidebar.

- [ ] **Step 6: Commit**

```bash
git add src/ApiTester.App/FuzzWindow.xaml src/ApiTester.App/FuzzWindow.xaml.cs src/ApiTester.App/MainWindow.xaml src/ApiTester.App/MainWindow.xaml.cs
git commit -m "Add the Discover endpoints window and Ctrl+Enter send to the GUI"
```

---

### Task 5: Bundled wordlist, docs, changelog, version bump

**Files:**
- Create: `wordlists/common-api-endpoints.txt`
- Modify: `src/ApiTester.App/HelpWindow.xaml.cs` (new "Discovering endpoints" section)
- Modify: `README.md`, `docs/index.html`, `CHANGELOG.md`
- Modify: both csprojs (1.26.0 → 1.27.0)

- [ ] **Step 1: Create the starter wordlist**

Create `wordlists/common-api-endpoints.txt`:

```
# Common API endpoints to probe when no documentation is available.
# One per line; "METHOD path" pins a method; blank lines and #comments are ignored.
/
/health
/healthz
/ready
/status
/ping
/version
/info
/metrics
/api
/api/v1
/api/v2
/openapi.json
/swagger.json
/swagger/index.html
/.well-known/openapi
/users
/user
/accounts
/account
/login
POST /login
/logout
/auth
POST /auth/token
/token
/session
/admin
/config
/settings
/products
/orders
/items
/search
/files
/upload
/download
/events
/logs
/jobs
/tasks
/notifications
/webhooks
/roles
/permissions
/groups
/teams
```

- [ ] **Step 2: GUI help section**

In `src/ApiTester.App/HelpWindow.xaml.cs`, add to `_sections` after "Collections & history":

```csharp
            ("Discovering endpoints", Discovery),
```

Add the builder (house style — `Section/P/Sub/Bullets/CodeLine/NoteBox`):

```csharp
    private UIElement Discovery() => Section("Discovering endpoints",
        P("When an API ships without documentation, use Discover to find out which endpoints exist. " +
          "Click “Discover…” in the toolbar, point it at a website, choose (or paste) a list of candidate " +
          "paths, and it sends a request to each one with your client certificate and any captured token."),
        Sub("READING THE RESULTS"),
        P("Each row shows the outcome: Found (2xx), Unauthorized (401/403 — it exists but needs auth), " +
          "MethodNotAllowed (405 — it exists, wrong method), Redirect, ServerError, NotFound, or Error. " +
          "Everything except 404 and connection errors is treated as a discovery. Hide the noise with the " +
          "“Hide 404s / errors” toggle."),
        Sub("TURNING FINDINGS INTO REQUESTS"),
        P("Double-click a row to open that endpoint in a new request tab, or “Save discovered to collection…” " +
          "to store them all as saved requests you can run later."),
        NoteBox("The same discovery runs headless: certapi fuzz <website> -w <wordlist>. A starter wordlist " +
                "ships in the repo under wordlists/common-api-endpoints.txt."));
```

- [ ] **Step 3: CHANGELOG**

Add at the top below the intro:

```markdown
## [1.27.0] - 2026-07-17

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
- **Ctrl+Enter** sends the current request in the app.

### Changed
- Workspaces exported from the app are now stamped with the current schema version, so an
  explicit “None (never send auth)” survives a round-trip through export and re-import.
```

- [ ] **Step 4: README + Pages**

README.md — add to the feature list:

```markdown
- **Endpoint discovery** — probe a wordlist against a website to map an undocumented API, in the
  app (**Discover…**) or headless (`certapi fuzz`). Discoveries open as tabs or save as a collection.
```

Add a short "Discovering endpoints" subsection near the collections/usage area with a CLI example:

```markdown
### Discovering endpoints

No API docs? Probe a wordlist to see what exists:

    certapi fuzz https://api.example.com -w wordlists/common-api-endpoints.txt --cert "CN=My Client"

Each line is a path (or `METHOD path`); `#` comments and blanks are ignored. Anything but a 404 or
a connection error counts as a discovery. Add `--save-collection Discovered` to keep the hits, or
`--json` for machine output. In the app, use **Discover…** in the toolbar.
```

`docs/index.html` — add a features-grid card copying an adjacent card's structure:
- Title `Endpoint discovery`
- Body: `Probe a wordlist against a website to map an undocumented API — in the app's Discover window or headless with certapi fuzz. Hits open as tabs or save as a collection.`

And add a `certapi fuzz` example line to the page's CLI section.

- [ ] **Step 5: Version bump**

Both `src/ApiTester.Cli/ApiTester.Cli.csproj` and `src/ApiTester.App/ApiTester.App.csproj`: `<Version>1.26.0</Version>` → `1.27.0`.

- [ ] **Step 6: Verify + commit**

Run: `dotnet build ApiTester.sln` (0 warnings), `dotnet test tests/ApiTester.Tests/ApiTester.Tests.csproj` (all pass), `dotnet run --project src/ApiTester.Cli -- --version` → `certapi 1.27.0`, and `dotnet run --project src/ApiTester.Cli -- help fuzz` (examples render).

```bash
git add wordlists/common-api-endpoints.txt src/ApiTester.App/HelpWindow.xaml.cs README.md docs/index.html CHANGELOG.md src/ApiTester.Cli/ApiTester.Cli.csproj src/ApiTester.App/ApiTester.App.csproj
git commit -m "Document endpoint discovery and bump to 1.27.0"
```

---

## Plan Self-Review (completed)

- **Spec coverage:** wordlist parsing (Task 1), engine (Task 2), CLI fuzz incl. stdin/save-collection/filters/json (Task 3), GUI window + Ctrl+Enter (Task 4), bundled wordlist + docs + version (Task 5). Auto-token priming is in both the CLI and GUI send closures.
- **Type consistency:** `EndpointEntry`, `EndpointList.Parse`, `FuzzOutcome`, `FuzzClassifier.Classify/IsDiscovery`, `FuzzResult`, `FuzzProgress`, `FuzzPlan`, `FuzzReport`, `EndpointFuzzer.RunAsync` names match across tasks; CLI/GUI both build the `send` closure with `followRedirects: false` and per-host token attach/capture.
- **Judgment calls flagged for implementers:** verify `VariableResolver.Resolve`'s tuple element name (`.Resolved` vs `.Item1`); verify MainWindow field names (`_apiClient`, `_allOptions`, `_state`, `_collections`); pick code-behind Ctrl+Enter (not the XAML placeholder). Anchor on quoted code, not line numbers.
