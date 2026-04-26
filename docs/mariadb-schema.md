# MariaDB Schema

KanbanBoard.Api now uses MariaDB through `MySql.EntityFrameworkCore`.
The application expects `ConnectionStrings__Kanban` to point at an existing database.

## Create Database

Create the database and user before running schema migrations:

```sql
CREATE DATABASE kanban CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER 'kanban'@'%' IDENTIFIED BY 'replace-with-secret';
GRANT ALL PRIVILEGES ON kanban.* TO 'kanban'@'%';
FLUSH PRIVILEGES;
```

Restrict the host portion of the user if your MariaDB topology allows it.

## Apply Schema

Run the checked-in schema script:

```bash
mariadb -h HOST -P 3306 -u kanban -p kanban < deploy/sql/001_initial_mariadb_schema.sql
```

The script creates the EF migrations history table and the current Kanban schema:

- `Projects`
- `AppUsers`
- `Epics`
- `EpicDocuments`
- `WorkItems`
- `WorkItemComments`
- `PersonalAccessTokens`

The app does not apply migrations on startup. The production flow is operator-run schema migration, then data import, then app restart. This avoids silent schema mutation against the shared MariaDB instance.

## Data Import

After the schema exists, use the SQLite to MariaDB data migration utility documented in [sqlite-to-mariadb.md](sqlite-to-mariadb.md).
