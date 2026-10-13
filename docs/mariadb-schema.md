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
mariadb -h HOST -P 3306 -u kanban -p kanban < deploy/sql/003_work_item_claim.sql
mariadb -h HOST -P 3306 -u kanban -p kanban < deploy/sql/004_work_item_dependencies.sql
mariadb -h HOST -P 3306 -u kanban -p kanban < deploy/sql/005_work_item_links.sql
mariadb -h HOST -P 3306 -u kanban -p kanban < deploy/sql/006_work_item_comment_author_and_idempotency_key.sql
mariadb -h HOST -P 3306 -u kanban -p kanban < deploy/sql/007_project_repository_and_idempotency_key.sql
```

Each script records its migration id in `__EFMigrationsHistory`, so the API skips migrations already applied by hand. A test (`StartupMigrationTests.Deploy_sql_history_rows_match_every_ef_migration`) fails if the ids in `deploy/sql` drift from the migrations in `KanbanBoard.Api/Data/Migrations`; add a numbered script with each new migration.

`002` adds the `PersonalAccessTokens.Scope` column (`read` or `full`); existing tokens default to `full`. It is safe to re-run.

`003` adds the claim columns to `WorkItems`: `ClaimantUserId`, `ClaimantName`, `ClaimMarker` and `ClaimExpiresAtUtc`, all nullable, so existing items start unclaimed. `ClaimMarker` is `utf8mb4_bin` so markers compare byte for byte. `MySql.EntityFrameworkCore` leaves the collation out of the `ADD` it generates, so the migration (and the script) sets it with an explicit `MODIFY`. It is safe to re-run.

`004` adds the `WorkItemDependencies` table: one row per edge, `BlockedItemId` is blocked by `BlockerItemId`, with the pair as primary key. Both columns reference `WorkItems.Id` with `ON DELETE CASCADE`, so deleting a work item or its project removes its edges. It is safe to re-run.

The API serialises dependency adds with the MariaDB named lock `kanban_work_item_dependencies` (`GET_LOCK`, 10 s timeout), so the cycle check and the insert of two concurrent adds cannot interleave. It is held for one request and released on the same connection; `SELECT IS_USED_LOCK('kanban_work_item_dependencies')` shows a holder. An add that waits the full 10 s is refused with HTTP 503 and code `busy`, and writes nothing.

`005` adds the `WorkItemLinks` table: one row per external URL attached to a work item, with a `Kind` (`pr`, `branch` or `other`). `WorkItemId` references `WorkItems.Id` with `ON DELETE CASCADE`. `Url` holds up to 2048 characters, which is more than an index key can hold under `utf8mb4`, and the database's default collation would compare it ignoring case; so the API stores `UrlHash`, the SHA-256 of the URL in lowercase hex, and both the unique key (`WorkItemId`, `Kind`, `UrlHash`) and the lookup by URL (`IX_WorkItemLinks_UrlHash`) use the hash. A row whose `UrlHash` does not match its `Url` is never found by URL, so do not insert links by hand. It is safe to re-run.

The API starts every link add and remove with the `UPDATE` that stamps the work item's `UpdatedAtUtc`, inside a transaction. That statement holds the item's row until the transaction ends, so the link writes of one item run one at a time and two identical adds never meet on the unique key: the second waits, finds the first one's row and inserts nothing (its stamp is rolled back). Adding a comment writes in the same order, stamp first and then the `INSERT`; in the other order two writers of one item deadlocked (error 1213) on the shared lock the `INSERT` takes for its foreign key.

`006` changes `WorkItemComments`: `Author` grows from 80 to 320 characters (the length of a claimant name), and two nullable columns are added. `AuthorUserId` is the `AppUsers.Id` behind the token that wrote the comment; it has no foreign key, so a comment outlives its author, and it is `NULL` for every existing comment and for comments written without a token. `IdempotencyKeyHash` is the SHA-256, in lowercase hex, of the author's user id and the caller's idempotency key (the key itself is not stored, and a hash compares exactly where the default collation would ignore case); the unique key (`WorkItemId`, `IdempotencyKeyHash`) allows one comment per item and key and any number without a key (`NULL`). Existing rows are not rewritten: they keep their ids, authors and timestamps. It is safe to re-run.

The API writes a comment like a link: the `UPDATE` that stamps the item comes first and holds the item's row, then it looks for the idempotency key, reads the time of the item's latest comment, and inserts. Concurrent adds with one key therefore never meet on the unique key: the later ones wait, find the first one's row and insert nothing. Because comment writes of one item run one at a time, each comment also gets a `CreatedAtUtc` later than the latest one already stored (that time plus one microsecond when the writer's clock is not ahead of it, with the item stamped again to match), which is what lets `list_work_item_comments` page by (`CreatedAtUtc`, `Id`) without a new comment landing behind a reader's cursor. Comments inserted by hand are not covered by that rule, and one stored with a time far in the future pulls the later comments of its item, and the item's `UpdatedAtUtc`, forward with it.

`007` adds three nullable columns to `Projects`; existing projects keep their rows and get `NULL` in all three. `RepositoryUrl` (up to 2048 characters) is the http, https or ssh URL of the repository the project's work is done in, and `DefaultBranch` (up to 255 characters) the branch of it that work starts from. The API validates both on write (no credentials in the URL, a branch name git accepts), so do not set them by hand to values it would refuse. `IdempotencyKeyHash` is the SHA-256, in lowercase hex, of the creating caller's user id and idempotency key, as for comments; the unique key `IX_Projects_IdempotencyKeyHash` allows one project per key and any number without one (`NULL`). It is safe to re-run.

A project has no parent row to lock, so the API serialises creates that carry an idempotency key with the MariaDB named lock `kanban_project_creates` (`GET_LOCK`, 10 s timeout), the same way as dependency adds: the look-up of the key and the `INSERT` of two concurrent creates cannot interleave, the later one finds the first one's project, and the unique key is never hit. A keyed create that waits the full 10 s is refused with HTTP 503 and code `busy`, and writes nothing. Creates without a key take no lock.

`001` creates the EF migrations history table and the initial Kanban schema:

- `Projects`
- `AppUsers`
- `Epics`
- `EpicDocuments`
- `WorkItems`
- `WorkItemComments`
- `PersonalAccessTokens`

MariaDB note: `MySql.EntityFrameworkCore` takes the migration lock with `GET_LOCK('__EFMigrationsLock',-1)`, which MariaDB rejects, and reads the result as a MySQL `BIGINT` while MariaDB returns `INT`. `MariaDbMigrationLockInterceptor` rewrites that one statement to a positive timeout and widens the result. It is registered on the API's `DbContext` and on the design-time factory (`dotnet ef database update`). The provider also ignores the `GET_LOCK` result, so it would migrate after a timed-out wait (result `0`) while another session still held the lock; the interceptor throws a `TimeoutException` for any result other than `1`. The lock wait (default 600 s) is kept 60 s below the migration command timeout (`Database:MigrationCommandTimeoutSeconds`, default 660 s): if the client timed out first, MariaDB would keep the lock on the killed connection and the API would hang. `StartupMigrationTests` covers the rewrite, the timeout result and the command timeout through the EF Core pipeline (sync and async) with a faked connection. Against a real `mariadb:11.4` server it was also checked by hand: with another session holding the lock for 75 s the API waited and then migrated, and with the lock held past the wait it exited with the `TimeoutException` and changed no schema.

## Data Import

After the schema exists, use the SQLite to MariaDB data migration utility documented in [sqlite-to-mariadb.md](sqlite-to-mariadb.md).
