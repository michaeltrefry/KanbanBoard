# Production deployment

This repo deploys to a Linode server with GitHub Actions, GHCR, Docker Compose, and Caddy.

Production hostnames:

- Web UI and API: `https://kanban.trefry.net`
- MCP server: `https://kanban-mcp.trefry.net/mcp`

## How deployment works

1. Pull requests and pushes run `dotnet restore`, `dotnet build`, and Docker image builds.
2. Pushes or merges to `main` publish two images to GHCR:
   - `ghcr.io/michaeltrefry/kanbanboard-api`
   - `ghcr.io/michaeltrefry/kanbanboard-mcp`
3. GitHub Actions connects to the Linode over SSH.
4. The workflow copies `deploy/docker-compose.prod.yml` and `deploy/Caddyfile` to `/opt/kanban-board`.
5. The Linode pulls the new images and restarts the stack.
6. Caddy obtains and renews public TLS certificates automatically.

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

Create the deployment directory and data directory:

```bash
sudo mkdir -p /opt/kanban-board/data
sudo chown -R "$USER":"$USER" /opt/kanban-board
```

Open inbound ports:

- SSH: `22/tcp`, or your custom SSH port.
- HTTP: `80/tcp`, required for Caddy/Let's Encrypt.
- HTTPS: `443/tcp`, required for the app and MCP endpoint.

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

## GitHub secrets

In GitHub, go to the repository, then Settings -> Secrets and variables -> Actions -> New repository secret.

Required:

- `LINODE_HOST`: Linode IPv4 address or DNS name.
- `LINODE_USER`: SSH user on the Linode.
- `LINODE_SSH_KEY`: private key for the deploy user.
- `KANBAN_TLS_EMAIL`: email Caddy/Let's Encrypt can use for certificate notices.

Optional:

- `LINODE_SSH_PORT`: SSH port. Defaults to `22` if omitted.
- `GHCR_USERNAME`: GitHub username for pulling private GHCR images from the Linode.
- `GHCR_READ_TOKEN`: classic GitHub PAT with `read:packages`, only needed if the GHCR packages stay private.

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

Both should return the Linode IP before the first TLS certificate issuance can succeed.

## First deployment

After DNS, Docker, firewall rules, and GitHub secrets are ready:

1. Merge or push to `main`, or run the workflow manually from GitHub Actions.
2. Watch the `Build and deploy` workflow.
3. On the Linode, check containers:

```bash
cd /opt/kanban-board
docker compose -f docker-compose.prod.yml ps
docker compose -f docker-compose.prod.yml logs -f caddy
```

4. Open:
   - `https://kanban.trefry.net`
   - `https://kanban-mcp.trefry.net/mcp`

## Manual rollback

Each deployment writes the exact image tags into `/opt/kanban-board/.env`. To roll back manually, edit the image tags to an earlier `sha-...` tag and restart:

```bash
cd /opt/kanban-board
nano .env
docker compose -f docker-compose.prod.yml pull
docker compose -f docker-compose.prod.yml up -d
```

SQLite data lives in `/opt/kanban-board/data` and is not replaced by image updates.
