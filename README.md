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

The MCP service requires `Authorization: Bearer <personal-access-token>` on `/mcp`. Users create PATs from the settings page; the MCP server validates them through a secret-protected internal API endpoint and forwards tool calls to the API with internal service authentication.

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
- MCP: `https://kanban-mcp.trefry.net/mcp`
- Deployment guide: [docs/deployment.md](docs/deployment.md)
- MariaDB schema: [docs/mariadb-schema.md](docs/mariadb-schema.md)
- SQLite to MariaDB data migration: [docs/sqlite-to-mariadb.md](docs/sqlite-to-mariadb.md)

Production secrets such as `ConnectionStrings__Kanban`, `Auth__ClientSecret`, `PersonalAccessTokens__EncryptionKey`, and `InternalApi__SharedSecret` are supplied through GitHub-managed Actions secrets and written to `/opt/kanban-board/.env.release` during deployment. Local `.env` files are ignored and are for local development only.
