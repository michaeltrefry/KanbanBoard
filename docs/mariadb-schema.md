# MariaDB Schema

KanbanBoard.Api now uses MariaDB through `MySql.EntityFrameworkCore`.
The application expects `ConnectionStrings__Kanban` to point at an existing database.

## Create Database

Create the database and user before the API or the scripts create the schema:

```sql
CREATE DATABASE kanban CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER 'kanban'@'%' IDENTIFIED BY 'replace-with-secret';
GRANT ALL PRIVILEGES ON kanban.* TO 'kanban'@'%';
FLUSH PRIVILEGES;
```

Restrict the host portion of the user if your MariaDB topology allows it.

## Apply Schema

The API applies pending EF Core migrations on startup (`Database:MigrateOnStartup`, default `true`), so a database that already has `001` applied is brought up to date by deploying the new image. The connection's user therefore needs schema-change privileges on `kanban` (at least `ALTER`, plus `CREATE`, `INDEX`, `DROP` and `REFERENCES` for migrations that add tables, indexes or keys). The `GRANT ALL PRIVILEGES ON kanban.*` above covers this. See [deployment.md](deployment.md#schema-changes-on-deploy).

The checked-in scripts remain for bootstrapping a new database and for manual use (for example with `Database__MigrateOnStartup=false`). Run them in order:

```bash
mariadb -h HOST -P 3306 -u kanban -p kanban < deploy/sql/001_initial_mariadb_schema.sql
mariadb -h HOST -P 3306 -u kanban -p kanban < deploy/sql/002_personal_access_token_scope.sql
```

Each script records its migration id in `__EFMigrationsHistory`, so the API skips migrations already applied by hand. A test (`StartupMigrationTests.Deploy_sql_history_rows_match_every_ef_migration`) fails if the ids in `deploy/sql` drift from the migrations in `KanbanBoard.Api/Data/Migrations`; add a numbered script with each new migration.

`002` adds the `PersonalAccessTokens.Scope` column (`read` or `full`); existing tokens default to `full`. It is safe to re-run.

`001` creates the EF migrations history table and the initial Kanban schema:

- `Projects`
- `AppUsers`
- `Epics`
- `EpicDocuments`
- `WorkItems`
- `WorkItemComments`
- `PersonalAccessTokens`

MariaDB note: `MySql.EntityFrameworkCore` takes the migration lock with `GET_LOCK('__EFMigrationsLock',-1)`, which MariaDB rejects, and reads the result as a MySQL `BIGINT` while MariaDB returns `INT`. `MariaDbMigrationLockInterceptor` rewrites that one statement to a positive timeout and widens the result. It is registered on the API's `DbContext` and on the design-time factory (`dotnet ef database update`).

## Data Import

After the schema exists, use the SQLite to MariaDB data migration utility documented in [sqlite-to-mariadb.md](sqlite-to-mariadb.md).
