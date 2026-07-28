# 27. Configuration files & profiles

A real command in a corporate environment carries the same tedious half every time:

```powershell
certapi send https://api.internal/orders --cert "CN=My Client" --proxy socks5://127.0.0.1:1080 `
  --revocation online --timeout 60 --workspace .\suite.json
```

A **profile** puts that half in a file, so the command becomes:

```powershell
certapi send https://api.internal/orders --profile corp
```

## The file

`certapi.config.json` — JSON, with `//` comments and trailing commas allowed, because people edit
these by hand:

```jsonc
{
  // Used when --profile is not given. Omit for "no default".
  "defaultProfile": "corp",
  "profiles": {
    "corp": {
      "cert": "CN=My Client",
      "store": "CurrentUser",
      "proxy": "socks5://127.0.0.1:1080",
      "noProxyList": "internal.corp,.test",
      "revocation": "online",
      "revocationStrict": false,
      "retry": 3,
      "timeout": 60,
      "insecure": false,
      "workspace": "C:/work/suite.json",
      "headers": { "X-Env": "staging" }
    },
    "local": { "insecure": true }
  }
}
```

Every field is optional, and each command reads only the fields it understands — a profile with a
proxy in it never breaks `certapi certs`.

## Where it is found

First match wins:

1. **`--config <path>`** — named explicitly. A path that does not exist is an error, not a
   fall-through to some other file.
2. **`CERTAPI_CONFIG`** — an environment variable naming a file.
3. **`certapi.config.json`** — found by walking **up** from the working directory, so a
   per-repository configuration works from anywhere inside it.
4. **`%APPDATA%\certapi\config.json`** — your personal default.

`--no-config` ignores all four. That is how a run is made reproducible regardless of what happens
to sit in a parent directory — worth using in continuous integration.

## Precedence

**An explicitly typed flag always wins. The profile fills in what you did not type. The built-in
default stands when neither said anything.**

Naming one of a mutually exclusive pair counts as choosing it: `--cert-file` on the command line
suppresses a profile's `cert`, and `--no-proxy` suppresses a profile's `proxy`, rather than
colliding with it.

## Secrets stay out of the file

Any value may contain `${env:NAME}`, read from the environment when the file is loaded:

```jsonc
{ "profiles": { "corp": { "proxyUser": "svc:${env:PROXY_PASSWORD}" } } }
```

So the file names *which* secret it needs, and the secret itself lives wherever your machine or
pipeline keeps secrets — the file stays safe to commit. A reference to a variable that is not set
is an error naming the profile, the field, and the variable, rather than a silently empty
credential. (The same idea inside a request is [`{{env:NAME}}`](09-Environments-and-Variables.md).)

## Seeing what is in effect

```powershell
certapi config path                    # which file, and by which rule
certapi config profiles                # the names defined, marking the default
certapi config show --profile corp     # the resolved profile, as a command sees it
```

`show` prints `(set)` for a password or a proxy credential rather than the value — a diagnostic
should never be the thing that leaks a secret.

Next: [CLI Reference](21-CLI-Reference.md).
