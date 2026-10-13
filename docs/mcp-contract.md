# Kanban MCP contract

This is the contract of the Kanban MCP server: every tool, every argument, every shape it returns, every error code, and the conventions they share. A caller such as the Dark Factory adapter can be built against this file alone.

The file cannot drift from the server. The [tool reference](#tool-reference) is written from the server's own `tools/list`, and a unit test (`KanbanBoard.Tests/McpContractDocTests.cs`) fails when it differs from it by a character. The same tests check each [shape](#shapes) against the type the server serialises (every field, its type, whether it is always there), the [error codes](#errors), the [change kinds](#change-kinds), the enumerations and the [limits](#limits). The [README](../README.md#mcp-tools) explains the same tools at more length, with the reasons.

- [Calling the server](#calling-the-server)
- [Tool reference](#tool-reference)
- [Shapes](#shapes)
- [Errors](#errors)
- [Conventions](#conventions)
- [Known limitations](#known-limitations)

## Calling the server

- **Transport.** MCP over streamable HTTP at `/mcp` (production: `https://kanban-mcp.trefry.net/mcp`).
- **Authentication.** Every request carries `Authorization: Bearer <personal access token>`. Without a token, or with one that is unknown, revoked or expired, the HTTP request is answered `401` and no tool runs.
- **Scopes.** A token is `full` or `read`. A `read` token can call every read tool. Every write tool refuses it with `scope_denied`, before the arguments are looked at and before the API is called: the answer is `scope_denied` also when an argument is missing, misspelt or of the wrong type.
- **Identity.** The identity of a call is the owner of its token and nothing else. No argument names a claimant, an author or an actor (the `author` argument of the comment tools is ignored when the call carries a token). A program uses a *service identity* (`create-service-identity`, see [deployment](deployment.md#factory-service-identity)), which has a name and an id like a person's.
- **Results.** A call that succeeds returns a tool result whose single text block is one JSON document: the shape named under each tool below. A call that is refused returns a tool result with `isError: true` and the [error envelope](#errors).

## Tool reference

Read tools change nothing. Write tools create, change or delete data and need a `full` token.

In the tables below, *Required* `no` means the argument can be left out. Passing `null` is the same as leaving it out. *Default* is what the tool uses then; an empty cell means "not set". Every GUID is a string such as `e0865c06-7b9a-4f0f-9a1e-3a8f3c0f2d11`.

<!-- BEGIN GENERATED: tool reference (from tools/list; see KanbanBoard.Tests/McpContractDocTests.cs) -->

### All tools

The server lists 37 tools: 14 read tools and 23 write tools.

| Tool | Access | Purpose |
| --- | --- | --- |
| [`add_issue_comment`](#add_issue_comment) | write | Add a comment to an issue. |
| [`add_work_item_comment`](#add_work_item_comment) | write | Add a comment to any work item. |
| [`add_work_item_dependency`](#add_work_item_dependency) | write | Record that work item id is blocked by work item blocked_by_id. |
| [`add_work_item_link`](#add_work_item_link) | write | Attach an external URL, such as a pull request or a branch, to work item id. |
| [`claim_work_item`](#claim_work_item) | write | Claim a work item for the calling token's identity until ttl_seconds from now. |
| [`create_epic`](#create_epic) | write | Create an epic in a project. |
| [`create_epic_document`](#create_epic_document) | write | Create a document for an epic. |
| [`create_epic_work_item`](#create_epic_work_item) | write | Create a story, issue, or task directly in an epic backlog. |
| [`create_project`](#create_project) | write | Create a new project, optionally with the repository and branch its work is done in. |
| [`create_work_item`](#create_work_item) | write | Create a story, issue, or task in a project backlog. |
| [`delete_epic`](#delete_epic) | write | Delete an epic and its documents. |
| [`delete_project`](#delete_project) | write | Delete a project with its epics, documents and work items. |
| [`delete_work_item`](#delete_work_item) | write | Delete a work item with its comments, links and dependencies. |
| [`find_work_items_by_link`](#find_work_items_by_link) | read | Find the work items that carry an external link with exactly this URL, under any kind and in any status, project or epic, oldest item first. |
| [`get_board`](#get_board) | read | Fetch a project board, optionally filtered to a single status. |
| [`get_epic`](#get_epic) | read | Fetch a single epic. |
| [`get_epic_document`](#get_epic_document) | read | Fetch a single epic document. |
| [`get_issues`](#get_issues) | read | Fetch issue work items, optionally filtered by project and status. |
| [`get_work_item`](#get_work_item) | read | Fetch a single work item by id, including its description, status, comments, its blockers and dependents with their current status, and its external links. |
| [`import_epic_plan`](#import_epic_plan) | write | Turn a plan into a board in one call. |
| [`list_changes`](#list_changes) | read | Read the change feed. |
| [`list_epic_documents`](#list_epic_documents) | read | List documents that belong to an epic. |
| [`list_epic_work_items`](#list_epic_work_items) | read | List work items that belong to a specific epic backlog. |
| [`list_epics`](#list_epics) | read | List epics that belong to a project. |
| [`list_projects`](#list_projects) | read | List all projects in the kanban board. |
| [`list_work_item_comments`](#list_work_item_comments) | read | List the comments of work item id, oldest first, one page at a time. |
| [`list_work_items`](#list_work_items) | read | List work items, optionally filtered by project, epic, type, status, label, claim state and time of last change. |
| [`move_work_item`](#move_work_item) | write | Move a work item to a column and a position in it. |
| [`release_work_item_claim`](#release_work_item_claim) | write | Release the caller's claim on a work item so others can claim it. |
| [`remove_work_item_dependency`](#remove_work_item_dependency) | write | Remove the dependency that makes work item id blocked by work item blocked_by_id. |
| [`remove_work_item_link`](#remove_work_item_link) | write | Remove one link from work item id. |
| [`renew_work_item_claim`](#renew_work_item_claim) | write | Extend a live claim the caller holds under this marker to ttl_seconds from now. |
| [`search_work_items`](#search_work_items) | read | Search work items whose title or description contains every word in the query (case-insensitive). |
| [`update_epic`](#update_epic) | write | Replace an epic. |
| [`update_epic_document`](#update_epic_document) | write | Replace an epic document. |
| [`update_project`](#update_project) | write | Change a project. |
| [`update_work_item`](#update_work_item) | write | Change a work item. |

### Arguments of each tool

#### `add_issue_comment`

**Write tool.** Add a comment to an issue. Same as add_work_item_comment, with author required: with a personal access token the author is the token's identity and the author argument is ignored. Pass idempotency_key to make the call safe to repeat: add_issue_comment and add_work_item_comment share their keys, so when the same identity already used the key on this item with either tool, nothing is added and the earlier comment is reported. Returns the work item with all its comments, plus comment_id and comment_created (false for a repeat).

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `itemId` | string (GUID) | yes |  |  | Issue work item id |
| `author` | string | yes |  |  | Comment author, used only when the call carries no personal access token |
| `body` | string | yes |  |  | Comment text |
| `idempotency_key` | string | no |  |  | Caller-chosen key for this comment, such as a run id plus a step name: 1 to 128 printable ASCII characters without spaces, compared exactly; anything else is refused with code invalid_argument. It belongs to the calling identity and this work item. |

#### `add_work_item_comment`

**Write tool.** Add a comment to any work item. The author of a comment added with a personal access token is the token's identity: the comment carries that identity's name as author and its id as author_id, and the author argument is ignored. Without a token, author is required. Pass idempotency_key to make the call safe to repeat: when the same identity already used the key on this item, nothing is added, the item's updatedAtUtc does not change, and the earlier comment is reported whatever its body. Returns the work item with all its comments, plus comment_id (the id of the comment that was added, or of the earlier one) and comment_created (false for a repeat). An unknown itemId is refused with code not_found, a malformed argument with invalid_argument.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `itemId` | string (GUID) | yes |  |  | Work item id |
| `body` | string | yes |  |  | Comment text |
| `author` | string | no |  |  | Comment author, used only when the call carries no personal access token. With a token the author is the token's identity and this is ignored. |
| `idempotency_key` | string | no |  |  | Caller-chosen key for this comment, such as a run id plus a step name: 1 to 128 printable ASCII characters without spaces, compared exactly. It belongs to the calling identity and this work item. |

#### `add_work_item_dependency`

**Write tool.** Record that work item id is blocked by work item blocked_by_id. Adding a dependency that already exists changes nothing and succeeds. Refused with code cycle, changing nothing, when it would make an item block itself directly or through other items. Refused with code busy, changing nothing, when other dependency changes kept it waiting 10 seconds; retry. Returns work item id with its blockers and dependents.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `id` | string (GUID) | yes |  |  | Id of the work item that is blocked |
| `blocked_by_id` | string (GUID) | yes |  |  | Id of the work item that blocks it |

#### `add_work_item_link`

**Write tool.** Attach an external URL, such as a pull request or a branch, to work item id. kind is pr, branch or other; any other kind is refused with code invalid_argument, as is a url that is not an absolute http or https URL of at most 2048 characters, that holds whitespace, a control character or credentials (user:password@), or whose percent-encoded form is longer than 6144 characters (every ASCII URL of 2048 characters fits; a non-ASCII character counts for 6 to 9). Adding a kind and url the item already carries changes nothing and succeeds. The same url may be attached to several items. Returns work item id; its links field lists each link as {id, kind, url, created_at_utc}.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `id` | string (GUID) | yes |  |  | Work item id |
| `kind` | string | yes |  |  | Kind of link: pr, branch or other |
| `url` | string | yes |  |  | Absolute http or https URL, at most 2048 characters, without whitespace or credentials. URLs are compared exactly, character for character. |

#### `claim_work_item`

**Write tool.** Claim a work item for the calling token's identity until ttl_seconds from now. Succeeds when the item is unclaimed, its claim has expired, or the caller already holds it under the same marker (which resets the expiry). Refused with code already_claimed when another identity or another marker holds a live claim.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `id` | string (GUID) | yes |  |  | Work item id |
| `marker` | string | yes |  |  | Caller-chosen id for this claim, such as a run id (max 200 characters, compared exactly). Renew and release must pass the same marker. |
| `ttl_seconds` | integer | yes |  |  | Seconds until the claim lapses unless renewed (1 to 86400) |

#### `create_epic`

**Write tool.** Create an epic in a project. Pass idempotency_key to make the call safe to repeat: when the same identity already used the key for an epic of this project, nothing is created and the epic it created is returned as it is now, whatever the other arguments. The answer carries created (false for a repeat). An unknown projectId is refused with code not_found, a name that is empty or longer than 160 characters with invalid_argument.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `projectId` | string (GUID) | yes |  |  | Project id |
| `name` | string | yes |  |  | Epic name, at most 160 characters |
| `description` | string | no |  |  | Optional description |
| `isArchived` | boolean | no | `false` |  | Whether the epic starts archived |
| `idempotency_key` | string | no |  |  | Caller-chosen key for this epic: 1 to 128 printable ASCII characters without spaces, compared exactly; anything else is refused with code invalid_argument. It belongs to the calling identity and this project, and is separate from the keys of the project's work items. |

#### `create_epic_document`

**Write tool.** Create a document for an epic. Pass idempotency_key to make the call safe to repeat: when the same identity already used the key on this epic, nothing is created and the document it created is returned as it is now, whatever the other arguments. The answer carries created (false for a repeat). An unknown epicId is refused with code not_found, an empty body or a title that is empty or longer than 200 characters with invalid_argument.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `epicId` | string (GUID) | yes |  |  | Epic id |
| `title` | string | yes |  |  | Document title, at most 200 characters |
| `body` | string | yes |  |  | Document body |
| `idempotency_key` | string | no |  |  | Caller-chosen key for this document: 1 to 128 printable ASCII characters without spaces, compared exactly; anything else is refused with code invalid_argument. It belongs to the calling identity and this epic. |

#### `create_epic_work_item`

**Write tool.** Create a story, issue, or task directly in an epic backlog. Pass idempotency_key to make the call safe to repeat: when the same identity already used the key for a work item of the project, nothing is created, nothing is added to the change feed and the item it created is returned as it is now, whatever the other arguments. The answer carries created (false for a repeat). The key is looked up in the epic's project: create_work_item and create_epic_work_item share their keys.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `epicId` | string (GUID) | yes |  |  | Epic id |
| `title` | string | yes |  |  | Title |
| `type` | string | no | `"Story"` | `Story`, `Issue`, `Task` | Type: Story, Issue, or Task |
| `status` | string | no | `"Backlog"` | `Backlog`, `Ready`, `InProgress`, `Blocked`, `Done`, `Closed` | Status |
| `priority` | string | no | `"Medium"` | `Low`, `Medium`, `High`, `Critical` | Priority |
| `description` | string | no |  |  | Optional description |
| `estimate` | integer | no |  |  | Optional estimate |
| `labels` | string | no |  |  | Optional comma-separated labels |
| `label_list` | array of string | no |  |  | Labels as a list, in place of labels (passing both is refused with code invalid_argument). Each label is a non-empty string without a comma; all labels together, joined with commas, are at most 4000 characters. Duplicates, ignoring case, are dropped. |
| `idempotency_key` | string | no |  |  | Caller-chosen key for this work item: 1 to 128 printable ASCII characters without spaces, compared exactly; anything else is refused with code invalid_argument. It belongs to the calling identity and the project, and is separate from the keys of the project's epics. |

#### `create_project`

**Write tool.** Create a new project, optionally with the repository and branch its work is done in. Pass idempotency_key to make the call safe to repeat: when the same identity already used the key, nothing is created and the project it created is returned as it is now, whatever the other arguments. Returns the project with created (false for a repeat). A key another project has is refused with code conflict, a malformed argument with invalid_argument, and a keyed create that waited 10 seconds for other keyed creates with busy; retry.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `name` | string | yes |  |  | Project name, at most 160 characters |
| `key` | string | yes |  |  | Short key, at most 24 characters, stored in upper case. No two projects share a key. |
| `description` | string | no |  |  | Optional description |
| `repository_url` | string | no |  |  | Repository the project's work is done in: an http or https URL without user name or password (https://github.com/owner/repo.git), or an SSH URL without password (ssh://git@github.com/owner/repo.git or git@github.com:owner/repo.git). At most 2048 characters, stored as given. |
| `default_branch` | string | no |  |  | Branch of the repository that work starts from, such as main: a branch name git accepts, at most 255 characters |
| `idempotency_key` | string | no |  |  | Caller-chosen key for this project, such as a plan id: 1 to 128 printable ASCII characters without spaces, compared exactly. It belongs to the calling identity. |

#### `create_work_item`

**Write tool.** Create a story, issue, or task in a project backlog. Pass epicId to attach it to a specific epic backlog. Pass idempotency_key to make the call safe to repeat: when the same identity already used the key for a work item of the project, nothing is created, nothing is added to the change feed and the item it created is returned as it is now, whatever the other arguments. The answer carries created (false for a repeat). An unknown projectId is refused with code not_found, a malformed argument or an epic that is archived or of another project with invalid_argument.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `projectId` | string (GUID) | yes |  |  | Project id |
| `title` | string | yes |  |  | Title |
| `type` | string | yes |  | `Story`, `Issue`, `Task` | Type: Story, Issue, or Task |
| `status` | string | no | `"Backlog"` | `Backlog`, `Ready`, `InProgress`, `Blocked`, `Done`, `Closed` | Status |
| `priority` | string | no | `"Medium"` | `Low`, `Medium`, `High`, `Critical` | Priority |
| `epicId` | string (GUID) | no |  |  | Optional epic id |
| `description` | string | no |  |  | Optional description |
| `estimate` | integer | no |  |  | Optional estimate |
| `labels` | string | no |  |  | Optional comma-separated labels |
| `label_list` | array of string | no |  |  | Labels as a list, in place of labels (passing both is refused with code invalid_argument). Each label is a non-empty string without a comma; all labels together, joined with commas, are at most 4000 characters. Duplicates, ignoring case, are dropped. |
| `idempotency_key` | string | no |  |  | Caller-chosen key for this work item: 1 to 128 printable ASCII characters without spaces, compared exactly; anything else is refused with code invalid_argument. It belongs to the calling identity and the project, and is separate from the keys of the project's epics. |

#### `delete_epic`

**Write tool.** Delete an epic and its documents. Its work items are not deleted: they are taken out of the epic and stay in the project. An unknown epicId is refused with code not_found.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `epicId` | string (GUID) | yes |  |  | Epic id |

#### `delete_project`

**Write tool.** Delete a project with its epics, documents and work items. An unknown projectId is refused with code not_found.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `projectId` | string (GUID) | yes |  |  | Project id |

#### `delete_work_item`

**Write tool.** Delete a work item with its comments, links and dependencies. An unknown itemId is refused with code not_found.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `itemId` | string (GUID) | yes |  |  | Work item id |

#### `find_work_items_by_link`

**Read tool.** Find the work items that carry an external link with exactly this URL, under any kind and in any status, project or epic, oldest item first. Returns full work items, and an empty array when no item carries the URL. An empty url, or one whose percent-encoded form is longer than 6144 characters, is refused with code invalid_argument.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `url` | string | yes |  |  | The URL, exactly as it was added: URLs are compared character for character |

#### `get_board`

**Read tool.** Fetch a project board, optionally filtered to a single status. Closed items stay persisted but are hidden unless status=Closed is requested.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `projectId` | string (GUID) | yes |  |  | Project id |
| `status` | string | no |  | `Backlog`, `Ready`, `InProgress`, `Blocked`, `Done`, `Closed` | Optional status filter |
| `includeArchivedEpics` | boolean | no | `false` |  | Whether to include items that belong to archived epics |

#### `get_epic`

**Read tool.** Fetch a single epic.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `epicId` | string (GUID) | yes |  |  | Epic id |

#### `get_epic_document`

**Read tool.** Fetch a single epic document.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `documentId` | string (GUID) | yes |  |  | Epic document id |

#### `get_issues`

**Read tool.** Fetch issue work items, optionally filtered by project and status.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `projectId` | string (GUID) | no |  |  | Optional project id |
| `status` | string | no |  | `Backlog`, `Ready`, `InProgress`, `Blocked`, `Done`, `Closed` | Optional status filter |
| `unblocked_only` | boolean | no | `false` |  | When true, return only items that are not waiting on a blocker: every item they are blocked by is Done (items with no blockers included) |

#### `get_work_item`

**Read tool.** Fetch a single work item by id, including its description, status, comments, its blockers and dependents with their current status, and its external links. estimate is null when not set; label_list is the labels as a list (labels is the same labels as one comma-separated string); version counts the updates and moves of the item, and can be passed to update_work_item and move_work_item as expected_version.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `itemId` | string (GUID) | yes |  |  | Work item id |

#### `import_epic_plan`

**Write tool.** Turn a plan into a board in one call: creates one epic in the project, the plan's documents as documents of that epic, its items as work items of that epic, and the dependencies its items name with blocked_by, all in one transaction. Either everything is created or nothing is: a plan with a malformed item, a ref used twice, a blocked_by that names no item of the plan, or a dependency cycle is refused and leaves no epic, document, item or dependency behind. Only the structure of the plan is checked. The idempotency_key is required and makes the call safe to repeat: when the same identity already used the key in this project, nothing is created and the answer is the one the first import gave, with created false, whatever the plan of the repeat says and whatever has happened to the objects since. The key lasts as long as the epic: delete_epic frees it and leaves the items of the import in the project without an epic, so a repeat after that creates a second epic and a second set of items, and a repeat that meets the delete while it runs is refused with code conflict, creating nothing. Returns {created, project_id, epic_id, document_ids, item_ids}: document_ids in the order of the plan, item_ids an object that maps each ref of the plan to the id of the work item created for it. Items are put at the end of their columns in the order of the plan, at version 1. The change feed gets one item_created entry per item and then one dependency_added entry per dependency, with the calling identity as actor. A plan holds at most 200 items, 20 documents and 500 dependencies and is at most 2 MB (2097152 bytes) of JSON. Refused with code not_found for an unknown project_id, cycle for a dependency cycle (an item blocked by itself included), invalid_argument for every other fault of the plan or of the key (an argument the tool does not have included), conflict when the epic of the key was deleted while the call ran, and busy, with nothing created, when other writers kept it waiting 10 seconds; retry with the same key.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `project_id` | string (GUID) | yes |  |  | Id of the project the epic is created in |
| `epic` | object {name: string, description?: string} | yes |  |  | The epic to create: {name, description?}. name is at most 160 characters. |
| `items` | array of object {ref: string, title: string, type?: string, status?: string, priority?: string, description?: string, estimate?: integer, labels?: string, label_list?: array of string, blocked_by?: array of string} | yes |  |  | The work items to create, 1 to 200: {ref, title, type?, status?, priority?, description?, estimate?, labels?, label_list?, blocked_by?}. ref is the caller's own name for the item, 1 to 64 characters, unique within the plan and compared exactly; it is not stored on the item. title is at most 240 characters. type is Story, Issue or Task (Story when left out), status is Backlog, Ready, InProgress, Blocked, Done or Closed (Backlog when left out), priority is Low, Medium, High or Critical (Medium when left out). labels is a comma-separated string and label_list the same as a list; pass at most one of them. blocked_by lists the refs of the items of this plan that block the item. |
| `idempotency_key` | string | yes |  |  | Caller-chosen key for this import, such as a plan id: 1 to 128 printable ASCII characters without spaces, compared exactly; anything else is refused with code invalid_argument. It belongs to the calling identity and this project. |
| `documents` | array of object {title: string, body: string} | no |  |  | Documents to create on the epic, at most 20: {title, body}. title is at most 200 characters. |

#### `list_changes`

**Read tool.** Read the change feed: every change to a work item, oldest first, as an object {"items": [...], "next_cursor": "..."}. next_cursor is always set, also when items is empty: pass it as cursor to the next call to get the entries written since, each exactly once and in the order they were written. Called without cursor it returns no entries and the cursor of the newest entry, which is where to start following from; from_start true starts at the oldest entry instead. Reading again from the same cursor returns the same entries; fewer entries than limit means the end of the feed was reached. Each entry is {sequence, item_id, project_id, kind, actor, changes, created_at_utc}. kind is one of item_created, item_updated, item_moved, item_deleted, claimed, claim_renewed, claim_released, comment_added, link_added, link_removed, dependency_added, dependency_removed. actor is {id, name} of the identity whose token or browser session made the change, and is left out when the write carried neither. changes lists {field, old, new} for each field the write changed: the item's own fields (title, description, type, status, priority, order, estimate, labels, epic_id, version), the stored claim (claimant_id, claimant, claim_marker, claim_expires_at_utc), comment ({id, author, body}), link ({id, kind, url}), or blocked_by (the id of the blocking item; the entry is on the blocked item). Deleting an item, or its project, also writes one dependency_removed on every item left that it blocked. A text longer than 16384 characters (a description, a comment body) is stored cut to its first 16384 characters, and that change then carries old_truncated true or new_truncated true (left out otherwise): read the item for the whole text. Entries stay after their item is deleted. A write that was refused or changed nothing has no entry, and neither has a change to a project or an epic itself. A project_id no project has is not refused: it returns no entries and the cursor that was passed. A malformed cursor, a cursor of another tool, a cursor ahead of the newest entry of the feed (the database was restored to an earlier state; start again without cursor), cursor together with from_start, or a limit outside 1 to 200 is refused with code invalid_argument.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `cursor` | string | no |  |  | next_cursor of an earlier call, passed back unchanged |
| `project_id` | string (GUID) | no |  |  | Only entries of work items of this project. The cursor is a position in the whole feed, so one cursor can be used with any project_id or with none. |
| `limit` | integer | no |  |  | Most entries to return, 1 to 200. 50 when not passed. |
| `from_start` | boolean | no | `false` |  | true reads from the oldest entry of the feed. Cannot be combined with cursor. |

#### `list_epic_documents`

**Read tool.** List documents that belong to an epic.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `epicId` | string (GUID) | yes |  |  | Epic id |

#### `list_epic_work_items`

**Read tool.** List work items that belong to a specific epic backlog.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `epicId` | string (GUID) | yes |  |  | Epic id |
| `type` | string | no |  | `Story`, `Issue`, `Task` | Optional work item type: Story, Issue, or Task |
| `status` | string | no |  | `Backlog`, `Ready`, `InProgress`, `Blocked`, `Done`, `Closed` | Optional status filter |
| `includeArchivedEpics` | boolean | no | `false` |  | Whether to include items when the epic is archived |
| `unblocked_only` | boolean | no | `false` |  | When true, return only items that are not waiting on a blocker: every item they are blocked by is Done (items with no blockers included) |

#### `list_epics`

**Read tool.** List epics that belong to a project.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `projectId` | string (GUID) | yes |  |  | Project id |
| `includeArchived` | boolean | no | `false` |  | Whether to include archived epics |

#### `list_projects`

**Read tool.** List all projects in the kanban board. Each project carries repository_url and default_branch, the repository and branch its work is done in; both are null when not set.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `includeArchived` | boolean | no | `false` |  | Whether to include archived projects |

#### `list_work_item_comments`

**Read tool.** List the comments of work item id, oldest first, one page at a time: an object {"items": [...], "next_cursor": "..."}, where next_cursor is left out on the last page. Each comment is {id, workItemId, author, author_id, body, createdAtUtc}: author_id is the id of the identity whose token wrote the comment and is left out for a comment written without a token; author is that identity's name when the comment was written, or the name the writer gave. An unknown id is refused with code not_found, a limit outside 1 to 200 or a malformed cursor with invalid_argument.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `id` | string (GUID) | yes |  |  | Work item id |
| `limit` | integer | no |  |  | Page size, 1 to 200. 50 when not passed. |
| `cursor` | string | no |  |  | next_cursor of the previous page, passed back unchanged. Each comment is returned exactly once across the pages, also when comments are added in between: a new comment always comes after every earlier one. |

#### `list_work_items`

**Read tool.** List work items, optionally filtered by project, epic, type, status, label, claim state and time of last change. Closed items stay persisted but are hidden unless status=Closed is requested. Each item lists its blockers and dependents with their current status, and its external links, and carries estimate (null when not set), label_list (its labels as a list) and version (see update_work_item). Without limit and cursor the result is an array of every matching item, in board order. With limit or cursor it is one page, oldest item first: an object {"items": [...], "next_cursor": "..."}, where next_cursor is left out on the last page. A malformed argument is refused with code invalid_argument.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `projectId` | string (GUID) | no |  |  | Optional project id |
| `epicId` | string (GUID) | no |  |  | Optional epic id |
| `type` | string | no |  | `Story`, `Issue`, `Task` | Optional work item type: Story, Issue, or Task |
| `status` | string | no |  | `Backlog`, `Ready`, `InProgress`, `Blocked`, `Done`, `Closed` | Optional status filter |
| `includeArchivedEpics` | boolean | no | `false` |  | Whether to include items that belong to archived epics |
| `unblocked_only` | boolean | no | `false` |  | When true, return only items that are not waiting on a blocker: every item they are blocked by is Done (items with no blockers included) |
| `label` | string | no |  |  | Return only items that carry this label. The whole label must match, ignoring case: 'api' does not match an item labelled 'rapid'. |
| `updated_since` | string | no |  |  | ISO 8601 timestamp, for example 2026-10-12T12:00:00Z. Return only items whose updatedAtUtc is at or after it. updatedAtUtc changes when the item's fields change, when it is moved, when a comment is added to it, or when a link is added to or removed from it; it does not change when the item is claimed, renewed or released, when a claim expires, or when a dependency or another item changes. |
| `claim_state` | string | no |  |  | Claim state at the time of the call: 'unclaimed' (no live claim, including a claim that has expired), 'claimed' (a live claim), or 'expired' (a claim that expired and was not released or replaced) |
| `limit` | integer | no |  |  | Page size, 1 to 200. Passing limit or cursor makes the result a page object instead of an array. |
| `cursor` | string | no |  |  | next_cursor of the previous page, passed back unchanged together with the same filters. Each matching item is returned exactly once across the pages, also when items are moved or edited in between. Page size is 50 when limit is not passed. |
| `include_comments` | boolean | no | `true` |  | When false, every item is returned with an empty comments list and no comment is read |

#### `move_work_item`

**Write tool.** Move a work item to a column and a position in it; the items it displaces are renumbered in the same write and are otherwise unchanged. A claim on the item does not stop a move by another caller: claims are advisory. Pass expected_version to refuse the move with code conflict, changing nothing, when the item was updated or moved after it was read. Returns the work item with its new version. An unknown itemId is refused with code not_found.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `itemId` | string (GUID) | yes |  |  | Work item id |
| `status` | string | yes |  | `Backlog`, `Ready`, `InProgress`, `Blocked`, `Done`, `Closed` | Target status |
| `order` | integer | no | `0` |  | Zero-based order in the target column |
| `expected_version` | integer | no |  |  | The version field of the item as it was last read. When passed, the call is refused with code conflict, changing nothing, unless the item still has that version. |

#### `release_work_item_claim`

**Write tool.** Release the caller's claim on a work item so others can claim it. Refused with code conflict when the caller holds no claim under this marker, and already_claimed when someone else holds the item.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `id` | string (GUID) | yes |  |  | Work item id |
| `marker` | string | yes |  |  | Marker the claim was made with |

#### `remove_work_item_dependency`

**Write tool.** Remove the dependency that makes work item id blocked by work item blocked_by_id. Removing a dependency that does not exist changes nothing and succeeds. Returns work item id with its blockers and dependents.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `id` | string (GUID) | yes |  |  | Id of the work item that is blocked |
| `blocked_by_id` | string (GUID) | yes |  |  | Id of the work item that blocks it |

#### `remove_work_item_link`

**Write tool.** Remove one link from work item id. Address the link either by link_id (the id of an entry of the item's links field) or by kind and url together; any other combination is refused with code invalid_argument, as is a url whose percent-encoded form is longer than 6144 characters. Removing a link the item does not carry changes nothing and succeeds. Returns work item id with its remaining links.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `id` | string (GUID) | yes |  |  | Work item id |
| `link_id` | string (GUID) | no |  |  | Id of the link to remove. Leave out when passing kind and url. |
| `kind` | string | no |  |  | Kind of the link to remove: pr, branch or other. Pass together with url. |
| `url` | string | no |  |  | URL of the link to remove, exactly as it was added. Pass together with kind. |

#### `renew_work_item_claim`

**Write tool.** Extend a live claim the caller holds under this marker to ttl_seconds from now. Refused with code conflict when the claim has lapsed or was released, and already_claimed when someone else now holds the item.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `id` | string (GUID) | yes |  |  | Work item id |
| `marker` | string | yes |  |  | Marker the claim was made with |
| `ttl_seconds` | integer | yes |  |  | Seconds until the claim lapses unless renewed again (1 to 86400) |

#### `search_work_items`

**Read tool.** Search work items whose title or description contains every word in the query (case-insensitive). Returns ids, project ids, titles, types, statuses, estimates (null when not set) and versions only (no bodies), including closed items, capped at a page size. Use get_work_item for full details.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `query` | string | yes |  |  | Words to match in work item titles and descriptions |
| `projectId` | string (GUID) | no |  |  | Optional project id to limit the search to |
| `limit` | integer | no |  |  | Optional maximum number of results (default 25, max 100) |

#### `update_epic`

**Write tool.** Replace an epic: name, description and isArchived are all written, with the values of this call. An argument that is left out is not kept: without description the epic's description is emptied, and without isArchived an archived epic is restored (isArchived false). To change one of them, read the epic with get_epic and pass all three. The call is unconditional: the last writer wins. isArchived true takes the epic's unfinished work items out of the epic. Returns the epic. An unknown epicId is refused with code not_found, an empty name with invalid_argument.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `epicId` | string (GUID) | yes |  |  | Epic id |
| `name` | string | yes |  |  | Epic name |
| `description` | string | no |  |  | The epic's description. Left out, the description is emptied. |
| `isArchived` | boolean | no | `false` |  | Whether the epic is archived. Left out, the epic is not archived, also when it was. |

#### `update_epic_document`

**Write tool.** Replace an epic document: title and body are both written, with the values of this call; the whole body is replaced, not appended to. The call is unconditional: the last writer wins. Returns the document. An unknown documentId is refused with code not_found, an empty title or body with invalid_argument.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `documentId` | string (GUID) | yes |  |  | Epic document id |
| `title` | string | yes |  |  | Document title |
| `body` | string | yes |  |  | The whole new body |

#### `update_project`

**Write tool.** Change a project. Only the arguments that are passed are changed: an argument that is left out, or passed as null, keeps its value. To empty an optional field, name it in clear (description, repository_url, default_branch); an empty string is refused with code invalid_argument, as is a field that is both passed and named in clear. Renames with name, archives with is_archived true and restores with is_archived false; an archived project is left out of list_projects unless includeArchived is true. Returns the project. An unknown id is refused with code not_found, a key another project has with conflict.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `id` | string (GUID) | yes |  |  | Project id |
| `name` | string | no |  |  | New name, at most 160 characters. Several projects may share a name. |
| `key` | string | no |  |  | New key, at most 24 characters, stored in upper case. No two projects share a key. |
| `description` | string | no |  |  | New description |
| `repository_url` | string | no |  |  | Repository the project's work is done in: an http or https URL without user name or password (https://github.com/owner/repo.git), or an SSH URL without password (ssh://git@github.com/owner/repo.git or git@github.com:owner/repo.git). At most 2048 characters, stored as given. |
| `default_branch` | string | no |  |  | Branch of the repository that work starts from, such as main: a branch name git accepts, at most 255 characters |
| `is_archived` | boolean | no |  |  | true archives the project, false restores it |
| `clear` | array of string | no |  |  | Names of the optional fields to empty: any of description, repository_url, default_branch |

#### `update_work_item`

**Write tool.** Change a work item. Only the arguments that are passed are changed: an argument that is left out, or passed as null, keeps its value, so update_work_item(itemId, status) changes the status and nothing else. To empty an optional field, name it in clear (description, estimate, labels, epicId); an empty string or an empty label_list is refused with code invalid_argument, as are a field that is both passed and named in clear and a call that passes no field to change. One exception, kept for callers from before clear existed: description passed as an empty or whitespace-only string leaves the item with an empty description, and labels passed as a string with no label in it ("", " ", " , ") leaves it without labels. A new status puts the item at the end of that column. A claim on the item does not stop an update by another caller: claims are advisory. Pass expected_version to refuse the update with code conflict, changing nothing, when the item was updated or moved after it was read. Returns the work item with its new version: version goes up by one with every update and every move of the item, and not when it is claimed, commented on, linked, given a dependency, or when another item is reordered around it. An unknown itemId is refused with code not_found.

| Argument | Type | Required | Default | Allowed values | Description |
| --- | --- | --- | --- | --- | --- |
| `itemId` | string (GUID) | yes |  |  | Work item id |
| `title` | string | no |  |  | New title, at most 240 characters |
| `type` | string | no |  | `Story`, `Issue`, `Task` | New type: Story, Issue, or Task |
| `status` | string | no |  | `Backlog`, `Ready`, `InProgress`, `Blocked`, `Done`, `Closed` | New status |
| `priority` | string | no |  | `Low`, `Medium`, `High`, `Critical` | New priority |
| `epicId` | string (GUID) | no |  |  | Id of the epic to put the item in: an active epic of the item's project |
| `description` | string | no |  |  | New description |
| `estimate` | integer | no |  |  | New estimate |
| `labels` | string | no |  |  | New labels as one comma-separated string. They replace the labels the item has. |
| `label_list` | array of string | no |  |  | New Labels as a list, in place of labels (passing both is refused with code invalid_argument). Each label is a non-empty string without a comma; all labels together, joined with commas, are at most 4000 characters. Duplicates, ignoring case, are dropped. They replace the labels the item has. |
| `expected_version` | integer | no |  |  | The version field of the item as it was last read. When passed, the call is refused with code conflict, changing nothing, unless the item still has that version. |
| `clear` | array of string | no |  |  | Names of the optional fields to empty: any of description, estimate, labels, epicId |

<!-- END GENERATED: tool reference -->

### What each tool returns

| Tool | Returns |
| --- | --- |
| `list_projects` | array of [Project](#project) |
| `create_project`, `update_project` | [Project](#project) |
| `get_board` | [Board](#board) |
| `list_epics` | array of [Epic](#epic) |
| `get_epic`, `create_epic`, `update_epic` | [Epic](#epic) |
| `list_epic_documents` | array of [Epic document](#epic-document) |
| `get_epic_document`, `create_epic_document`, `update_epic_document` | [Epic document](#epic-document) |
| `list_work_items` | array of [Work item](#work-item) without `limit` and `cursor`; a [Work item page](#work-item-page) with either |
| `list_epic_work_items`, `get_issues`, `find_work_items_by_link` | array of [Work item](#work-item) |
| `search_work_items` | array of [Search result](#search-result) |
| `get_work_item`, `create_work_item`, `create_epic_work_item`, `update_work_item`, `move_work_item`, `claim_work_item`, `renew_work_item_claim`, `release_work_item_claim`, `add_work_item_dependency`, `remove_work_item_dependency`, `add_work_item_link`, `remove_work_item_link`, `add_work_item_comment`, `add_issue_comment` | [Work item](#work-item) |
| `list_work_item_comments` | [Comment page](#comment-page) |
| `list_changes` | [Change page](#change-page) |
| `import_epic_plan` | [Import result](#import-result) |
| `delete_project`, `delete_epic`, `delete_work_item` | [Delete result](#delete-result) |

## Shapes

Every table lists every field of the shape, in the order the server writes them. *Present* says whether a field is in every answer:

- **always**: the field is there and is never `null`.
- **always, `null` when …**: the field is there in every answer and holds `null` in the case named.
- **left out when …**: the field is absent in the case named, and is never written as `null`. Read an absent field as "no value".

Types: a *GUID* is a string (`e0865c06-7b9a-4f0f-9a1e-3a8f3c0f2d11`); a *timestamp* is an ISO 8601 string with an offset (`2026-10-13T01:41:52.674522+00:00`); an *integer* is a JSON number without a fraction. `version` and `sequence` are integers, not GUIDs and not strings.

### Enumerations

Written and read as these strings, exactly.

| Enumeration | Values |
| --- | --- |
| Work item type | `Story`, `Issue`, `Task` |
| Work item status | `Backlog`, `Ready`, `InProgress`, `Blocked`, `Done`, `Closed` |
| Work item priority | `Low`, `Medium`, `High`, `Critical` |

### Project

| Field | Type | Present | Meaning |
| --- | --- | --- | --- |
| `id` | GUID | always | The project's id. |
| `name` | string | always | At most 160 characters. Several projects may share a name. |
| `key` | string | always | At most 24 characters, upper case, unique. |
| `description` | string | left out when the project has none | |
| `isArchived` | boolean | always | An archived project is left out of `list_projects` unless `includeArchived` is `true`. |
| `createdAtUtc` | timestamp | always | |
| `totalItems` | integer | always | The number of work items of the project. |
| `openItems` | integer | always | Those that are not `Done` and not `Closed`. |
| `repository_url` | string | always, `null` when not set | The repository the project's work is done in. |
| `default_branch` | string | always, `null` when not set | The branch of that repository work starts from. |
| `created` | boolean | left out when the answer is not that of `create_project` | `false` when the `idempotency_key` was a repeat and nothing was created. |

### Board

The answer of `get_board`: the project and its work items.

| Field | Type | Present | Meaning |
| --- | --- | --- | --- |
| `id` | GUID | always | The project's id. |
| `name` | string | always | |
| `key` | string | always | |
| `description` | string | left out when the project has none | |
| `isArchived` | boolean | always | |
| `createdAtUtc` | timestamp | always | |
| `items` | array of [Work item](#work-item) | always | By status, then `order`. `Closed` items are left out unless `status` is `Closed`. |
| `repository_url` | string | always, `null` when not set | As on [Project](#project). |
| `default_branch` | string | always, `null` when not set | As on [Project](#project). |

### Epic

| Field | Type | Present | Meaning |
| --- | --- | --- | --- |
| `id` | GUID | always | |
| `projectId` | GUID | always | |
| `name` | string | always | At most 160 characters. |
| `description` | string | left out when the epic has none | |
| `isArchived` | boolean | always | |
| `createdAtUtc` | timestamp | always | |
| `updatedAtUtc` | timestamp | always | |
| `created` | boolean | left out when the answer is not that of `create_epic` | `false` for a repeated `idempotency_key`. |

### Epic document

| Field | Type | Present | Meaning |
| --- | --- | --- | --- |
| `id` | GUID | always | |
| `epicId` | GUID | always | |
| `title` | string | always | At most 200 characters. |
| `body` | string | always | |
| `createdAtUtc` | timestamp | always | |
| `updatedAtUtc` | timestamp | always | |
| `created` | boolean | left out when the answer is not that of `create_epic_document` | `false` for a repeated `idempotency_key`. |

### Work item

Every tool that returns a work item returns this shape.

| Field | Type | Present | Meaning |
| --- | --- | --- | --- |
| `id` | GUID | always | |
| `projectId` | GUID | always | Never changes. |
| `epicId` | GUID | left out when the item is in no epic | |
| `epic` | [Epic reference](#epic-reference) | left out when the item is in no epic | |
| `title` | string | always | At most 240 characters. |
| `description` | string | left out when the item has none | `""` after the legacy empty-string update (see [Clearing a field](#clearing-a-field)). |
| `type` | string | always | A work item type. |
| `status` | string | always | A work item status: the column. |
| `priority` | string | always | A work item priority. |
| `order` | integer | always | The place in the column, lowest first. See [`order`](#order). |
| `estimate` | integer | always, `null` when not set | |
| `labels` | string | left out when the item has no labels | The labels joined with commas. |
| `comments` | array of [Comment](#comment) | always | Oldest first. `[]` when there are none, and for every item of `list_work_items` with `include_comments: false`. |
| `createdAtUtc` | timestamp | always | |
| `updatedAtUtc` | timestamp | always | The content timestamp. See [`updatedAtUtc`](#updatedatutc). |
| `claimant` | string | always, `null` when the item is unclaimed or its claim has expired | The name of the identity that holds the live claim. |
| `claimant_id` | GUID | always, `null` when the item is unclaimed or its claim has expired | The id of that identity. |
| `claim_marker` | string | always, `null` when the item is unclaimed or its claim has expired | The marker the claim was made with. |
| `claim_expires_at_utc` | timestamp | always, `null` when the item is unclaimed or its claim has expired | When the claim lapses unless renewed. |
| `blockers` | array of [Dependency reference](#dependency-reference) | always | The items this one is blocked by. `[]` when there are none. |
| `dependents` | array of [Dependency reference](#dependency-reference) | always | The items this one blocks. `[]` when there are none. |
| `links` | array of [Link](#link) | always | Oldest first. `[]` when there are none. |
| `version` | integer | always | The write counter. See [`version`](#version-and-expected_version). |
| `label_list` | array of string | always | The labels of `labels` as a list, in the same order. `[]` when there are none. |
| `comment_id` | GUID | left out when the answer is not that of `add_work_item_comment` or `add_issue_comment` | The id of the comment that was added, or of the earlier comment of a repeated `idempotency_key`. |
| `comment_created` | boolean | left out when the answer is not that of `add_work_item_comment` or `add_issue_comment` | `false` for a repeated `idempotency_key`. |
| `created` | boolean | left out when the answer is not that of `create_work_item` or `create_epic_work_item` | `false` for a repeated `idempotency_key`. |

### Epic reference

| Field | Type | Present | Meaning |
| --- | --- | --- | --- |
| `id` | GUID | always | The epic's id, the same as `epicId`. |
| `name` | string | always | |

### Dependency reference

An entry of `blockers` or `dependents`.

| Field | Type | Present | Meaning |
| --- | --- | --- | --- |
| `id` | GUID | always | The other work item. |
| `title` | string | always | Its title now. |
| `status` | string | always | Its status now. A blocker counts as finished only in `Done`; a `Closed` blocker still blocks. |

### Link

| Field | Type | Present | Meaning |
| --- | --- | --- | --- |
| `id` | GUID | always | The `link_id` of `remove_work_item_link`. |
| `kind` | string | always | `pr`, `branch` or `other`. |
| `url` | string | always | As it was added. |
| `created_at_utc` | timestamp | always | |

### Comment

| Field | Type | Present | Meaning |
| --- | --- | --- | --- |
| `id` | GUID | always | Never changes. |
| `workItemId` | GUID | always | |
| `author` | string | always | With a token: the name of the token's identity when the comment was written. Without one: the name the writer gave. |
| `body` | string | always | |
| `createdAtUtc` | timestamp | always | Later than that of every earlier comment of the item. |
| `author_id` | GUID | left out when the comment was written without a token | The id of the token's identity. |

### Search result

An entry of the answer of `search_work_items`.

| Field | Type | Present | Meaning |
| --- | --- | --- | --- |
| `id` | GUID | always | |
| `projectId` | GUID | always | |
| `title` | string | always | |
| `type` | string | always | |
| `status` | string | always | |
| `estimate` | integer | always, `null` when not set | |
| `version` | integer | always | |

### Work item page

The answer of `list_work_items` when `limit` or `cursor` is passed.

| Field | Type | Present | Meaning |
| --- | --- | --- | --- |
| `items` | array of [Work item](#work-item) | always | Oldest item first (creation time, then id). |
| `next_cursor` | string | left out when this is the last page | Pass it back as `cursor`, with the same filters. |

### Comment page

The answer of `list_work_item_comments`.

| Field | Type | Present | Meaning |
| --- | --- | --- | --- |
| `items` | array of [Comment](#comment) | always | Oldest first. |
| `next_cursor` | string | left out when this is the last page | Pass it back as `cursor`. |

### Change page

The answer of `list_changes`.

| Field | Type | Present | Meaning |
| --- | --- | --- | --- |
| `items` | array of [Change entry](#change-entry) | always | In the order the changes were written. `[]` when there is nothing after the cursor. |
| `next_cursor` | string | always | The position after the last entry returned, or the position that was asked for when there was none. Pass it to the next call whatever came back. |

### Change entry

| Field | Type | Present | Meaning |
| --- | --- | --- | --- |
| `sequence` | integer | always | The position in the feed. Every later entry has a higher one; numbers may be skipped. |
| `item_id` | GUID | always | The work item that changed. It may have been deleted since. |
| `project_id` | GUID | always | Its project. |
| `kind` | string | always | One of the [change kinds](#change-kinds). |
| `actor` | [Actor](#actor) | left out when the write carried neither a token nor a browser session | Who made the change. |
| `changes` | array of `{field, old, new}` | always | One entry per field the write changed. `old` and `new` are always there and are `null` for "no value". `old_truncated: true` or `new_truncated: true` is added when a text was cut to 16384 characters, and left out otherwise. |
| `created_at_utc` | timestamp | always | |

### Change kinds

| Kind | Written by | `changes` holds |
| --- | --- | --- |
| `item_created` | `create_work_item`, `create_epic_work_item`, `import_epic_plan` (one per item) | every field the new item has a value for, with `old` null |
| `item_updated` | `update_work_item`; `update_epic` that archives and `delete_epic`, for each item taken out of the epic | the fields whose value changed, and `version` |
| `item_moved` | `move_work_item` | `status` and `order` when they changed, and `version` |
| `item_deleted` | `delete_work_item`; `delete_project`, for each item of the project | every field the item had a value for, with `new` null |
| `claimed` | `claim_work_item` | the claim fields that changed |
| `claim_renewed` | `renew_work_item_claim` | `claim_expires_at_utc` |
| `claim_released` | `release_work_item_claim` | the claim fields, with `new` null |
| `comment_added` | `add_work_item_comment`, `add_issue_comment` | `comment`: `old` null, `new` `{id, author, body}` |
| `link_added` | `add_work_item_link` | `link`: `old` null, `new` `{id, kind, url}` |
| `link_removed` | `remove_work_item_link` | `link`: `old` `{id, kind, url}`, `new` null |
| `dependency_added` | `add_work_item_dependency`; `import_epic_plan` (one per dependency) | `blocked_by`: `old` null, `new` the id of the blocking item; the entry is on the blocked item |
| `dependency_removed` | `remove_work_item_dependency`; `delete_work_item` and `delete_project`, for each item left that a deleted item blocked | `blocked_by`: `old` the id of the blocking item, `new` null |

The `field` of a change is one of: the item's own fields `title`, `description`, `type`, `status`, `priority`, `order`, `estimate`, `labels` (the comma-separated string), `epic_id`, `version`; the stored claim `claimant_id`, `claimant`, `claim_marker`, `claim_expires_at_utc`; or `comment`, `link`, `blocked_by`. The same writes made from the web UI are in the feed as well. Kinds are a contract: new ones may be added, none is renamed.

A write that is refused or that changes nothing has no entry. Changes to a project, an epic or an epic document themselves have no entry; only their effect on work items has.

### Actor

| Field | Type | Present | Meaning |
| --- | --- | --- | --- |
| `id` | GUID | left out when the change was made in a browser session whose user has no stored id | The id of the identity: the same id as `claimant_id` and `author_id`. |
| `name` | string | left out when the identity has no name, which no write of this server produces | The name of the identity when the change was made. |

### Import result

The answer of `import_epic_plan`.

| Field | Type | Present | Meaning |
| --- | --- | --- | --- |
| `created` | boolean | always | `false` when the `idempotency_key` was a repeat: nothing was created and the ids are those of the first import. |
| `project_id` | GUID | always | |
| `epic_id` | GUID | always | The epic that was created. |
| `document_ids` | array of GUID | always | In the order of the plan. `[]` for a plan without documents. |
| `item_ids` | object | always | Maps each `ref` of the plan to the GUID of the work item created for it. |

### Delete result

The answer of `delete_project`, `delete_epic` and `delete_work_item`.

| Field | Type | Present | Meaning |
| --- | --- | --- | --- |
| `id` | GUID | always | The id that was passed. |
| `entityType` | string | always | `project`, `epic` or `work_item`. |
| `deleted` | boolean | always | `true`. A delete that did not happen is an error, not `false`. |

### Error

The `structuredContent` of a refused call, and the JSON of its single text block.

| Field | Type | Present | Meaning |
| --- | --- | --- | --- |
| `code` | string | always | One of the codes under [Errors](#errors). Branch on it. |
| `message` | string | always | For people. It may change; do not parse it. |

## Errors

A refused call returns a tool result with `isError: true` whose `structuredContent` is `{"code": "...", "message": "..."}`. A refused call changed nothing.

| Code | When | Retry? |
| --- | --- | --- |
| `scope_denied` | A write tool was called with a `read` token; or the token was revoked or has expired (any tool, read or write); or a claim tool was called on a server that does not require tokens, without one. | No. Use a `full` token that is valid. |
| `not_found` | The id that addresses the call (project, epic, epic document, work item) is not known. The message names the object and the id. | No. |
| `conflict` | The object is no longer in the state the call depends on: a stale `expected_version` on `update_work_item` or `move_work_item`; a renew or release of a claim the caller no longer holds; a project `key` another project has; an `import_epic_plan` whose epic was deleted while the call ran. | Not as it is. Read again, decide, then call again. |
| `already_claimed` | `claim_work_item`, `renew_work_item_claim` or `release_work_item_claim` on an item with a live claim of another identity or another marker. | Later: after the claim is released or has expired. |
| `cycle` | `add_work_item_dependency`, or the dependencies of a plan given to `import_epic_plan`, would make an item block itself, directly or through other items. | No. |
| `invalid_argument` | An argument is missing, malformed, out of range, of the wrong JSON type, or not an argument of the tool at all; or a plan breaks a rule. A missing, misspelt or mistyped argument is refused before the tool runs. | No. Fix the call. |
| `busy` | The server could not take the call in time because of other callers: a write waited 10 s for a lock, or was rolled back as the victim of a deadlock three times. | Yes, the same call. Use the same `idempotency_key` when it has one. |

Anything else that goes wrong (the API cannot be reached, the server faults while or after the tool ran) is a plain MCP error with `isError: true` and no `structuredContent`. Such a call may have written: read before repeating it, unless it carries an `idempotency_key`.

Order of the checks for a write tool: the scope first (`scope_denied`), then the arguments (`invalid_argument`), then the call itself.

## Conventions

### Naming

Tool names are `snake_case`. Argument and field names come in two spellings, and each name has exactly one:

- **Names from before the factory work are `camelCase`** and stay so: the arguments `projectId`, `epicId`, `itemId`, `documentId`, `includeArchived`, `includeArchivedEpics`, `isArchived` (of `create_epic` and `update_epic`), and the fields `projectId`, `epicId`, `workItemId`, `isArchived`, `createdAtUtc`, `updatedAtUtc`, `totalItems`, `openItems`, `entityType`.
- **Everything added since is `snake_case`**: `expected_version`, `idempotency_key`, `unblocked_only`, `label_list`, `claim_marker`, `claim_expires_at_utc`, `repository_url`, `default_branch`, `is_archived` (of `update_project`), `project_id`, `item_id`, `created_at_utc`, `next_cursor`, `author_id`, `comment_id`.

Names are compared exactly. The spelling to use is the one in the [tool reference](#tool-reference) and in the [shapes](#shapes): `itemId` for `update_work_item` and `id` for `claim_work_item`, `createdAtUtc` on a comment and `created_at_utc` on a link.

### Arguments

- An argument that is left out, or passed as `null`, is not set. For the partial updates (`update_project`, `update_work_item`) that means the field keeps its value.
- **An argument the tool does not have is refused** `invalid_argument`, before the tool runs, and the message lists the arguments the tool has. Nothing is dropped silently: `expected_versoin` does not turn a guarded write into an unguarded one.
- An argument of the wrong JSON type, and a required argument that is missing, are refused `invalid_argument` the same way.
- `update_epic` and `update_epic_document` are not partial updates: see [Known limitations](#known-limitations).

### Clearing a field

To empty an optional field with `update_project` or `update_work_item`, name it in `clear`: `update_work_item(itemId, clear: ["estimate", "labels"])`. `clear` may name `description`, `repository_url`, `default_branch` for a project and `description`, `estimate`, `labels`, `epicId` for a work item. An empty or whitespace-only string is not a way to clear and is refused `invalid_argument`; so is an empty `label_list`, a name `clear` does not know, and a field that is both passed and named in `clear`.

One legacy exception, for callers from before `clear`: `update_work_item` still takes `description: ""` (the item is left with the empty description `""`, where `clear` leaves none) and `labels` as a string without a label in it, such as `""` (the item is left without labels).

### Optional fields: left out or `null`

The server leaves a field without a value out of the answer, except the fields it promises in every answer. The [shapes](#shapes) say which is which for every field; in short:

- **Always present, `null` when empty:** `estimate`, `claimant`, `claimant_id`, `claim_marker`, `claim_expires_at_utc` on a work item; `estimate` on a search result; `repository_url` and `default_branch` on a project and on a board; `old` and `new` of a change.
- **Left out when empty:** `description` (project, board, epic, work item), `epicId`, `epic` and `labels` of a work item, `author_id` of a comment, `actor` of a change entry, `next_cursor` of a work item page and of a comment page, and the fields that belong to one tool's answer (`created`, `comment_id`, `comment_created`).
- **Always present and never `null`:** everything else, including the arrays (`comments`, `blockers`, `dependents`, `links`, `label_list`, `items`), which are `[]` when empty, and `next_cursor` of `list_changes`.

### Idempotency keys

Every create takes an `idempotency_key`; `import_epic_plan` requires one. A key is 1 to 128 printable ASCII characters without spaces, compared exactly (`run-1` and `RUN-1` are two keys); anything else is refused `invalid_argument`.

A key belongs to the calling identity and to the place the object is created in. A repeat of a key by the same identity in the same place creates nothing, adds nothing to the change feed, and returns the object the key created **as it is now**, whatever the other arguments of the repeat; the arguments are not compared. Two calls with one key at the same moment create one object and both report it.

| Tool | The key is unique per identity and | The answer says | A repeat returns |
| --- | --- | --- | --- |
| `create_project` | nothing else | `created` | the project as it is now |
| `create_epic` | project | `created` | the epic as it is now |
| `create_epic_document` | epic | `created` | the document as it is now |
| `create_work_item`, `create_epic_work_item` (one set of keys) | project | `created` | the work item as it is now |
| `add_work_item_comment`, `add_issue_comment` (one set of keys) | work item | `comment_created`, with `comment_id` | the work item as it is now; `comment_id` is the earlier comment |
| `import_epic_plan` | project | `created` | the stored answer of the first import: the same ids, also after its items were changed or deleted |

The keys of a project's epics, of its work items and of its imports are three separate sets. Deleting the object frees its key; deleting the epic of an import frees the import's key, and its items stay in the project without an epic. Callers without a token share one identity. Without a key every call creates.

### Cursors

`list_work_items`, `list_work_item_comments` and `list_changes` page with a cursor. A cursor is opaque: do not build or parse one. A cursor belongs to the tool that returned it and is refused `invalid_argument` by the others, as is a malformed one.

- `list_work_items` and `list_work_item_comments`: pass `next_cursor` back as `cursor` (for `list_work_items` with the same filters) until a page comes without `next_cursor`. Each item that exists for the whole walk is returned exactly once.
- `list_changes`: `next_cursor` is **always** there, also when `items` is empty. Call without `cursor` to get the cursor of the newest entry and no entries; `from_start: true` reads from the oldest entry. Then pass each `next_cursor` to the next call. Each entry is returned exactly once, in write order; fewer entries than `limit` means the end was reached. One cursor works with any `project_id` or none. A cursor ahead of the newest entry (the database was restored) is refused `invalid_argument`: start again without `cursor`.
- `limit` is 1 to 200 for all three, 50 when left out; outside that range it is refused `invalid_argument`.

### `version` and `expected_version`

`version` is the write counter of a work item: `1` when it is created, plus one for every write that is not refused of:

- `update_work_item` (also when the values passed are the ones the item already has),
- `move_work_item`,
- the same two made from the web UI,
- `update_epic` that archives the item's epic and `delete_epic`, which take the item out of the epic.

It does **not** change when the item is claimed, renewed or released, when its claim expires, when a comment, a link or a dependency is added or removed, or when another item is moved around it.

`update_work_item` and `move_work_item` take `expected_version`: the `version` of the item as it was last read. The write happens only when the item still has that version; otherwise it is refused `conflict`, nothing is written, and the message names the current version. Of several calls that pass the same `expected_version` at once, exactly one succeeds. Without `expected_version` the write is unconditional: the last writer wins. No other tool takes it.

### `updatedAtUtc`

`updatedAtUtc` of a work item is its content timestamp. It is stamped by: `create_work_item`, `create_epic_work_item`, `import_epic_plan`; `update_work_item`; `move_work_item`; being taken out of an epic that is archived or deleted; a comment that is added (not a repeat of a key); a link that is added or removed (not one that changed nothing).

It is **not** stamped by a claim, a renew, a release or the expiry of a claim, by a dependency that is added or removed, by a blocker that is finished, or by another item being moved around it.

The stamp is taken before the write commits, so a change can become visible slightly after one that carries a later stamp. To follow content with `updated_since`, poll with an overlap of a few seconds and de-duplicate by `id` plus `updatedAtUtc`. To follow every change in order, without overlap, use `list_changes`.

### `order`

`order` is the place of an item in its column: sort ascending. Within one column of one project no two items have the same `order`, however many callers create, import, move and update at once: those writes run one at a time per project. Values need not be contiguous and need not start at 0 (an item that leaves a column leaves a gap, and `move_work_item` stores the `order` it is given, also one past the end): compare them, do not compute with them. A new item, and an item given a new `status` by `update_work_item`, goes to the end of its column.

### Claims

- `claim_work_item(id, marker, ttl_seconds)` succeeds when the item is unclaimed, its claim has expired, or **the same caller** already holds it. The same caller means the same identity **and** the same `marker`; the expiry is then reset. Another identity, or the same identity with another marker, is refused `already_claimed`. Of several claims at once exactly one wins.
- `renew_work_item_claim` extends a live claim of the same caller. It is refused `conflict` when that claim has lapsed or was released, and `already_claimed` when someone else now holds the item.
- `release_work_item_claim` clears the claim of the same caller, also after it lapsed. It is refused the same two ways.
- **Expiry.** A claim lapses `ttl_seconds` (1 to 86400) after the last claim or renew. From then on the item reads as unclaimed: the four claim fields are `null`, and anyone can claim it. The expiry itself writes nothing: no `updatedAtUtc`, no `version`, no entry in the change feed.
- The caller's own id is the `claimant_id` of its own claim. The same id is the `author_id` of its comments and the `actor.id` of its entries in the change feed.
- Claims are advisory: see [Known limitations](#known-limitations).

### Polling for ready work

To find work that is ready to take, call

```
list_work_items(projectId, status: "Ready", unblocked_only: true, claim_state: "unclaimed")
```

and do **not** add `updated_since`: an item becomes ready when its last blocker is finished or when a claim on it expires, and neither stamps the item's `updatedAtUtc`. Then `claim_work_item` the item; a caller that loses the race gets `already_claimed` and takes the next one. Use `updated_since`, or better `list_changes`, to follow content changes.

### Limits

| What | Limit |
| --- | --- |
| Items in one plan | 200 |
| Documents in one plan | 20 |
| Dependencies in one plan (all `blocked_by` entries) | 500 |
| Size of one plan | 2097152 bytes (2 MB) of JSON as the MCP server sends it to the API |
| `ref` of a plan item | 64 characters |
| Link `url`, project `repository_url` | 2048 characters |
| Link `url`, percent-encoded | 6144 characters |
| Labels of one item, joined with commas | 4000 characters |
| Work item `title` | 240 characters |
| Epic `name`, project `name` | 160 characters |
| Epic document `title` | 200 characters |
| Project `key` | 24 characters |
| Project `default_branch` | 255 characters |
| `idempotency_key` | 128 characters |
| Claim `marker` | 200 characters |
| Claim `ttl_seconds` | 1 to 86400 |
| One text in a change entry | 16384 characters; a longer `old` or `new` is cut to its first 16384 and marked `old_truncated` / `new_truncated` |
| `limit` of `list_work_items`, `list_work_item_comments`, `list_changes` | 1 to 200 (default 50) |
| `search_work_items` `limit` | 100 (default 25) |

A value over a limit is refused `invalid_argument`, with two exceptions: a `limit` of `search_work_items` outside 1 to 100 is brought into that range instead of refused, and a text longer than 16384 characters is written whole to the item and only cut in the change feed.

## Known limitations

- **Claims are advisory.** A claim stops another caller's `claim_work_item`, and nothing else. `update_work_item`, `move_work_item`, `delete_work_item`, the comment, link and dependency tools and the web UI do not look at the claim: another identity can change or delete an item that is claimed. Callers that share a board must check `claimant_id` themselves, and use `expected_version` to notice a write they did not make.
- **`update_epic` and `update_epic_document` replace the whole object and are unconditional.** `update_epic(epicId, name)` writes all three of `name`, `description` and `isArchived`: an argument that is left out is not kept, so the description is emptied and an archived epic is restored. `update_epic_document(documentId, title, body)` replaces the title and the whole body. Neither takes `expected_version` or `clear`: read the object first, pass every field, and the last writer wins. This is how the two tools have always behaved.
- **Projects, epics and epic documents have no `version` and no entry in the change feed.** Only work items do. `update_project`, the epic tools and the delete tools are unconditional.
- **The change feed starts at the deploy that added it.** Writes made before that have no entries: a reader that starts with `from_start: true` sees the board's history only from then on, and must read the board itself (`list_work_items`) for the state before.
- **A rollback to an image from before the change feed loses entries.** Such an image runs against the current schema and its writes succeed, but they add no entry to the change feed and do not advance `version`. For that window a follower of `list_changes` misses writes and `expected_version` does not protect against them. See [deployment](deployment.md#manual-rollback).
- **The change feed is never pruned.** It grows by one row per write to a work item.
- **`list_work_items` without `limit` and `cursor` returns every matching item in one answer**, with its comments. Pass `limit` (and `include_comments: false`) to page.
- **`Closed` items are hidden** by `get_board`, `list_work_items`, `list_epic_work_items` and `get_issues` unless `status` is `Closed`. `search_work_items`, `find_work_items_by_link` and `get_work_item` return them.
- **A revoked token can pass the MCP server's own check for up to 30 s** (its validation cache); the API refuses it on every call all the same, read or write, with `scope_denied`.
