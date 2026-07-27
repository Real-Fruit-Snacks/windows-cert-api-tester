# 9. Environments & Variables

Reuse one set of requests against many targets — dev, staging, prod — by swapping a named set of
variables instead of editing every URL (Uniform Resource Locator).

## The idea

An **environment** is a named collection of `{{variable}}` values. Anywhere you write `{{name}}` — in
a URL, a header, or a body — it's replaced with the active environment's value when you send. Switch
the active environment and the whole workspace retargets.

Example: an environment **Staging** with

```
host = staging-api.corp
apiKey = abc123
```

lets a request to `https://{{host}}/v1/things` with header `X-Api-Key: {{apiKey}}` hit staging, and a
**Prod** environment retargets it without touching the request.

## Managing environments (app)

- The **ENV** picker in the title bar selects the active environment.
- **Edit** opens the environments manager: add/rename environments, and edit each one's variables as a
  key/value grid.

## Using variables (CLI)

Pick an environment and override individual variables per run:

```powershell
certapi send "https://{{host}}/v1/things" --env Staging
certapi send "https://{{host}}/v1/things" --env Staging --var host=localhost:8080
```

`--var k=v` is repeatable and takes precedence over the environment's value.

## Unresolved variables

If a `{{token}}` has no value:

- By default you get a **warning** and the literal `{{token}}` is sent as-is.
- Add `--strict-vars` (CLI — command-line interface) to make an unresolved variable a **hard error**
  instead — useful in CI (continuous integration) so
  a typo fails the run rather than sending a malformed request. In a
  [data-driven run](13-Data-Driven-Runs.md), this is how a request that needs a per-row value fails
  cleanly when the dataset doesn't supply it.

## Where variables come from

At send time the variable map is built from:

1. The **active environment**'s variables, plus
2. any `--var` **overrides**, plus
3. values **captured** from earlier responses (see [Capturing Values](12-Capturing-Values.md)) — a
   login can save `{{token}}` that later requests use.

## Marking a variable secret

Tick **secret** next to a variable in the environments manager when its value is a credential — an
API key, a password, a token you pasted in by hand. A secret variable is encrypted in the workspace
file with the Windows Data Protection API (DPAPI), scoped to the Windows user who saved it, instead of
sitting in plain text like an ordinary value; a value written by [capture](12-Capturing-Values.md)
gets the flag automatically, since that's exactly the path that lands a token in an environment.
Everything else about the variable — its key, and where `{{name}}` is used — works the same either
way.

## Workspaces vs. environments

An environment lives inside a **workspace** (`state.json` or a `--workspace` file). Export a workspace
to share both the requests and the environments; `certapi export workspace` strips a secret variable's
value by default (its key and its **secret** flag still travel), so a shared workspace doesn't hand
out credentials by accident — `--include-secrets` keeps it, encrypted for whoever exported it. The
app's own **Export workspace…** always keeps secrets, encrypted for your Windows user, so a variable
you marked secret only travels to someone who can decrypt it. See
[Import & Export](17-Import-and-Export.md).

Next: [Collections & History](10-Collections-and-History.md).
