# 17. Import & Export

Move requests in and out — from a cURL command, from an OpenAPI/Swagger document, and whole
workspaces.

## Import a cURL command

Paste a `curl` command and get a request. Great for turning a "copy as cURL" from browser dev-tools or
a colleague's snippet into an editable, saved request.

```powershell
certapi import curl "curl -X POST https://api.example.com/login -H 'Content-Type: application/json' -d '{}'"
certapi import curl "curl ..." --into "auth"          # into a folder
certapi import curl "curl ..." --workspace suite.json # into a workspace file
```

The parser understands the common flags — method, headers, data, and more.

## Import OpenAPI / Swagger

Turn an OpenAPI (or Swagger) document into a collection of ready-to-send requests:

```powershell
certapi import openapi .\petstore.json
certapi import openapi .\petstore.json --into "petstore"
certapi import openapi .\petstore.json --workspace .\suite.json
```

Paths, methods, base servers, and operation names come across so you can start sending immediately.

## Import a Postman collection

The format most teams already have in hand. Point at a Collection v2.0/v2.1 export:

```powershell
certapi import postman .\orders.postman_collection.json
certapi import postman .\orders.postman_collection.json --into "orders" --workspace .\suite.json
```

Folders, methods, both of Postman's URL forms, query rows, and headers come across, with disabled
rows staying disabled. Bodies map by mode — raw (with the language deciding the content type),
urlencoded, and formdata — and auth maps for bearer, basic, and apikey, with request-level auth
beating folder- and collection-level, exactly as Postman resolves it. `{{variables}}` share their
syntax between the two products, so request text imports unchanged, and collection-level variables
become an environment named after the collection — a variable Postman marked `secret` is stored
encrypted here, the same as any other secret at rest. Two deliberate cautions: a **file** form
part imports disabled (its path came from someone else's machine — point it somewhere real before
sending), and anything that cannot carry across faithfully is a **named warning** on stderr, never
a silent drop.

## Import an Insomnia export

```powershell
certapi import insomnia .\Insomnia_2026-07-28.json
certapi import insomnia .\export.json --into "orders" --workspace .\suite.json
```

Use Insomnia's **Export Data → Insomnia v4 (JSON)**. Folders, methods, URLs, query and header rows
come across with disabled rows staying disabled; text and form bodies map by their media type; and
bearer and basic authentication map directly (a block Insomnia has switched off is ignored rather
than applied). Insomnia's environments become environments here.

Two things about templates are worth knowing, because the two products spell variables differently:

- **`{{ _.name }}` is translated to `{{name}}`** — Insomnia's variable syntax becomes this
  product's, everywhere it appears: URL, query, headers, body, and auth.
- **A tag template — `{% uuid 'v4' %}`, `{% response … %}` — is a small program, not a value**, and
  has no equivalent here. It is left in the text exactly as written and named in a warning at
  import time, which is more useful than dropping it silently and discovering the gap at send time.

## Import a WSDL (SOAP)

```powershell
certapi import wsdl .\OrdersService.wsdl
certapi import wsdl .\OrdersService.wsdl --into "soap" --workspace .\suite.json
```

Reads a WSDL 1.1 document (and the SOAP 1.2 binding variant) and turns each operation into a saved
**POST** at the port's address, with:

- the right content type — `text/xml` plus a `SOAPAction` header for SOAP 1.1, or
  `application/soap+xml;…;action="…"` for SOAP 1.2, where the action travels in the content type
  instead;
- an **envelope skeleton** in the correct envelope namespace, with the operation element in the
  service's target namespace and one placeholder per message part.

**Deliberately minimal, and worth understanding before you use it.** Types are *not* expanded from
the schema: each part becomes a commented placeholder naming its element or type
(`<!-- body: element GetOrder — fill in from the schema -->`). Generating a full instance document
from XML Schema — with imports, restrictions, choices, and substitution groups — is a different
product, and a fabricated body would look authoritative while being wrong. This gets a working SOAP
request about ninety percent written; you fill in the payload.

An imported document or schema (`wsdl:import`, `xsd:import`, `xsd:include`) is **named in a
warning, never fetched** — this reads only the file you name and touches no network. If operations
seem missing, import the file the warning names as well.

## Export as OpenAPI

Export your collections (or a folder) as an OpenAPI document — useful for sharing the shape of an
API (application programming interface) or seeding another tool:

```powershell
certapi export openapi -o api.json                 # everything
certapi export openapi "petstore" -o petstore.json # one folder
```

Auth is exported as a **security scheme** description only — **never the secrets**.

## Export a whole workspace

Bundle your requests, collections, environments, and history into a portable file:

```powershell
certapi export workspace -o team-setup.json
```

Hand it to a teammate (or check it into source control) and they import it, or point their tools at it
with `--workspace team-setup.json`.

Secrets — captured tokens and cookies, a saved request's auth secret, and any variable marked
**secret** — are **stripped by default**, since an exported workspace is a file people end up
emailing to each other. The rest of the export is unaffected: a secret variable keeps its key and its
flag, just not its value. Add `--include-secrets` to keep them instead:

```powershell
certapi export workspace -o team-setup.json --include-secrets
```

Kept secrets are written encrypted for the current Windows user (the same protection the live
workspace uses), so even `--include-secrets` never puts a credential on disk in the clear — a
recipient on another machine or signed in as someone else still can't read them. `certapi` reports on
stderr what was stripped (or kept).

## Round-tripping with the app

Imports land in the live workspace (or a `--workspace` file) that the app reads, so anything you
import on the CLI (command-line interface) shows up in the app, and vice versa. See
[Collections & History](10-Collections-and-History.md).

Next: [Mock Server](18-Mock-Server.md).
