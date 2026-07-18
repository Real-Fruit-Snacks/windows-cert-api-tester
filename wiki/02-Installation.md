# 2. Installation

## Requirements

- **Windows 10 or 11**, 64-bit.
- Nothing else for the self-contained downloads — the .NET 9 runtime is bundled.
- The **Rendered website** view uses the Microsoft Edge **WebView2** runtime, which ships with
  Windows 11 and up-to-date Windows 10. If it's missing, only that one tab is affected; everything
  else works.

## Download (recommended)

Grab the latest release from the repository's **Releases** page. Each release attaches:

| Asset | What it is |
|---|---|
| `ApiTester.App.exe` | The desktop app (self-contained, x64) |
| `certapi.exe` | The command-line client (self-contained, x64) |
| `common-api-endpoints.txt` | A starter wordlist for [endpoint discovery](14-Endpoint-Discovery.md) |
| `certapi-source-<tag>.zip` | The full repository source at that tag |

Both `.exe` files are **portable** — no installer, no admin rights. Put them anywhere (a USB stick, a
locked-down VM) and run them. To use `certapi` from any prompt, drop `certapi.exe` in a folder that's
on your `PATH`.

## First run

1. Double-click `ApiTester.App.exe`. The main window opens on your last workspace (empty the first
   time).
2. If you have client certificates in your Windows store, they appear in the **CERTIFICATE** dropdown
   on the request line. No certs? That's fine — you can still test any endpoint that doesn't require
   one.
3. Prove the whole certificate path works without a real server: click **Run Self-Test** at the
   bottom of the window (or run `certapi selftest`). See [Self-Test](23-Troubleshooting.md#self-test).

## Where your data lives

The app stores its workspace — tabs, collections, environments, history, saved tokens, and your theme
choice — in:

```
%AppData%\CertApiTester\state.json
```

That's a single JSON file. Back it up, or hand it to a teammate, and everything travels with it. The
CLI reads the same file by default, so requests you build in the app are runnable headless
immediately. You can also point either tool at a separate **workspace file** with `--workspace` — see
[Collections & History](10-Collections-and-History.md).

## Verifying a download

The self-contained `.exe` files are large (~35–65 MB) because they bundle the runtime — that's
expected. If your organization requires it, verify the file against the release, and note that the
executables are **not** code-signed unless your build pipeline signs them.

## Building it yourself

Prefer to compile from source? See [Building from Source](25-Building-from-Source.md).

Next: [Quick Start](03-Quick-Start.md).
