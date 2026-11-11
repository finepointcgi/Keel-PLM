# AI assistants (MCP)

Keel runs a [Model Context Protocol](https://modelcontextprotocol.io) server at `/api/v1/mcp`, so an AI assistant inside the
enclave can answer questions about parts, BOMs, documents, changes, requirements and programs. Design and rationale:
[ADR-0064](../adr/0064-mcp-server-for-ai-assistants.md).

**Every tool only reads.** Each one calls the same service as the REST API, as the signed-in person, so the export-control and
CUI read rule, redaction and auditing are exactly those of the rest of Keel.

| Tool | What it returns |
| --- | --- |
| `search_parts`, `get_part` | Parts and one part's revisions, iterations, lifecycle and attributes |
| `get_bom`, `where_used` | A BOM explosion (latest configuration) and the assemblies using a part |
| `search_documents`, `get_document` | Documents and one document's metadata and file list (never file contents) |
| `search_changes`, `get_change`, `get_change_impact` | PRs, ECRs, ECNs and change tasks, one change, and its impact analysis |
| `search_specifications`, `get_specification`, `get_requirement_coverage`, `get_requirement_trace` | Requirement specifications, requirements, coverage and trace links |
| `search_programs`, `get_program_dashboard` | Programs and one program's dashboard |

## Before you connect an assistant

- **The model must run inside the enclave.** Tool results contain marked data. Use a local model (for example Ollama or
  LM Studio through Hearth, on a host inside the boundary). Never point an assistant that uses a hosted model at Keel.
- **It acts as the person who signs in.** There are no API keys or service accounts for MCP. The assistant sees what that
  person may see, and every read is in the audit log under their name.
- Treat the assistant host like any other system that handles CUI: it is in scope for your SSP.

## Results

Each result starts with a markings line, then the data as JSON:

```
MARKINGS: CUI//EXPT · ITAR XI(d) · DISTRIBUTION D
Handle this data as marked.
{ "number": "P-000123", … }
```

Objects you may not read are left out of searches, show as redacted lines in BOMs, and otherwise read "Not found, or not
something you may read." The same answer is given for an object that doesn't exist.

## Sign-in

The assistant needs a bearer token for the person from Keel's IdP. MCP clients that support MCP authorization do this
themselves:

1. The client calls `/api/v1/mcp`, gets `401` with `WWW-Authenticate: Bearer resource_metadata="…"`.
2. It reads `/.well-known/oauth-protected-resource/api/v1/mcp`, which names the IdP (`Keel:Auth:Authority`).
3. It signs the person in with authorization code + PKCE and a loopback redirect, and sends the token on each call.

Register a public client for this in the IdP (the dev realm's `keel-mcp` client is the template): standard flow, PKCE S256,
redirect URIs `http://127.0.0.1/*` and `http://localhost/*`, the same attribute and role mappers as `keel-web`, and the
`keel-api` audience mapper.

**Claude Code** (only on a host inside the enclave, with a model endpoint inside it):

```bash
claude mcp add --transport http keel https://keel.example.mil/api/v1/mcp --client-id keel-mcp
```

**Hearth:** **Tools and MCP › Add an integration › Keel PLM**, enter the address; Hearth opens the IdP sign-in.

## Audit

Each tool call writes `mcp.tool_call` (tool, arguments, outcome) to the audit chain with the request's correlation id. The same
id is on the `*.read` and denial events the call caused, so `keel-audit` and the SIEM can tie them together.

## Protocol

Streamable HTTP, stateless: one JSON-RPC request per `POST`, one JSON response. No sessions or server-sent events (`GET` and
`DELETE` return 405). Protocol versions `2025-11-25`, `2025-06-18`, `2025-03-26`; methods `initialize`, `ping`, `tools/list`,
`tools/call`. Requests with a browser `Origin` header are refused. Results over 60,000 characters are cut.

## Linking to Meridian and Workbench

Keel numbers are the shared ids the other engineering apps point at: a Meridian element's tagged values `keel.part` and
`keel.spec` hold a Keel part or specification number, and a Workbench project's **Keel program** custom field holds a Keel
program code. An assistant reads the number in one app and looks it up here with `get_part`, `get_specification` or
`get_program_dashboard`, under the same access rules as any other read.
