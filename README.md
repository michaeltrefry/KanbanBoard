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

The MCP service requires `Authorization: Bearer <personal-access-token>` on `/mcp`. Users create PATs from the settings page; the MCP server validates them through a secret-protected internal API endpoint and forwards tool calls to the API with internal service authentication. Each PAT has a scope: `full` (default; also every token created before scopes existed) or `read`. A `read` token can use every read tool, but write tools (create, update, move, claim, comment, link, delete) return a `scope_denied` error without calling the API, and the API also refuses internal write requests made on behalf of a `read` token.

The MCP service exposes tools for:

- Listing projects
- Creating projects, and updating them (target repository, rename, archive)
- Fetching a board
- Listing, creating, updating, and retrieving epics
- Deleting epics
- Listing, creating, updating, and retrieving epic documents
- Fetching issue items
- Fetching a single work item with its description and comments
- Adding comments to work items and listing a work item's comments
- Following every change to work items from a cursor (the change feed)
- Searching work items by words in titles and descriptions
- Creating work items
- Updating work items
- Moving work items
- Deleting work items
- Deleting projects

`GetBoard` accepts an optional `status` filter so callers can request only a single column's items instead of the full board payload.
`GetIssues` returns only issue-type work items and accepts optional `projectId` and `status` filters.
`ListProjects` accepts an optional `includeArchived` flag so callers can include archived projects when needed. Every project carries `repository_url` and `default_branch` (see [Projects](#projects)).
`ListEpics` accepts an optional `includeArchived` flag so callers can include archived epics when needed.
`GetBoard` accepts an optional `includeArchivedEpics` flag so callers can include items linked to archived epics.
Epics can be managed through project-scoped list/create endpoints plus retrieve/update/delete endpoints for individual epics.
Epic documents can be managed through epic-scoped list/create endpoints plus retrieve/update endpoints for individual documents.
`GetWorkItem` returns one work item (title, description, status, comments, and other fields) and returns a not-found error for unknown ids.
Work item reads (`GetWorkItem`, `ListWorkItems`, `ListEpicWorkItems`, `GetIssues`, `GetBoard`) include the item's live claim as `claimant`, `claimant_id`, `claim_marker` and `claim_expires_at_utc`; all four are `null` when the item is unclaimed or its claim has expired.
Work item reads also include `blockers` (the items this one is blocked by) and `dependents` (the items it blocks); each entry is `{id, title, status}` with the other item's current status, and both are `[]` when there are none.
Work item reads also include `links`, the external links attached to the item (see [Links](#links)); it is `[]` when there are none.
Work item reads also include `estimate` (always present, `null` when not set), `label_list` (the labels as a list, `[]` when there are none; `labels` is the same labels as one comma-separated string) and `version` (see [Updating and moving work items](#updating-and-moving-work-items)).
`ListWorkItems`, `ListEpicWorkItems` and `GetIssues` accept an optional `unblocked_only` flag (default `false`); when `true` they return an item only when every one of its blockers is `Done` (items with no blockers included).
`ListWorkItems` also accepts the polling arguments described under [Polling work items](#polling-work-items).
`SearchWorkItems` matches every whitespace-separated word of `query` case-insensitively against titles and descriptions, accepts optional `projectId` and `limit` (default 25, max 100), includes closed items, and returns only ids, project ids, titles, types, statuses, estimates (`estimate`, `null` when not set) and versions (`version`). It is backed by `GET /api/items/search`.

### Projects

A project says where its work is done. `list_projects(includeArchived?)` returns every project with its target, `create_project(name, key, description?, repository_url?, default_branch?, idempotency_key?)` creates one, and `update_project(id, name?, key?, description?, repository_url?, default_branch?, is_archived?, clear?)` changes one. All three return this shape (`list_projects` an array of it):

```json
{ "id": "0d6c…", "name": "App", "key": "APP", "description": "…", "isArchived": false, "createdAtUtc": "2026-10-12T12:00:00+00:00", "totalItems": 12, "openItems": 5, "repository_url": "https://github.com/acme/app.git", "default_branch": "main" }
```

`repository_url` and `default_branch` are always present and are `null` when not set. `description` is left out when the project has none. The answer of `create_project` also carries `created`.

- **`update_project` changes only what is passed.** An argument that is left out, or passed as `null`, keeps its value; a call with only `id` changes nothing and returns the project.
- **Clearing a field: name it in `clear`.** `clear` is a list of the names of the optional fields to empty: `update_project(id, clear: ["repository_url", "default_branch"])`. For a project it may name `description`, `repository_url` and `default_branch`. An empty or whitespace-only string is never a way to clear: it is refused `invalid_argument`. So is a name in `clear` that is not one of these (names are matched exactly; a `null` entry is not one of them), and a field that is both passed with a value and named in `clear`. This is the convention for emptying an optional field over MCP. (`update_work_item` keeps one exception, for its `description` and `labels`: see [Updating and moving work items](#updating-and-moving-work-items).)
- **`repository_url`** is an `http` or `https` URL without user name or password (`https://github.com/acme/app.git`), or an SSH URL: `ssh://git@github.com/acme/app.git` or the short form `git@github.com:acme/app.git`. An SSH URL may carry a user name, which is not a secret, and never a password: a user name with a `%` in it is refused, because git decodes an `ssh://` URL before use and `%3A` would hide one. An SSH URL whose path starts with `-` (`git@host:-x`, `ssh://git@host/-x`) is refused as well, since git or ssh would read it as an option. Any other form (a local path, `file://`, `git://`), a URL with whitespace or a control character, one longer than 2048 characters, and an `http`/`https` URL that fails the rules of a [link](#links) URL are refused `invalid_argument`. Credentials are refused in every form because the URL is returned to every `read` token. Whitespace around the URL is dropped; it is otherwise stored and returned as given.
- **`default_branch`** is a branch name of at most 255 characters that git accepts: no whitespace or control character, none of `~ ^ : ? * [ \`, no `..`, `@{` or `//`, not starting with `/` or `-`, not ending with `/` or `.`, no part between slashes starting with `.` or ending with `.lock` (`.hidden`, `a/.b`, `a.lock/b`, `main.lock`), and not `@` alone. Anything else is refused `invalid_argument`. Whether the branch exists in the repository is not checked.
- **Rename and key.** `name` (at most 160 characters) renames; several projects may share a name. `key` (at most 24 characters, stored in upper case) is unique: a key another project has is refused `conflict`, by `create_project` as well, and nothing is changed.
- **Archive.** `is_archived: true` archives a project and `is_archived: false` restores it, exactly as the checkbox in the web UI does. An archived project is left out of `list_projects` unless `includeArchived` is `true`; nothing else changes: `get_board` and the work item tools still answer for it, and its items, epics and target are kept.
- **`idempotency_key` makes `create_project` safe to repeat.** It follows the rules of a [comment](#comments) key: 1 to 128 printable ASCII characters without spaces, compared exactly, anything else refused `invalid_argument`. A key belongs to the calling identity (a project has no parent, so nothing else scopes it; callers without a token share one identity). When that identity already used the key, nothing is created, no board-change event is sent, and the answer is the project the key created, as it is now, with `created: false`; the other arguments are not compared, so a repeat with another name or key still returns the first project. Two calls with one key at the same moment create one project and both report its id. A keyed create that waits 10 s for other keyed creates is refused `busy`, with nothing created, and can be retried. Deleting the project frees its key. Without a key every call creates a project (`created: true`).
- An unknown `id` is refused `not_found` by `update_project`. An update that changes nothing sends no board-change event.
- `create_project` and `update_project` are write tools: a `read` token is refused `scope_denied` before the API is called. `list_projects` is a read tool and answers a call with the arguments it always had.

### Updating and moving work items

`update_work_item(itemId, title?, type?, status?, priority?, epicId?, description?, estimate?, labels?, label_list?, expected_version?, clear?)` changes a work item and `move_work_item(itemId, status, order?, expected_version?)` puts it in a column at a position. Both address the item by its id and return the work item. Every tool that returns a work item returns these fields with the ones it always had:

```json
{ "id": "e086…", "title": "Fix parser", "status": "Ready", "estimate": null, "labels": "api,two words", "label_list": ["api", "two words"], "version": 4 }
```

- **`update_work_item` changes only what is passed.** An argument that is left out, or passed as `null`, keeps its value: `update_work_item(itemId, status: "InProgress")` changes the status and nothing else. `title`, `type`, `status` and `priority` used to be required; a caller that still sends all four keeps working, and the optional fields it does not send keep their values as they always did. A call that passes no field to change (only `itemId`, or `itemId` with `expected_version` or an empty `clear`) is refused `invalid_argument` and changes nothing: it would otherwise advance the version and make another caller's guarded write fail. The API's `PATCH /api/items/{id}` refuses a body without a field the same way; the web UI's `PUT` is as it was. A new `status` puts the item at the end of that column; use `move_work_item` to choose the position.
- **Clearing a field: name it in `clear`**, the [convention of `update_project`](#projects). For a work item `clear` may name `description`, `estimate`, `labels` and `epicId`: `update_work_item(itemId, clear: ["estimate", "labels"])`. An empty or whitespace-only string and an empty `label_list` are refused `invalid_argument`, as are a name in `clear` that is not one of these four and a field that is both passed and named in `clear` (`label_list` together with `clear: ["labels"]` included).
- **The one exception: `description` and `labels` of `update_work_item` still take an empty string.** Before `clear` existed, `update_work_item(itemId, title, type, status, priority, description: "")` emptied the description and `labels: ""` removed the labels, and callers written then keep working: `description` passed as an empty or whitespace-only string leaves the item with the empty description `""` (as it always did; `clear: ["description"]` leaves it `null`), and `labels` passed as a string with no label in it (`""`, `" "`, `" , "`) leaves the item without labels. The other fields are untouched. This holds for these two arguments of `update_work_item` only: an empty `title`, an empty entry of `label_list`, and every empty string passed to `update_project` are refused `invalid_argument`, and so is `description: ""` or `labels: ""` together with the same name in `clear`. New callers should use `clear`.
- **`version` is the item's write counter.** It is `1` on a new item (and on every item that existed before this feature) and goes up by exactly one with every `update_work_item` and every `move_work_item` of the item that is not refused, with the same writes made from the web UI, and when archiving or deleting an epic takes the item out of it. It does **not** change when the item is claimed, renewed or released or its claim expires, when a comment, a link or a dependency is added or removed, or when another item is moved or reordered around it (the sibling's `order` changes; its `version` and `updatedAtUtc` do not). An update that passes the values the item already has is still an update and advances it. Those are separate records that an update cannot overwrite, so an orchestrator that holds a claim can renew it, comment and attach its pull request between reading an item and updating it, and its update still goes through. `version` is a number that only the database writes, as the stored value plus one inside the `UPDATE`: two writes never give an item the same version, which `updatedAtUtc` (taken from a clock before the commit) cannot promise.
- **`expected_version` makes an update or a move conditional.** Pass the `version` of the item as it was last read. The write is one `UPDATE … WHERE Id = … AND Version = …`, so the database decides: when the item still has that version the call succeeds and returns the new one; otherwise it is refused `conflict`, nothing is written (not a field, not `updatedAtUtc`, not the order of any sibling), and the message names the current version. Read the item again and decide whether the change still applies. Of several calls that pass the same `expected_version` at the same moment exactly one succeeds. Without `expected_version` an update or a move is unconditional, as before: the last writer wins.
- **Labels are a list.** `label_list` on a read is the item's labels in order; `labels` stays the comma-separated string it has always been, with the same labels, so existing callers and the web UI are unaffected. To write, pass either `label_list` (each label a non-empty string without a comma) or `labels` (comma-separated), not both; `create_work_item` and `create_epic_work_item` take `label_list` as well. The labels replace the ones the item has. They are trimmed, duplicates that differ only in case are dropped, and together, joined with commas, they may be at most 4000 characters (240 before); more is refused `invalid_argument`. Labels stored before this feature read back as the same labels.
- **Other rules.** `title` is at most 240 characters. `epicId` must be an active epic of the item's project, otherwise the call is refused `invalid_argument`; an update that does not name an epic is not refused because the item's epic has since been archived. An unknown `itemId` is refused `not_found`. A move writes the item first and renumbers the other items of the column afterwards, in a write of its own, so that two moves in one column cannot deadlock; if that renumbering fails, the move still stands and is answered with the moved item and its new version, the failure is logged by the API, and the next move in the column renumbers it. Both are write tools: a `read` token is refused `scope_denied` before the API is called.

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
- **`updatedAtUtc` is the content timestamp.** It changes when the item's fields change (including a move to another column or position, and being taken out of an epic that is archived or deleted) when a comment is added to it, and when a link is added to or removed from it. It does not change when a sibling is moved or reordered around it, when it is claimed, renewed or released, when its claim expires, or when a dependency is added, removed or finished. The stamp is taken before the change commits, so a change can become visible slightly after another one that carries a later stamp: when following changes with `updated_since`, poll with a small overlap (for example a few seconds before the latest `updatedAtUtc` seen) and de-duplicate by id plus `updatedAtUtc`.
- **Claim state is read from the clock on every call.** A claim that expires writes nothing, so `updated_since` cannot report it, and neither can it report an item that became unblocked. To find work that is ready to take, poll with `status`, `unblocked_only: true` and `claim_state: "unclaimed"` and no `updated_since`; use `updated_since` to follow content changes.
- A malformed `label`, `updated_since`, `claim_state` or `cursor`, and a `limit` outside 1 to 200, is refused `invalid_argument`. A `limit` that is not an integer (`"ten"`, `2.5`) never reaches the tool: the MCP server fails to bind the argument and answers with a plain MCP error without a code.

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

### Links

`add_work_item_link(id, kind, url)` attaches an external URL to work item `id`, `remove_work_item_link(id, link_id)` or `remove_work_item_link(id, kind, url)` takes one off, and `find_work_items_by_link(url)` returns the work items that carry a URL. Add and remove return work item `id`. Every work item read lists the item's links, oldest first, as `links`:

```json
"links": [{ "id": "6f0c…", "kind": "pr", "url": "https://github.com/acme/app/pull/42", "created_at_utc": "2026-10-12T12:00:00+00:00" }]
```

- `kind` is `pr`, `branch` or `other` (case is ignored); any other kind is refused `invalid_argument`.
- `url` must be an absolute `http` or `https` URL of at most 2048 characters, otherwise the add is refused `invalid_argument`. It is refused the same way when it holds whitespace or a control character, when it carries credentials (`https://user:password@host/…`; links are returned to every `read` token), or when its percent-encoded form is longer than 6144 characters: find and remove send the URL percent-encoded, so every ASCII URL of 2048 characters fits, while a non-ASCII character counts for 6 to 9 (about 680 characters such as `✓`). Whitespace around the URL is dropped; nothing else is normalised. URLs are compared exactly, character for character: `https://github.com/acme/app/pull/42` and `https://github.com/Acme/app/pull/42/` are different links, so pass a URL the same way every time.
- A link is identified by item, kind and URL. Adding a kind and URL the item already carries returns the item with the existing link and creates nothing, also when two identical adds arrive at once. The same URL may be attached to several items, and to one item under several kinds.
- `remove_work_item_link` takes either `link_id` (the `id` of an entry of `links`) or `kind` and `url` together; any other combination is refused `invalid_argument`, as is a `url` whose percent-encoded form is longer than 6144 characters. Removing a link the item does not carry, including a `link_id` that belongs to another item, succeeds and changes nothing.
- An unknown `id` is refused `not_found` by add and remove.
- `find_work_items_by_link(url)` returns full work items, oldest first, in any status (including `Closed`), project or epic, whatever the kind of the link; the result is `[]` when no item carries the URL. An empty `url`, or one whose percent-encoded form is longer than 6144 characters, is refused `invalid_argument`; any other string that is not an attached URL gives `[]`.
- A link that is added or removed stamps the item's `updatedAtUtc`, so `updated_since` reports it; an add or remove that changes nothing does not, and sends no board-change event either. Deleting a work item (or its project) deletes its links.
- Add and remove are write tools: a `read` token is refused `scope_denied` before the API is called. `find_work_items_by_link` is a read tool.

### Comments

`add_work_item_comment(itemId, body, author?, idempotency_key?)` adds a comment and `list_work_item_comments(id, limit?, cursor?)` reads a work item's comments. A comment is a record:

```json
{ "id": "57a8…", "workItemId": "e086…", "author": "factory", "author_id": "3cac…", "body": "Opened PR 42", "createdAtUtc": "2026-10-12T23:42:26.996571+00:00" }
```

Work item reads embed the same records in `comments`, oldest first. `id` is a GUID, like a work item id, and never changes.

- **The author is the token's identity.** A comment added with a personal access token carries that identity's name as `author` (the same name as `claimant`) and its id as `author_id`; the `author` argument is accepted and ignored, so no caller can write under another name. Match your own comments by `author_id`: `author` is the name at the time of writing. A comment written without a token (the web UI, or an MCP server that does not require tokens) keeps the `author` the writer gave, which is then required, and has no `author_id`; so does every comment older than this feature.
- **`add_work_item_comment` returns the work item**, as before, with all its comments, plus `comment_id` (the id of the comment this call is about) and `comment_created`.
- **`idempotency_key` makes the add safe to repeat.** It is 1 to 128 printable ASCII characters without spaces, compared exactly (`run-1` and `RUN-1` are two keys); anything else is refused `invalid_argument`. A key belongs to the calling identity and the work item: when that identity already used it on that item, nothing is added, `comment_id` is the earlier comment's id, `comment_created` is `false`, the item's `updatedAtUtc` does not move and no board-change event is sent. The body is not compared: a repeat with another body still returns the first comment unchanged. Two calls with one key at the same moment add one comment and both report its id. The same key used by another identity, or on another item, is another comment; callers without a token share one identity. Without a key every call adds a comment.
- **`list_work_item_comments` returns one page**, `{"items": [...], "next_cursor": "..."}`, oldest comment first. `limit` is 1 to 200 (50 when left out). Pass `next_cursor` back unchanged as `cursor` until a page comes without one. Every comment is returned exactly once, also when comments are added during the walk: a comment's `createdAtUtc` is always later than that of every comment written to the item before it (when clocks disagree it is the latest one plus a microsecond), so a new comment lands after the cursor, never behind it. The cursor is opaque, and it belongs to this tool: a `next_cursor` of `list_work_items` is refused here, and the other way round. A malformed `cursor`, and a `limit` outside 1 to 200, is refused `invalid_argument`. A `limit` that is not an integer never reaches the tool: the MCP server fails to bind the argument and answers with a plain MCP error without a code.
- An unknown work item is refused `not_found` by both tools.
- A comment that is added stamps the item's `updatedAtUtc` with the comment's `createdAtUtc`, so `updated_since` reports it.
- `add_work_item_comment` is a write tool: a `read` token is refused `scope_denied` before the API is called. `list_work_item_comments` is a read tool. `add_issue_comment(itemId, author, body)` is the older form of the add, without a key; it stamps the author the same way.

### Change feed

`list_changes(cursor?, project_id?, limit?, from_start?)` reads the persisted change feed: one entry for every change to a work item, in the order the changes were written. It is how a caller follows the board without re-reading it, and picks up where it stopped after a restart. It is backed by `GET /api/items/changes` and is unrelated to `GET /api/changes`, the in-memory event stream that only tells the web UI that something changed.

```json
{
  "items": [
    {
      "sequence": 4812,
      "item_id": "e086…",
      "project_id": "0b5c…",
      "kind": "item_moved",
      "actor": { "id": "3cac…", "name": "factory" },
      "changes": [
        { "field": "status", "old": "Backlog", "new": "InProgress" },
        { "field": "version", "old": 1, "new": 2 }
      ],
      "created_at_utc": "2026-10-13T01:41:52.674522+00:00"
    }
  ],
  "next_cursor": "aC40ODEy"
}
```

- **Start by taking a cursor.** Called without `cursor`, the tool returns no entries and the cursor of the newest entry in the feed: save it, make your writes, and read from it. `from_start: true` starts at the oldest entry instead (`cursor` and `from_start` together are refused `invalid_argument`).
- **`next_cursor` is always there**, also when `items` is empty: it is the position after the last entry returned, or the position that was asked for when there was none. A poller passes it to its next call whatever came back. Fewer entries than `limit` (1 to 200, 50 when left out) means the end of the feed was reached.
- **Nothing lands behind a cursor.** Every entry has a higher `sequence` than every entry committed before it, so a caller that has read up to a cursor gets each later entry exactly once, in write order, however many callers write at the same time. Reading again from the same cursor returns the same entries, also after the API restarts: entries are stored in the database, in the transaction of the write they describe, and are never changed. `sequence` may skip numbers; do not derive anything from the gaps.
- **`project_id`** keeps only the entries of work items of that project. A cursor is a position in the whole feed, not in a filtered one: the cursor a filtered call returned can be passed to an unfiltered call, and the other way round. The cursor is opaque and belongs to this tool; a cursor of `list_work_items` or `list_work_item_comments` is refused `invalid_argument`, as is a malformed one.
- **`kind`** is one of:

  | Kind | Written by | `changes` holds |
  | --- | --- | --- |
  | `item_created` | `create_work_item`, `create_epic_work_item` | every field the new item has a value for, with `old` null |
  | `item_updated` | `update_work_item`; archiving or deleting an epic, for each item it takes out of the epic | the fields whose value changed, and `version` |
  | `item_moved` | `move_work_item` | `status` and `order` when they changed, and `version` |
  | `item_deleted` | `delete_work_item`; `delete_project`, for each item of the project | every field the item had a value for, with `new` null |
  | `claimed` | `claim_work_item` | the claim fields that changed |
  | `claim_renewed` | `renew_work_item_claim` | `claim_expires_at_utc` |
  | `claim_released` | `release_work_item_claim` | the claim fields, with `new` null |
  | `comment_added` | `add_work_item_comment`, `add_issue_comment` | `comment`: `old` null, `new` `{id, author, body}` |
  | `link_added` | `add_work_item_link` | `link`: `old` null, `new` `{id, kind, url}` |
  | `link_removed` | `remove_work_item_link` | `link`: `old` `{id, kind, url}`, `new` null |
  | `dependency_added` | `add_work_item_dependency` | `blocked_by`: `old` null, `new` the id of the blocking item |
  | `dependency_removed` | `remove_work_item_dependency` | `blocked_by`: `old` the id of the blocking item, `new` null |

  The same writes made from the web UI are in the feed as well. Kinds are a contract: new ones may be added, none is renamed.
- **`changes`** lists `{field, old, new}` for each field the write changed, and only those; `old` and `new` are always present and are `null` for "no value". The item's own fields are `title`, `description`, `type`, `status`, `priority`, `order`, `estimate`, `labels` (the comma-separated string), `epic_id` and `version`; the claim fields are `claimant_id`, `claimant`, `claim_marker` and `claim_expires_at_utc`. `old` is the value the write replaced, read while the write held the item, not a value read earlier. For a claim that is the claim as it was stored: a claim that had expired without being released shows up as the `old` values of the claim that takes the item over.
- **`actor`** is who made the change, taken from the request and never from an argument: `{id, name}` of the identity behind the personal access token (the same id and name as `claimant_id` and `claimant`), or of the signed-in user for a change made in the web UI. It is left out when the write carried neither (an installation without authentication). The `author` of a comment written without a token is content of the comment, not the actor.
- **One write, one entry.** A dependency is one entry, on the blocked item (the `id` argument of the tool), and so in that item's project. Moving an item is one entry for that item; the items it displaces are renumbered without an entry, as without a new `version`. Archiving an epic is one `item_updated` per item it takes out and none for the epic.
- **A write that is refused, or that changes nothing, has no entry**: `conflict`, `already_claimed`, `cycle`, `not_found`, `invalid_argument`, a repeated `idempotency_key`, a link or dependency that was already there, a remove of one that was not.
- **Entries outlive their item.** Deleting a work item, or its project, leaves its entries in the feed, ending with `item_deleted`; `get_work_item` for `item_id` is then refused `not_found`.
- **Not in the feed:** changes to a project or an epic themselves (create, rename, archive, delete; only their effect on work items is), epic documents, and tokens. Writes made before this feature existed have no entries.
- **Retention:** entries are never pruned. The feed grows by one row per write; there is no tool or job that removes entries.
- **Cost:** to keep the order above, the API records entries one at a time: every write to a work item ends by taking one lock for the `INSERT` of its entry and its `COMMIT` (see [docs/mariadb-schema.md](docs/mariadb-schema.md)). Writes to different items, which used to commit side by side, now commit one after the other. A write that waited 10 s for that lock is refused `busy` and changes nothing.
- `list_changes` is a read tool: a `read` token can call it.

### Tool errors

A refused tool call returns a tool result with `isError: true`. Its `structuredContent` is `{"code": "...", "message": "..."}` and its single text block holds the same JSON. Branch on `code`; `message` is for people and may change.

| Code | Meaning |
| --- | --- |
| `scope_denied` | The token (or missing token) is not allowed to make this call. |
| `not_found` | The addressed entity does not exist. |
| `already_claimed` | The work item has a live claim held by another identity or marker. |
| `conflict` | The entity is no longer in the state the call depends on (for example: the work item no longer has the `expected_version` of an update or a move); nothing was changed. |
| `invalid_argument` | An argument is missing or out of range. |
| `cycle` | The dependency would make a work item block itself, directly or through other items; nothing was changed. |
| `busy` | The server could not take the call in time because of other callers (today: `add_work_item_dependency` waited 10 s for other dependency changes, `create_project` with an `idempotency_key` waited 10 s for other keyed creates, or any write to a work item waited 10 s to record its entry in the [change feed](#change-feed)); nothing was changed and the call can be retried. |

Scope refusals from every write tool, unknown ids from `GetWorkItem`, malformed arguments of `ListWorkItems`, and every refusal from `create_project`, `update_project`, `update_work_item`, `move_work_item`, `create_work_item`, `create_epic_work_item` and the claim, dependency, link and comment tools use this envelope. Other failures (for example an unreachable API) are reported as a plain MCP error without a code.

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
- `UpdateProject`
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
- `ListWorkItemComments`
- `ListChanges`
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
- `AddWorkItemLink`
- `RemoveWorkItemLink`
- `FindWorkItemsByLink`

## Production deployment

Production deployment is handled by GitHub Actions, GHCR, and Docker Compose. Caddy is managed separately on the host.

- Web UI/API: `https://kanban.trefry.net`
- MCP: `https://kanban-mcp.trefry.net/mcp`
- Deployment guide: [docs/deployment.md](docs/deployment.md)
- MariaDB schema: [docs/mariadb-schema.md](docs/mariadb-schema.md)
- SQLite to MariaDB data migration: [docs/sqlite-to-mariadb.md](docs/sqlite-to-mariadb.md)

Production secrets such as `ConnectionStrings__Kanban`, `Auth__ClientSecret`, `PersonalAccessTokens__EncryptionKey`, and `InternalApi__SharedSecret` are supplied through GitHub-managed Actions secrets and written to `/opt/kanban-board/.env.release` during deployment. Local `.env` files are ignored and are for local development only.
