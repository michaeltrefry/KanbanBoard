# Kanban Board

A lightweight single-developer kanban board with:

- .NET 10 API and static web UI
- SQLite persistence
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
dotnet run --launch-profile https --project ./KanbanBoard.Api
```

The API and UI will be available at [https://localhost:7256](https://localhost:7256).

To run the MCP server locally over HTTPS:

```bash
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

By default, the SQLite database is bind-mounted to `./.kanban-data` on your host machine, so it survives container rebuilds and Docker volume resets.

You can override the default host ports if they conflict with another local stack:

```bash
KANBAN_DOCKER_HTTPS_PORT=9443 KANBAN_MCP_HTTPS_PORT=3002 docker compose up --build
```

You can also move the database anywhere on your machine by overriding the host data path:

```bash
KANBAN_HOST_DATA_DIR=/absolute/path/to/kanban-data docker compose up --build
```

For local auth testing with Docker Compose, put `Auth__*` and `PersonalAccessTokens__*` settings in a repo-root `.env`. The file is ignored by git. For `dotnet run`, export those variables in your shell first.

Services:

- Web UI and API: [https://localhost:8444](https://localhost:8444)
- MCP server from the host: [https://localhost:3001/mcp](https://localhost:3001/mcp)
- MCP server from Docker containers on `trefry-network`: `http://kanban-mcp:3000/mcp`

SQLite data persists in the host directory configured by `KANBAN_HOST_DATA_DIR` or, by default, `./.kanban-data`.
The API container still listens on internal HTTP port `8080` so the MCP container can call it as `http://api:8080` without certificate hostname mismatches on the Docker network.

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

## Production deployment

Production deployment is handled by GitHub Actions, GHCR, and Docker Compose. Caddy is managed separately on the host.

- Web UI/API: `https://kanban.trefry.net`
- MCP: `https://kanban-mcp.trefry.net/mcp`, keep blocked in host Caddy until PAT authentication is implemented
- Deployment guide: [docs/deployment.md](docs/deployment.md)

Production auth secrets such as `Auth__ClientSecret` and `PersonalAccessTokens__EncryptionKey` are supplied through GitHub-managed Actions secrets and written to `/opt/kanban-board/.env.release` during deployment. Local `.env` files are ignored and are for local development only.
