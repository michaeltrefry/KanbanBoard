# SQLite to MariaDB Data Migration

Kanban data is expected to be preserved when moving the API runtime from SQLite to MariaDB.
Use `tools/KanbanBoard.DataMigrator` as an operator-run, one-time migration utility after the MariaDB schema has been created with [mariadb-schema.md](mariadb-schema.md).

The migrator copies data in dependency order:

1. `Projects`
2. `AppUsers`
3. `Epics`
4. `EpicDocuments`
5. `WorkItems`
6. `WorkItemComments`
7. `PersonalAccessTokens`

It preserves IDs, timestamps, ordering, labels, archived state, comments, documents, local users, and PAT encrypted metadata. Existing PATs remain valid only if the MariaDB deployment uses the same `PersonalAccessTokens__EncryptionKey` that encrypted them originally.

## Direct MariaDB Access

```bash
dotnet run --project tools/KanbanBoard.DataMigrator -- copy \
  --sqlite ./KanbanBoard.Api/App_Data/kanban.db \
  --mariadb "Server=127.0.0.1;Port=3306;Database=kanban;User ID=kanban;Password=secret;" \
  --replace-target \
  --output ./kanban-migration.json
```

By default, `copy` creates a SQLite backup next to the source database before importing rows. Use `--backup ./path/to/kanban.backup.db` to choose the backup path.

Without `--replace-target`, the utility refuses to import into non-empty MariaDB tables.

## Built-In SSH Tunnel

If MariaDB is only reachable from the server, the utility can open a temporary SSH tunnel using the local `ssh` command:

```bash
dotnet run --project tools/KanbanBoard.DataMigrator -- copy \
  --sqlite ./KanbanBoard.Api/App_Data/kanban.db \
  --mariadb "Server=ignored;Database=kanban;User ID=kanban;Password=secret;" \
  --ssh-host linode.example.com \
  --ssh-user deploy \
  --ssh-key ~/.ssh/linode_deploy \
  --ssh-remote-host 127.0.0.1 \
  --ssh-remote-port 3306 \
  --replace-target
```

When `--ssh-host` is provided, the migrator rewrites the MariaDB connection string to use `127.0.0.1:<temporary-local-port>` for the duration of the run. SSH uses `BatchMode=yes`, so the key must be available without an interactive password prompt, usually through `ssh-agent` or an unencrypted deploy key.

## JSON Snapshot Workflow

For an extra safety checkpoint, export first:

```bash
dotnet run --project tools/KanbanBoard.DataMigrator -- export-json \
  --sqlite ./KanbanBoard.Api/App_Data/kanban.db \
  --output ./kanban-migration.json
```

Then import the snapshot:

```bash
dotnet run --project tools/KanbanBoard.DataMigrator -- import-json \
  --input ./kanban-migration.json \
  --mariadb "Server=127.0.0.1;Database=kanban;User ID=kanban;Password=secret;" \
  --replace-target
```

The same `--ssh-*` options are supported by `import-json`.

## Validation

After import, the utility validates:

- row counts for every migrated table
- foreign-key relationships for projects, epics, documents, work items, comments, users, and PATs
- one representative board-style project read from MariaDB

If validation fails, the utility exits non-zero.
