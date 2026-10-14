# Production deployment

This repo deploys the Kanban containers to a Linode server with GitHub Actions, GHCR, and Docker Compose. Caddy is managed separately on the host.

Production hostnames:

- Web UI and API: `https://kanban.trefry.net`
- MCP server: `https://kanban-mcp.trefry.net/mcp`

Public MCP access requires `Authorization: Bearer <personal-access-token>`. The MCP container validates PATs through the API over the internal Docker network using `InternalApi__SharedSecret`; the MCP service is not published to localhost by this Compose file.

## How deployment works

1. Pull requests and pushes run `dotnet restore`, `dotnet build`, and Docker image builds.
2. Pushes or merges to `main` publish two images to GHCR:
   - `ghcr.io/michaeltrefry/kanbanboard-api`
   - `ghcr.io/michaeltrefry/kanbanboard-mcp`
3. GitHub Actions connects to the Linode over SSH.
4. The workflow copies `deploy/docker-compose.prod.yml` to `/opt/kanban-board`.
5. The workflow writes image tags and GitHub-managed production settings to `/opt/kanban-board/.env.release`.
6. Local `.env` files remain ignored by git and are for local development only.
7. The Linode pulls the new images and restarts the stack.
8. The workflow ensures the external Docker network `trefry-network` exists before Compose starts the stack.
9. The host-managed Caddy instance proxies `https://kanban.trefry.net` to the API container on localhost or to `kanban-api:8080` on `trefry-network`.

GitHub currently says Container registry storage and bandwidth are free, but says it will provide at least one month of notice if that policy changes. Public container images can be pulled anonymously. See GitHub's docs for [GitHub Packages billing](https://docs.github.com/en/billing/concepts/product-billing/github-packages) and [working with the Container registry](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry).

## Linode setup

These commands assume Ubuntu and a user with `sudo`. If the Linode is Debian, use Docker's Debian repository URL in place of the Ubuntu URL below.

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates curl gnupg

sudo install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg \
  | sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
sudo chmod a+r /etc/apt/keyrings/docker.gpg

. /etc/os-release
echo \
  "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu ${VERSION_CODENAME} stable" \
  | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null

sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo systemctl enable --now docker
```

Create the deployment directory:

```bash
sudo mkdir -p /opt/kanban-board
sudo chown -R "$USER":"$USER" /opt/kanban-board
```

The production Compose stack binds the API to `127.0.0.1:8080` by default and the MCP server to `127.0.0.1:3000` by default. Configure the host-managed Caddy sites to reverse proxy to those loopback ports:

```caddyfile
kanban.trefry.net {
    reverse_proxy 127.0.0.1:8080
}

kanban-mcp.trefry.net {
    reverse_proxy 127.0.0.1:3000
}
```

If either port is already taken, set `KANBAN_API_HTTP_PORT` or `KANBAN_MCP_HTTP_PORT` in the GitHub workflow release env or directly in `/opt/kanban-board/.env.release`, then point Caddy at the same localhost port.

The API trusts the forwarded host/proto headers from the reverse proxy so OIDC redirects are generated for `https://kanban.trefry.net` instead of the container's internal HTTP endpoint. Caddy's `reverse_proxy` sends the required `X-Forwarded-Host` and `X-Forwarded-Proto` headers by default.

The production Compose stack also joins both containers to the external Docker network `trefry-network`. The deploy workflow creates it automatically if it does not already exist. If Caddy is running in Docker on the same host, attach that Caddy container to `trefry-network` and proxy the app and MCP server to:

```text
kanban-api:8080
kanban-mcp:3000
```

For local Docker Compose development only, create a repo-root `.env`; Compose reads this file automatically and passes the values into `kanban-api`:

```bash
ConnectionStrings__Kanban="Server=127.0.0.1;Port=3306;Database=kanban;User ID=kanban;Password=SECRET;"
Database__CommandTimeoutSeconds=30

Auth__Enabled=true
Auth__Authority=https://identity.trefry.net/realms/YOUR_REALM
Auth__ClientId=kanban-board
Auth__ClientSecret=KEYCLOAK_CLIENT_SECRET
Auth__RequireHttpsMetadata=true
Auth__AdminSubjects=KEYCLOAK_USER_ID_OF_EACH_ADMIN

PersonalAccessTokens__Enabled=true
PersonalAccessTokens__EncryptionKey=32_BYTE_BASE64_OR_64_HEX_KEY
PersonalAccessTokens__TokenPrefix=kbp

InternalApi__SharedSecret=RANDOM_32_PLUS_CHARACTER_SHARED_SECRET
McpAuthentication__RequirePersonalAccessToken=true
McpAuthentication__ValidationCacheSeconds=30
```

For `dotnet run`, export the same variables in your shell before starting the API because ASP.NET Core does not read `.env` files by itself.

For Keycloak, configure the `kanban-board` OIDC client as a confidential client by turning **Client authentication** on. Use these URLs:

- Valid redirect URI: `https://kanban.trefry.net/signin-oidc`
- Valid post logout redirect URI: `https://kanban.trefry.net/signout-callback-oidc`
- Web origin: `https://kanban.trefry.net`

Production deployment uses GitHub-managed secrets and writes them into `/opt/kanban-board/.env.release` on the server during deploy. Required production auth secrets:

- `KANBAN_MARIADB_CONNECTION`: MariaDB connection string for the `kanban` database.
- `KANBAN_AUTH_AUTHORITY`: Keycloak realm authority, for example `https://identity.trefry.net/realms/YOUR_REALM`.
- `KANBAN_AUTH_CLIENT_SECRET`: Keycloak client secret for the `kanban-board` confidential client.
- `KANBAN_PAT_ENCRYPTION_KEY`: 32-byte / 256-bit PAT encryption key encoded as base64 or 64 hex characters.
- `KANBAN_INTERNAL_API_SHARED_SECRET`: random 32+ character secret shared only between `kanban-api` and `kanban-mcp`.

Do not commit local `.env` files. They are ignored and should stay on your machine.

The authenticated web login and browser API use cookie sessions. MCP clients use Personal Access Tokens from the user settings page and send them as bearer tokens to `/mcp`. The MCP server validates PATs through the API's internal validation endpoint, caches successful validation briefly, and forwards tool calls with the internal shared secret rather than exposing a public API bypass.

Before switching production from SQLite to MariaDB, create the MariaDB schema with [../deploy/sql/001_initial_mariadb_schema.sql](../deploy/sql/001_initial_mariadb_schema.sql) and the later numbered scripts in `deploy/sql` (see [mariadb-schema.md](mariadb-schema.md)), then preserve existing data with the migration utility documented in [sqlite-to-mariadb.md](sqlite-to-mariadb.md). The utility can connect directly to MariaDB or open a temporary SSH tunnel when the database is only reachable from the host.

### Schema changes on deploy

The API applies pending EF Core migrations itself when it starts (`Database:MigrateOnStartup`, default `true`; env `Database__MigrateOnStartup`). On each start it compares `__EFMigrationsHistory` with the migrations in the image, logs the ids it applies (`Applying N database migration(s): ...` / `Applied database migration(s): ...`), then starts serving. There is no manual pre-merge schema step: merging a change with a new migration to `main` deploys the image, and the new container migrates the schema before it accepts requests.

Only one instance migrates at a time: each takes the MariaDB named lock `__EFMigrationsLock` first. A starting API waits for it for `Database:MigrationCommandTimeoutSeconds` (default `660`, allowed `90`-`7200`; env `Database__MigrationCommandTimeoutSeconds`) minus 60, so 600 s by default. Migration commands run under that longer command timeout, not `Database:CommandTimeoutSeconds`. If the lock is still held when the wait ends, the API exits with `TimeoutException: Could not acquire the '__EFMigrationsLock' migration lock ...` and applies nothing. Find the holder with `SELECT IS_USED_LOCK('__EFMigrationsLock')` (a connection id for `SHOW PROCESSLIST` / `KILL`).

If a migration fails, the API exits with the exception instead of serving against a partly migrated schema. Compose (`restart: unless-stopped`) will keep restarting it, so check `docker compose -f docker-compose.prod.yml logs kanban-api`. MariaDB DDL is not transactional, so a failed migration can leave its earlier statements applied; fix the schema by hand (or with the matching `deploy/sql` script) before restarting.

The database user in `KANBAN_MARIADB_CONNECTION` must be able to change the schema: `ALTER` (and `CREATE`, `INDEX`, `DROP`, `REFERENCES` for future migrations that add tables, indexes or foreign keys) on the `kanban` schema, plus `SELECT`/`INSERT` on `__EFMigrationsHistory`. The `GRANT ALL PRIVILEGES ON kanban.*` in [mariadb-schema.md](mariadb-schema.md) covers this. Migrations take a `GET_LOCK('__EFMigrationsLock', ...)` named lock, which needs no extra privilege.

The numbered scripts in `deploy/sql` remain for bootstrapping a new database and for applying a change by hand. Each one records its migration id in `__EFMigrationsHistory`, so the API skips migrations an operator has already applied. To manage the schema only by hand, set the Actions variable `KANBAN_DB_MIGRATE_ON_STARTUP` to `false` (the workflow writes it to `/opt/kanban-board/.env.release` as `Database__MigrateOnStartup`; unset means `true`) and apply each new script before deploying the code that needs it.

For the PAT scope change (sc-25712) the API adds `PersonalAccessTokens.Scope` on its first start; existing tokens become `full`. Running `deploy/sql/002_personal_access_token_scope.sql` beforehand is optional and harmless. To verify after deploy:

```bash
mariadb -h HOST -P 3306 -u kanban -p kanban -e "SELECT MigrationId FROM __EFMigrationsHistory"
# lists 20261010161942_AddPersonalAccessTokenScope
```

For the work item claim change (sc-25859) the API adds four nullable claim columns to `WorkItems` on its first start (`20261012211402_AddWorkItemClaim`); existing items start unclaimed. `deploy/sql/003_work_item_claim.sql` is the manual equivalent.

For the work item dependency change (sc-25860) the API creates the `WorkItemDependencies` table on its first start (`20261012214428_AddWorkItemDependencies`); existing items start with no dependencies. `deploy/sql/004_work_item_dependencies.sql` is the manual equivalent.

For the work item link change (sc-25862) the API creates the `WorkItemLinks` table on its first start (`20261012225054_AddWorkItemLinks`); existing items start with no links. `deploy/sql/005_work_item_links.sql` is the manual equivalent.

For the comment record change (sc-25863) the API alters `WorkItemComments` on its first start (`20261012233418_AddWorkItemCommentAuthorAndIdempotencyKey`): `Author` widens to 320 characters, and the nullable `AuthorUserId` and `IdempotencyKeyHash` columns and a unique index on (`WorkItemId`, `IdempotencyKeyHash`) are added. Existing comments keep their ids, authors and timestamps and get `NULL` in both new columns. `deploy/sql/006_work_item_comment_author_and_idempotency_key.sql` is the manual equivalent.

For the project target change (sc-25864) the API alters `Projects` on its first start (`20261013000421_AddProjectRepositoryAndIdempotencyKey`): the nullable `RepositoryUrl`, `DefaultBranch` and `IdempotencyKeyHash` columns and a unique index on `IdempotencyKeyHash` are added. Existing projects keep their rows and get `NULL` in all three; set a project's repository afterwards with the MCP tool `update_project`. `deploy/sql/007_project_repository_and_idempotency_key.sql` is the manual equivalent.

For the work item update change (sc-25865) the API alters `WorkItems` on its first start (`20261013004410_AddWorkItemVersionAndLongerLabels`): `Labels` widens from 240 to 4000 characters and `Version` (`bigint NOT NULL DEFAULT 1`) is added. Existing items keep their labels and get version 1. `deploy/sql/008_work_item_version_and_longer_labels.sql` is the manual equivalent.

For the change feed (sc-25866) the API creates the `WorkItemChanges` table on its first start (`20261013013744_AddWorkItemChanges`). Existing data is not touched and gets no rows: the feed holds the writes made from then on, and it is never pruned. `deploy/sql/009_work_item_changes.sql` is the manual equivalent. From this release every write to a work item also takes the MariaDB named lock `kanban_work_item_changes` for the moment it records its entry (see [mariadb-schema.md](mariadb-schema.md)).

For the plan import and the idempotent creates (sc-25867) the API alters the schema on its first start (`20261013132811_AddCreateIdempotencyKeysAndEpicPlanImports`): `WorkItems`, `Epics` and `EpicDocuments` each get a nullable `IdempotencyKeyHash` column with a unique index, and the `EpicPlanImports` table is created. Existing rows keep `NULL` and are not rewritten. Building the unique index on `WorkItems` reads every work item once. `deploy/sql/010_create_idempotency_keys_and_epic_plan_imports.sql` is the manual equivalent. The acceptance script `tools/KanbanBoard.FactoryAcceptance` creates its own scratch projects, runs the eight acceptance tests of the epic and prints one PASS, FAIL or SKIP line for each; every project it creates is left archived. See [Acceptance run](#acceptance-run) for its variables.

For disabling service identities (sc-26038) the API adds a nullable `DisabledAtUtc` column to `AppUsers` on its first start (`20261014110144_AddAppUserDisabledAt`). Every existing user, person or service identity, keeps `NULL` (enabled); no row is rewritten. `deploy/sql/011_app_user_disabled_at.sql` is the manual equivalent.

### Factory service identity

The factory orchestrator signs in to MCP as its own identity, not as a person. Create that identity on the server, after the release that contains sc-25859 is deployed, with the API's own command. It runs in the API container, so it uses the same database and PAT encryption key as the running API, and it needs no browser or OIDC login:

```bash
cd /opt/kanban-board
set -a
. ./.env.release
set +a
docker compose -f docker-compose.prod.yml exec \
  -e Logging__LogLevel__Default=Warning \
  kanban-api dotnet KanbanBoard.Api.dll create-service-identity --name factory
```

It prints the identity and two tokens, once:

```text
Service identity 'factory' (GUID) created.
full-scope token: kbp_...
read-scope token: kbp_...
Store both now. They are not shown again.
```

Give the orchestrator the full-scope token (and the read-scope token where it only reads) as `Authorization: Bearer <token>` on `https://kanban-mcp.trefry.net/mcp`. Items it claims show `factory` as `claimant`. The command exits without serving HTTP; the output of an `exec` is not written to the container log.

- Running it again with the same name (case-insensitive) adds a new token pair to the same identity and leaves the old tokens valid.
- Add `--rotate` to revoke the identity's existing tokens once the new pair is issued; if issuing fails, the existing tokens stay valid. MCP caches a validated token for `McpAuthentication__ValidationCacheSeconds` (default 30), so the MCP server can still accept a revoked token for that long; the API does not: it looks the forwarded token up on every call and refuses a revoked or expired one `scope_denied`, read or write.
- `PersonalAccessTokens__Enabled` must be `true`; otherwise the command exits with code 1 and changes nothing: no identity, no token, and with `--rotate` no revocation.
- A disabled identity (see [Admins](#admins)) is refused: the command prints `Service identity 'NAME' is disabled: it gets no new token until an admin enables it.`, exits with code 1 and changes nothing (no token, and with `--rotate` no revocation). It has no flag to enable an identity; enable it on the Admin page.

### Admins

An admin sees an **Admin** button in the web UI, next to **Settings**. The Admin page lists the service identities (name, created, active tokens, last used, disabled), creates one by name, disables and enables one, and issues a token for an enabled one (name, scope `read` or `full`, optional expiry); the plaintext is shown once (`Cache-Control: no-store`). People keep managing their own tokens on the Settings page; the Admin page never issues a token for a person.

- A name follows the rules of `create-service-identity`: trimmed, 1 to 120 characters, one identity per name whatever its case (on MariaDB whatever its accents too). A name that exists is refused with 409 `{"code":"identity_exists"}`, whose message names the existing identity and its id, and nothing changes; `create-service-identity --name` with that name then adds tokens to the identity the page created.
- **Disable** revokes every token of the identity and marks it disabled, in one transaction. The API refuses its tokens on their next call (`scope_denied`); the MCP server's own validation cache can still let a token in for up to `McpAuthentication__ValidationCacheSeconds` (30 s), but every tool call it makes is refused by the API. No token is issued for a disabled identity (409 `{"code":"identity_disabled"}`, from the page and from the command), and a token of a disabled identity is refused even if it was not revoked. Disabling a disabled identity changes nothing.
- **Enable** lets tokens be issued again. The tokens revoked by the disable stay revoked: issue new ones.
- **Tokens** of an identity lists each token's name, scope, created, expires, last used and state (`revoked`, else `expired`, else `active`), never its secret (`GET /api/admin/service-identities/{id}/tokens`).
- **Revoke** asks for confirmation, then revokes the token (`DELETE .../tokens/{tokenId}`, 204; revoking a revoked token succeeds and keeps its first revocation time). It is refused through MCP as a disabled identity's tokens are (above).
- **Rotate** issues a replacement with the same name and scope and an optional new expiry (none when left empty), shows its plaintext once, and revokes the old token only once the replacement is stored, in one transaction (`POST .../tokens/{tokenId}/rotate` `{"expiresAtUtc":null}`, 201 `{token, plaintextToken, revokedTokenId}`). An expired token can be rotated; a revoked one is refused with 409 `{"code":"token_revoked"}`. A refused or failed rotate (`tokens_disabled`, `invalid_argument`, `identity_disabled`, `not_found`) writes nothing and the old token keeps working. Of concurrent rotates of one token, one succeeds and the rest get `token_revoked`. A token id that is not the identity's is `not_found`.
- An identity is never deleted: comments, claims and change-feed entries refer to it by id.

Admins are set with `Auth__AdminSubjects` (on the hosted instance, the GitHub Actions variable or secret `KANBAN_ADMIN_SUBJECTS`, which the deploy writes into `.env.release`): the OIDC subject (`sub`) of each admin, separated by commas, semicolons or spaces. In Keycloak the subject is the user's **ID** (Users -> the user -> Details), a UUID; a signed-in user also sees their own subject on the Settings page. The match is exact and case-sensitive. The subject is matched rather than the email, which a user may be able to change or leave unverified at the provider.

- Unset or empty: nobody is an admin, the Admin button is not shown, and every `/api/admin` endpoint answers 403 `{"code":"admin_required"}`.
- Only a signed-in browser session counts. A request of the MCP server (internal secret, with or without a forwarded token) and a request carrying an `Authorization` header are refused with the same 403, whoever they are for.
- With `Auth__Enabled=false` there is no signed-in user, so nobody is an admin.
- Issuing needs `PersonalAccessTokens__Enabled=true`; otherwise it is refused with 503 `{"code":"tokens_disabled"}`.
- A change to the list takes effect when the API restarts (on the hosted instance: the next deploy).

### Acceptance run

After a deploy of the factory tools, run the acceptance script from a checkout of the deployed commit, with both tokens of the service identity and its name:

```bash
KANBAN_MCP_URL=https://kanban-mcp.trefry.net/mcp \
KANBAN_FACTORY_TOKEN=kbp_... \
KANBAN_FACTORY_READ_TOKEN=kbp_... \
KANBAN_FACTORY_IDENTITY_NAME=factory \
dotnet run --project tools/KanbanBoard.FactoryAcceptance
```

- `KANBAN_MCP_URL`, `KANBAN_FACTORY_TOKEN` and `KANBAN_FACTORY_IDENTITY_NAME` are required; without one of them the script prints a usage message and exits with code 2 before it calls anything.
- `KANBAN_FACTORY_IDENTITY_NAME` is the name given to `create-service-identity --name`. The script asserts that the token's claims, comments and change-feed entries carry that name: run with a person's token from the settings page, tests 1, 3 and 4 fail.
- Without `KANBAN_FACTORY_READ_TOKEN` test 7 is skipped, the last line is `ACCEPTANCE INCOMPLETE` and the exit code is 3. Only `ACCEPTANCE PASSED` with exit code 0 is an acceptance: all eight tests ran and every check passed. `ACCEPTANCE FAILED` exits with code 1.
- The script writes only into scratch projects it creates and leaves archived (keys `ACC`, `ACI`, `ACE` plus 20 hex characters); it can be run again.

The contract the script checks is [mcp-contract.md](mcp-contract.md).

Open inbound ports:

- SSH: `22/tcp`, or your custom SSH port.
- HTTP: `80/tcp`, if your host Caddy uses HTTP challenge/redirects.
- HTTPS: `443/tcp`, required for the app.

If you use `ufw`:

```bash
sudo ufw allow OpenSSH
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw enable
sudo ufw status
```

Also check any Linode Cloud Firewall attached to the instance.

## Deploy user and SSH key

Create a deploy key locally:

```bash
ssh-keygen -t ed25519 -C "kanbanboard-github-actions" -f ./kanbanboard_linode_deploy
```

Add the public key to the Linode user's `~/.ssh/authorized_keys`:

```bash
ssh-copy-id -i ./kanbanboard_linode_deploy.pub user@YOUR_LINODE_IP
```

The private key content goes into the GitHub secret `LINODE_SSH_KEY`.

## GitHub Actions settings

In GitHub, go to the repository, then Settings -> Secrets and variables -> Actions.

Sensitive values must be Actions secrets. Non-sensitive deployment settings can be Actions variables or secrets. If you use the `production` environment, put them on that environment or at the repository level.

Required:

- `LINODE_HOST`: Linode IPv4 address or DNS name. Variable or secret.
- `LINODE_USER`: SSH user on the Linode. Variable or secret.
- `LINODE_SSH_KEY`: full private key for the deploy user, including the `BEGIN` and `END` lines. Secret.
- `KANBAN_MARIADB_CONNECTION`: MariaDB connection string for the `kanban` database. Secret.
- `KANBAN_AUTH_AUTHORITY`: Keycloak realm authority, for example `https://identity.trefry.net/realms/YOUR_REALM`. Variable or secret.
- `KANBAN_AUTH_CLIENT_SECRET`: Keycloak client secret for the `kanban-board` confidential client. Secret.
- `KANBAN_PAT_ENCRYPTION_KEY`: 32-byte / 256-bit PAT encryption key encoded as base64 or 64 hex characters. Secret.
- `KANBAN_INTERNAL_API_SHARED_SECRET`: random 32+ character secret shared only between `kanban-api` and `kanban-mcp`. Secret.

Optional:

- `LINODE_SSH_PORT`: SSH port. Defaults to `22` if omitted. Variable or secret.
- `KANBAN_ADMIN_SUBJECTS`: the admins of the Admin page (service identities and their tokens), written to `Auth__AdminSubjects`. Variable or secret. See [Admins](#admins). Unset or empty: nobody is an admin and the page is not shown.
- `KANBAN_DB_MIGRATE_ON_STARTUP`: `false` stops the API from applying EF Core migrations on startup. Defaults to `true` if omitted. Only the lowercase values `true` and `false` are accepted; the deploy fails before touching the server on any other value. Variable.
- `LINODE_SSH_KEY_B64`: base64-encoded full private key. Secret. Optional alternative to `LINODE_SSH_KEY` if multiline secrets are troublesome.
- `LINODE_SSH_KEY_PASSPHRASE`: passphrase for `LINODE_SSH_KEY`, only needed if the private key is passphrase-protected. Secret.
- `GHCR_USERNAME`: GitHub username for pulling private GHCR images from the Linode. Variable or secret.
- `GHCR_READ_TOKEN`: classic GitHub PAT with `read:packages`, only needed if the GHCR packages stay private. Secret.

The workflow publishes to GHCR using the built-in `GITHUB_TOKEN`, so no write token is needed for publishing.

## GHCR package visibility

The simplest setup is to make the two GHCR packages public after the first successful publish:

1. Open the repository on GitHub.
2. Go to Packages.
3. Open each package:
   - `kanbanboard-api`
   - `kanbanboard-mcp`
4. Open Package settings.
5. Change visibility to public.

If you prefer private packages, create `GHCR_USERNAME` and `GHCR_READ_TOKEN` secrets. The workflow copies the read token to the Linode only long enough to run `docker login`, then removes the token file.

## GoDaddy DNS setup

GoDaddy's DNS docs note that if the domain uses GoDaddy nameservers, DNS records are managed in the GoDaddy account. If the nameservers point somewhere else, make these records wherever DNS is actually hosted. See GoDaddy's docs for [managing DNS records](https://www.godaddy.com/help/manage-dns-records-680) and [creating a subdomain](https://www.godaddy.com/help/create-a-subdomain-4080).

Add these records for `trefry.net`:

| Type | Name | Value | TTL |
| --- | --- | --- | --- |
| A | `kanban` | Linode IPv4 address | Default or 600 seconds |
| A | `kanban-mcp` | Linode IPv4 address | Default or 600 seconds |

If the Linode has IPv6 and you want IPv6 support too, add matching `AAAA` records using the Linode IPv6 address.

Check propagation:

```bash
dig +short kanban.trefry.net
dig +short kanban-mcp.trefry.net
```

Both should return the Linode IP before public host routing can work.

## First deployment

After DNS, Docker, firewall rules, and GitHub secrets are ready:

1. Merge or push to `main`, or run the workflow manually from GitHub Actions.
2. Watch the `Build and deploy` workflow.
3. On the Linode, check containers:

```bash
cd /opt/kanban-board
set -a
. ./.env.release
set +a
docker compose -f docker-compose.prod.yml ps
docker compose -f docker-compose.prod.yml logs -f kanban-api
```

4. Open:
   - `https://kanban.trefry.net`
   - `https://kanban-mcp.trefry.net/mcp`, which should return 401 without `Authorization: Bearer <personal-access-token>`.

## Manual rollback

Each deployment writes the exact image tags and GitHub-managed production settings into `/opt/kanban-board/.env.release`. To roll back manually, edit the image tags to an earlier `sha-...` tag and restart.

```bash
cd /opt/kanban-board
nano .env.release
set -a
. ./.env.release
set +a
docker compose -f docker-compose.prod.yml pull
docker compose -f docker-compose.prod.yml up -d
```

A rollback changes the images, not the database: the schema stays as the newest image left it, and no migration is undone. An image from before the factory work (before the change feed of sc-25866 and the `version` of sc-25865) starts against that schema and works: create, comment, move and delete all succeed. But its writes are not the writes the newer tools promise:

- **They add no entry to the change feed.** A caller that follows `list_changes` does not see what the old image wrote, then or later: the entries are never backfilled. After rolling forward again, such a caller must read the board itself (`list_work_items`) to find the state it missed.
- **They do not advance `version`.** An `update_work_item` or `move_work_item` with `expected_version`, made after rolling forward, succeeds against an item the old image changed in between: for the writes of that window the guard is unsound.

So while an older image runs, stop the orchestrator, and after rolling forward have it take a new cursor (`list_changes` without `cursor`) and re-read its items before it writes.

**Columns with duplicate `order` values are not repaired at deploy.** Before the release with sc-25868, concurrent moves into one column could leave two of its items with the same `order`. No migration renumbers them. The first `move_work_item` (or drag in the web UI) into or within such a column renumbers all of its items to 0, 1, 2, …, ordered by `order` and then by creation time; from then on its values are distinct and stay so. To find such columns: `SELECT ProjectId, Status, `Order`, COUNT(*) FROM WorkItems GROUP BY ProjectId, Status, `Order` HAVING COUNT(*) > 1`.

The same holds once at the first deploy: **the change feed starts empty.** Writes made before the release with sc-25866 have no entries, so `from_start: true` shows the history from that deploy on, not the board's.

MariaDB data lives in the external MariaDB instance. The application container no longer stores the primary database in `/opt/kanban-board/data`.
