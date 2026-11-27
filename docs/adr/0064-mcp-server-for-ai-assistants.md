# ADR-0064: MCP server for AI assistants inside the enclave

**Status:** Accepted · 2026-11-10

## Context
Engineers want to ask an AI assistant questions, and have it do routine work, that span Keel and the systems around it: "what does ECN-000042 affect?", "which
requirements in SPEC-0003 are not verified?", "where is P-000123 used?". Assistants such as Hearth (a local-model chat app) and
Claude Code speak the Model Context Protocol (MCP). Workbench and Meridian already offer MCP servers.

Keel holds export-controlled data. Anything an assistant reads must pass the same read rule as the UI (ADR-0004), carry its
markings, and leave an audit trail (ADR-0005). The model the assistant uses must run inside the enclave: a hosted model would
take marked data outside it.

## Decision
- **Endpoint.** `POST /api/v1/mcp`: the MCP streamable HTTP transport, stateless, one JSON response per request; no sessions and
  no server-sent events (`GET`/`DELETE` → 405). Methods `initialize`, `ping`, `tools/list`, `tools/call`. Requests carrying a
  browser `Origin` are refused.
- **Read tools**, each a thin call to the module service its REST endpoint uses: `search_parts`, `get_part`, `get_bom`,
  `where_used`, `search_documents`, `get_document` (metadata and file list, never file contents), `search_changes`, `get_change`,
  `get_change_impact`, `search_specifications`, `get_specification`, `get_requirement_coverage`, `get_requirement_trace`,
  `search_programs`, `get_program_dashboard`.
- **Write tools**, the same way: `create_part`, `check_out_part`, `update_part_working_copy`, `check_in_part`, `undo_check_out`,
  `add_bom_line`, `remove_bom_line` (on a part the person has checked out), `create_document` (metadata only; files are uploaded
  in Keel, where they are scanned), `create_change` (PR or ECR only), `add_comment`. New objects need their markings stated in
  the call; nothing defaults them. MCP annotations mark them as not read-only (and destructive where they discard or replace),
  so clients ask the person before each one.
- **Never through MCP:** release, approval, e-signatures (they need a step-up sign-in, ADR-0007), lifecycle state changes,
  marking changes, obsoleting, ECNs and change tasks. These stay with people in Keel.
- **Same access path.** Tools run as the caller's `Subject`, so roles, `AccessPolicy` filtering, redaction (A4), check-out
  locks and the services' own audit events (in the same transaction as each change) apply unchanged. Not found and not permitted give the same answer.
- **Markings on every result.** Each result starts with `MARKINGS: <banner>` (the page banner, or the distinct object banners
  it contains) and "Handle this data as marked." The server's instructions tell the client to keep markings with anything it
  quotes and not to send the data to systems that are not cleared for it.
- **Authentication.** The API's own: an IdP bearer token for a person with a PLM role. Keel publishes RFC 9728 protected
  resource metadata at `/.well-known/oauth-protected-resource/api/v1/mcp`, and a 401 on the MCP path names it in
  `WWW-Authenticate`, so MCP clients find the IdP and sign the user in with authorization code + PKCE (the dev realm's
  `keel-mcp` public client allows loopback redirects). No long-lived keys or service accounts: the assistant acts as the person.
- **Audit.** Each call appends `mcp.tool_call` (tool, arguments, outcome) with the request's correlation id, which also
  appears on the read events the call caused.

## Consequences
- An assistant can see no more than its user, and every read is attributable to that user.
- Operators must make sure the assistant's model runs inside the enclave. Keel cannot enforce where the client sends results;
  the markings and instructions make the obligation explicit. Deploying an MCP client is a security-reviewed change like any
  other integration.
- An assistant can create and edit work in progress, but nothing it does is released or signed: a person still reviews and
  releases through workflow, so configuration control is unchanged.

## Alternatives
- **API keys or a service account** (as Workbench uses): simpler for clients, but the actor would be a shared account whose
  attributes must be curated by hand, and the audit trail would lose the person.
- **Reuse the integration pull API**: it serves outbound events to cleared systems, not interactive questions, and has no
  search, BOM or traceability views.
