# Kanban Board

A lightweight single-developer kanban board with:

- .NET 10 API and static web UI
- MariaDB persistence
- HTTPS-ready MCP server for agent tooling
- Docker Compose for the whole stack

## Run locally

Generate local certificates once with mkcert:

```bash
./scripts/create-local-certs.sh
```

This creates `./certs/localhost.pem` and `./certs/localhost-key.pem`, which both ASP.NET Core hosts use in development.

Run the web app and API over HTTPS:

```bash
export ConnectionStrings__Kanban="Server=127.0.0.1;Port=3306;Database=kanban;User ID=kanban;Password=secret;"
dotnet run --launch-profile https --project ./KanbanBoard.Api
```

The API and UI will be available at [https://localhost:7256](https://localhost:7256).

To run the MCP server locally over HTTPS, use the same `InternalApi__SharedSecret` value that the API is using:

```bash
export InternalApi__SharedSecret=replace-with-random-32-plus-character-internal-secret
dotnet run --launch-profile https --project ./KanbanBoard.Mcp
```

The MCP endpoint is exposed at [https://localhost:3001/mcp](https://localhost:3001/mcp).

## Run with Docker Compose

Generate local certificates first:

```bash
./scripts/create-local-certs.sh
```

```bash
docker compose up --build
```

The API expects `ConnectionStrings__Kanban` to point at an existing MariaDB database. The Compose file does not start a MariaDB service.

You can override the default host ports if they conflict with another local stack:

```bash
KANBAN_DOCKER_HTTPS_PORT=9443 KANBAN_MCP_HTTPS_PORT=3002 docker compose up --build
```

For local Docker Compose testing, put `ConnectionStrings__Kanban`, `Auth__*`, `PersonalAccessTokens__*`, and `InternalApi__SharedSecret` settings in a repo-root `.env`. The file is ignored by git. For `dotnet run`, export those variables in your shell first.

Services:

- Web UI and API: [https://localhost:8444](https://localhost:8444)
- MCP server from the host: [https://localhost:3001/mcp](https://localhost:3001/mcp)
- MCP server from Docker containers on `trefry-network`: `http://kanban-mcp:3000/mcp`

The API container still listens on internal HTTP port `8080` so the MCP container can call it as `http://api:8080` without certificate hostname mismatches on the Docker network.

## MCP tools

The MCP service requires `Authorization: Bearer <personal-access-token>` on `/mcp`. Users create PATs from the settings page; the MCP server validates them through a secret-protected internal API endpoint and forwards tool calls to the API with internal service authentication. Each PAT has a scope: `full` (default; also every token created before scopes existed) or `read`. A `read` token can use every read tool, but write tools (create, update, move, claim, comment, delete) return a `scope_denied` error without calling the API, and the API also refuses internal write requests made on behalf of a `read` token.

The MCP service exposes tools for:

- Listing projects
- Creating projects
- Fetching a board
- Listing, creating, updating, and retrieving epics
- Deleting epics
- Listing, creating, updating, and retrieving epic documents
- Fetching issue items
- Fetching a single work item with its description and comments
- Searching work items by words in titles and descriptions
- Creating work items
- Updating work items
- Moving work items
- Deleting work items
- Deleting projects

`GetBoard` accepts an optional `status` filter so callers can request only a single column's items instead of the full board payload.
`GetIssues` returns only issue-type work items and accepts optional `projectId` and `status` filters.
`ListProjects` accepts an optional `includeArchived` flag so callers can include archived projects when needed.
`ListEpics` accepts an optional `includeArchived` flag so callers can include archived epics when needed.
`GetBoard` accepts an optional `includeArchivedEpics` flag so callers can include items linked to archived epics.
Epics can be managed through project-scoped list/create endpoints plus retrieve/update/delete endpoints for individual epics.
Epic documents can be managed through epic-scoped list/create endpoints plus retrieve/update endpoints for individual documents.
`GetWorkItem` returns one work item (title, description, status, comments, and other fields) and returns a not-found error for unknown ids.
Work item reads (`GetWorkItem`, `ListWorkItems`, `ListEpicWorkItems`, `GetIssues`, `GetBoard`) include the item's live claim as `claimant`, `claimant_id`, `claim_marker` and `claim_expires_at_utc`; all four are `null` when the item is unclaimed or its claim has expired.
Work item reads also include `blockers` (the items this one is blocked by) and `dependents` (the items it blocks); each entry is `{id, title, status}` with the other item's current status, and both are `[]` when there are none.
`ListWorkItems`, `ListEpicWorkItems` and `GetIssues` accept an optional `unblocked_only` flag (default `false`); when `true` they return an item only when every one of its blockers is `Done` (items with no blockers included).
`ListWorkItems` also accepts the polling arguments described under [Polling work items](#polling-work-items).
`SearchWorkItems` matches every whitespace-separated word of `query` case-insensitively against titles and descriptions, accepts optional `projectId` and `limit` (default 25, max 100), includes closed items, and returns only ids, project ids, titles, types, and statuses. It is backed by `GET /api/items/search`.

### Polling work items

`list_work_items` takes these optional arguments in addition to `projectId`, `epicId`, `type`, `status`, `includeArchivedEpics` and `unblocked_only`. A call without them returns what it always did: an array of every matching item in board order, with comments.

| Argument | Meaning |
| --- | --- |
| `label` | Only items that carry this label. The whole label must match, ignoring case: `api` does not match an item labelled `rapid` or `api-v2`. |
| `updated_since` | ISO 8601 timestamp (one without an offset is read as UTC). Only items whose `updatedAtUtc` is at or after it. |
| `claim_state` | `unclaimed` (no live claim, which includes a claim that has expired), `claimed` (a live claim) or `expired` (a claim that expired and was neither released nor replaced). |
| `limit` | Page size, 1 to 200. |
| `cursor` | `next_cursor` of the previous page. Page size is 50 when `limit` is not passed. |
| `include_comments` | `false` returns every item with `comments: []` and reads no comment. Default `true`. |

- **Pages.** With `limit` or `cursor` the result is an object, `{"items": [...], "next_cursor": "..."}`, instead of an array. Pass `next_cursor` back unchanged as `cursor`, with the same filters, until a page comes without `next_cursor`. Pages are ordered oldest item first (creation time, then id), not in board order: that order never changes, so every matching item that exists for the whole walk is returned exactly once even when items are moved or edited during it. An item created during the walk, or one that stops or starts matching the filters during it, may or may not appear. The cursor is opaque; do not build or parse one.
- **`updatedAtUtc` is the content timestamp.** It changes when the item's fields change (including a move to another column or position) and when a comment is added to it. It does not change when a sibling is moved or reordered around it, when it is claimed, renewed or released, when its claim expires, or when a dependency is added, removed or finished. The stamp is taken before the change commits, so a change can become visible slightly after another one that carries a later stamp: when following changes with `updated_since`, poll with a small overlap (for example a few seconds before the latest `updatedAtUtc` seen) and de-duplicate by id plus `updatedAtUtc`.
- **Claim state is read from the clock on every call.** A claim that expires writes nothing, so `updated_since` cannot report it, and neither can it report an item that became unblocked. To find work that is ready to take, poll with `status`, `unblocked_only: true` and `claim_state: "unclaimed"` and no `updated_since`; use `updated_since` to follow content changes.
- A malformed `label`, `updated_since`, `claim_state`, `limit` or `cursor` is refused `invalid_argument`.

### Claims

An orchestrator takes a work item with `claim_work_item(id, marker, ttl_seconds)`, keeps it with `renew_work_item_claim(id, marker, ttl_seconds)` and gives it up with `release_work_item_claim(id, marker)`. `id` is the work item id (a GUID), `marker` is a caller-chosen id for the claim such as a run id (at most 200 characters, compared exactly), and `ttl_seconds` is 1 to 86400. All three return the work item.

- The claimant is always the identity that owns the calling token. No argument can name another one.
- A claim succeeds when the item is unclaimed, its claim has expired, or the caller already holds it under the same marker (the expiry is reset). Any other marker or identity on a live claim is refused `already_claimed`. The write is one conditional `UPDATE`, so two concurrent claims have exactly one winner.
- Renew is refused `conflict` when the caller's claim has lapsed or was released, and `already_claimed` when someone else now holds the item. Release is refused the same way; the holder may release its own claim after it lapsed.
- The claim tools are write tools: a `read` token is refused `scope_denied` before the API is called.

### Dependencies

`add_work_item_dependency(id, blocked_by_id)` records that work item `id` is blocked by work item `blocked_by_id`, and `remove_work_item_dependency(id, blocked_by_id)` deletes that edge. Both return work item `id` with its `blockers` and `dependents`.

- A dependency that would make an item block itself, directly or through other items (including `id` equal to `blocked_by_id`), is refused `cycle` and nothing is written. The check and the insert run one caller at a time, so concurrent adds cannot close a loop between them. An add that waits 10 s for its turn is refused `busy`, with nothing written, and can be retried.
- Adding an edge that already exists, or removing one that does not, succeeds and changes nothing. An unknown `id` or `blocked_by_id` is refused `not_found`.
- The two items may belong to different projects. Deleting a work item (or its project) deletes its dependencies.
- A blocker counts as finished only in status `Done`; a `Closed` blocker still blocks, so remove the dependency if the blocker was abandoned.
- Both are write tools: a `read` token is refused `scope_denied` before the API is called.

### Tool errors

A refused tool call returns a tool result with `isError: true`. Its `structuredContent` is `{"code": "...", "message": "..."}` and its single text block holds the same JSON. Branch on `code`; `message` is for people and may change.

| Code | Meaning |
| --- | --- |
| `scope_denied` | The token (or missing token) is not allowed to make this call. |
| `not_found` | The addressed entity does not exist. |
| `already_claimed` | The work item has a live claim held by another identity or marker. |
| `conflict` | The entity is no longer in the state the call depends on; nothing was changed. |
| `invalid_argument` | An argument is missing or out of range. |
| `cycle` | The dependency would make a work item block itself, directly or through other items; nothing was changed. |
| `busy` | The server could not take the call in time because of other callers (today: `add_work_item_dependency` waited 10 s for other dependency changes); nothing was changed and the call can be retried. |

Scope refusals from every write tool, unknown ids from `GetWorkItem`, malformed arguments of `ListWorkItems`, and every refusal from the claim and dependency tools use this envelope. Other failures (for example an unreachable API) are reported as a plain MCP error without a code.

### Service identities

A service identity is a user that no person signs in as, for a program such as the factory orchestrator. Create one with the API's own command; it needs no browser or OIDC login and prints one `full` and one `read` token:

```bash
dotnet KanbanBoard.Api.dll create-service-identity --name factory [--rotate]
```

The name is the identity's display name and the `claimant` on the items it claims. Running the command again with the same name adds a new token pair to the same identity; `--rotate` also revokes its existing tokens once the new pair is issued. See [docs/deployment.md](docs/deployment.md#factory-service-identity) for the production steps.

### Available tool names

The names below are the C# method names; on the wire each tool is its snake_case form (`ListProjects` is `list_projects`).

- `ListProjects`
- `CreateProject`
- `GetBoard`
- `ListEpics`
- `GetEpic`
- `CreateEpic`
- `UpdateEpic`
- `DeleteEpic`
- `ListEpicDocuments`
- `GetEpicDocument`
- `CreateEpicDocument`
- `UpdateEpicDocument`
- `DeleteProject`
- `ListWorkItems`
- `ListEpicWorkItems`
- `GetIssues`
- `GetWorkItem`
- `SearchWorkItems`
- `AddWorkItemComment`
- `AddIssueComment`
- `CreateWorkItem`
- `CreateEpicWorkItem`
- `UpdateWorkItem`
- `MoveWorkItem`
- `DeleteWorkItem`
- `ClaimWorkItem`
- `RenewWorkItemClaim`
- `ReleaseWorkItemClaim`
- `AddWorkItemDependency`
- `RemoveWorkItemDependency`

## Production deployment

Production deployment is handled by GitHub Actions, GHCR, and Docker Compose. Caddy is managed separately on the host.

- Web UI/API: `https://kanban.trefry.net`
- MCP: `https://kanban-mcp.trefry.net/mcp`
- Deployment guide: [docs/deployment.md](docs/deployment.md)
- MariaDB schema: [docs/mariadb-schema.md](docs/mariadb-schema.md)
- SQLite to MariaDB data migration: [docs/sqlite-to-mariadb.md](docs/sqlite-to-mariadb.md)

Production secrets such as `ConnectionStrings__Kanban`, `Auth__ClientSecret`, `PersonalAccessTokens__EncryptionKey`, and `InternalApi__SharedSecret` are supplied through GitHub-managed Actions secrets and written to `/opt/kanban-board/.env.release` during deployment. Local `.env` files are ignored and are for local development only.
