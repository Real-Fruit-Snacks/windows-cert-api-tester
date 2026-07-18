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
with `--workspace team-setup.json`. Note that variable values you typed (which may include secrets)
travel with a workspace — treat exported workspaces accordingly.

## Round-tripping with the app

Imports land in the live workspace (or a `--workspace` file) that the app reads, so anything you
import on the CLI (command-line interface) shows up in the app, and vice versa. See
[Collections & History](10-Collections-and-History.md).

Next: [Mock Server](18-Mock-Server.md).
