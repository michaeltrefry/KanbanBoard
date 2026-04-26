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

Before switching production from SQLite to MariaDB, create the MariaDB schema with [../deploy/sql/001_initial_mariadb_schema.sql](../deploy/sql/001_initial_mariadb_schema.sql), then preserve existing data with the migration utility documented in [sqlite-to-mariadb.md](sqlite-to-mariadb.md). The utility can connect directly to MariaDB or open a temporary SSH tunnel when the database is only reachable from the host.

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

MariaDB data lives in the external MariaDB instance. The application container no longer stores the primary database in `/opt/kanban-board/data`.
