# Kanban Board

A lightweight single-developer kanban board with:

- .NET 10 API and static web UI
- SQLite persistence
- HTTP MCP server for agent tooling
- Docker Compose for the whole stack

## Run locally

```bash
dotnet run --project ./KanbanBoard.Api
```

The API and UI will be available at [http://localhost:5157](http://localhost:5157) unless you override `ASPNETCORE_URLS`.

To run the MCP server locally:

```bash
ASPNETCORE_URLS=http://+:3001 KANBAN_API_BASE_URL=http://localhost:5157 dotnet run --project ./KanbanBoard.Mcp
```

The MCP endpoint is exposed at [http://localhost:3001/mcp](http://localhost:3001/mcp).

## Run with Docker Compose

```bash
docker compose up --build
```

Services:

- Web UI and API: [http://localhost:8080](http://localhost:8080)
- MCP server: [http://localhost:3001/mcp](http://localhost:3001/mcp)

SQLite data persists in the `kanban_data` named volume.

## MCP tools

The MCP service exposes tools for:

- Listing projects
- Creating projects
- Fetching a board
- Listing, creating, updating, and retrieving epics
- Deleting epics
- Listing, creating, updating, and retrieving epic documents
- Fetching issue items
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

### Available tool names

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
- `GetIssues`
- `CreateWorkItem`
- `UpdateWorkItem`
- `MoveWorkItem`
- `DeleteWorkItem`
