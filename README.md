# Paperless MCP Server

[![CI](https://github.com/mKenfenheuer/paperless-mcp-server/actions/workflows/ci.yml/badge.svg)](https://github.com/mKenfenheuer/paperless-mcp-server/actions/workflows/ci.yml)
[![Docker image](https://github.com/mKenfenheuer/paperless-mcp-server/actions/workflows/docker.yml/badge.svg)](https://github.com/mKenfenheuer/paperless-mcp-server/actions/workflows/docker.yml)

A remote [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server for [Paperless-ngx](https://docs.paperless-ngx.com).
It lets AI assistants such as Claude search, read, upload and organize documents in your Paperless instance.

- **Streamable HTTP transport** with built-in **OAuth 2.1** (dynamic client registration, PKCE, refresh tokens), as required by the MCP authorization spec. Clients such as Claude, VS Code and Cursor connect with a URL and a sign-in page; no tokens in config files.
- **Two sign-in modes:**
  - **Open:** users enter their Paperless URL and API token when connecting. One MCP server can serve any number of Paperless instances.
  - **Preconfigured:** you set `PAPERLESS_URL`, users sign in with their Paperless **username and password** (or an API token if they use 2FA).
- **Stateless:** no database. Credentials are encrypted (AES-256-GCM) into the tokens issued to the client, and nothing is stored on the server.
- **18 tools** for documents, metadata, notes and uploads. Tags, correspondents, document types, storage paths and custom fields can be passed by name.

## Quickstart (Docker Compose)

You need Docker and a reverse proxy that serves the container over HTTPS (Caddy, Traefik, nginx, …). Most MCP clients only connect to HTTPS URLs.

1. Create a directory with a `docker-compose.yml`:

   ```yaml
   services:
     paperless-mcp:
       image: ghcr.io/mkenfenheuer/paperless-mcp-server:latest
       container_name: paperless-mcp
       restart: unless-stopped
       ports:
         - "8080:8080"
       environment:
         SECRET_KEY: ${SECRET_KEY}
         # Recommended: the public HTTPS URL (otherwise derived from X-Forwarded-Proto / X-Forwarded-Host)
         PUBLIC_URL: https://paperless-mcp.example.com
         # Optional: preconfigure your Paperless instance (users sign in with username + password)
         # PAPERLESS_URL: https://paperless.example.com
   ```

2. Generate a secret and start the container:

   ```sh
   echo "SECRET_KEY=$(openssl rand -base64 48)" > .env
   docker compose up -d
   ```

3. Point your reverse proxy at port `8080` for `paperless-mcp.example.com`. With Caddy, for example:

   ```
   paperless-mcp.example.com {
       reverse_proxy localhost:8080
   }
   ```

   With Traefik, use labels on the service instead of publishing a port:

   ```yaml
       labels:
         - traefik.enable=true
         - traefik.http.routers.paperless-mcp.rule=Host(`paperless-mcp.example.com`)
         - traefik.http.routers.paperless-mcp.entrypoints=websecure
         - traefik.http.routers.paperless-mcp.tls.certresolver=letsencrypt
         - traefik.http.services.paperless-mcp.loadbalancer.server.port=8080
   ```

   The server trusts `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` from the proxy by default.

4. Add `https://paperless-mcp.example.com/mcp` as an MCP server in your client (see [Connecting clients](#connecting-clients)). A browser window opens where you sign in to Paperless and allow access.

To run the MCP server next to Paperless in the same Compose project, add the service to your existing Paperless `docker-compose.yml` and set `PAPERLESS_URL: http://webserver:8000` (the internal Paperless service name and port).

## Configuration

All settings are environment variables.

| Variable | Default | Description |
| --- | --- | --- |
| `PUBLIC_URL` | derived | Public URL clients use to reach the server, without a path. The MCP endpoint is `PUBLIC_URL/mcp`. Also used as the OAuth issuer. If unset, it is derived from each request (`X-Forwarded-Proto` / `X-Forwarded-Host` or `Host`). Setting it is recommended. |
| `SECRET_KEY` | random | At least 32 characters. Encrypts client registrations, authorization codes and tokens. If unset, a random key is generated at startup and every client must sign in again after a restart. |
| `PAPERLESS_URL` | – | Preconfigured Paperless instance. If set, users sign in with Paperless username and password (or API token). If unset, users enter URL and API token. |
| `PAPERLESS_ALLOWED_HOSTS` | – | Only without `PAPERLESS_URL`: comma-separated hostnames users may connect to, e.g. `paperless.example.com,*.home.arpa`. Empty allows any host. |
| `ACCESS_TOKEN_TTL_SECONDS` | `3600` | Lifetime of access tokens. |
| `REFRESH_TOKEN_TTL_SECONDS` | `2592000` | Lifetime of refresh tokens (30 days). After that, users sign in again. |
| `PAPERLESS_TIMEOUT_SECONDS` | `60` | Timeout for requests to Paperless. |
| `MAX_UPLOAD_MB` | `50` | Maximum size of documents uploaded through `upload_document`. |
| `MAX_DOWNLOAD_MB` | `25` | Maximum size of files returned by `download_document`. |
| `TRUST_FORWARDED_HEADERS` | `true` | Use `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` from a reverse proxy. Set to `false` if the server is exposed directly without a proxy. |

## Connecting clients

The server URL is always `https://<your-host>/mcp`. On first use, the client opens a browser window for sign-in.

**Claude (claude.ai, Desktop and mobile apps):** *Settings → Connectors → Add custom connector*, enter the URL and click *Connect*.

**Claude Code:**

```sh
claude mcp add --transport http paperless https://paperless-mcp.example.com/mcp
```

Then run `/mcp` inside Claude Code and choose *Authenticate*.

**VS Code (GitHub Copilot)** – `.vscode/mcp.json`:

```json
{
  "servers": {
    "paperless": { "type": "http", "url": "https://paperless-mcp.example.com/mcp" }
  }
}
```

**Cursor** – `~/.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "paperless": { "url": "https://paperless-mcp.example.com/mcp" }
  }
}
```

Any other client that supports remote MCP servers with OAuth works the same way.

### Signing in

- **Open mode:** enter the Paperless URL (e.g. `https://paperless.example.com`) and your API token. You find the token in Paperless under *My Profile → API Auth Token*.
- **Preconfigured mode:** enter your Paperless username and password. The server exchanges them once for your API token and never stores the password. If your account uses two-factor authentication, use *Sign in with an API token instead*.

The MCP client can do everything your Paperless user is allowed to do. To revoke access, regenerate the API token in Paperless; the client then has to sign in again.

## Tools

| Tool | Description |
| --- | --- |
| `search_documents` | Full-text search (Paperless query syntax) combined with filters: tags (all/any/none), correspondent, document type, storage path, created/added date ranges, ASN, inbox, similar documents. Paged, with highlighted excerpts. |
| `get_document` | All metadata of a document, including custom fields, notes and a link to the web UI. Optionally includes the text. |
| `get_document_content` | The OCR text of a document, in chunks for long documents. |
| `download_document` | The archived PDF or original file as an embedded resource. |
| `get_document_thumbnail` | Preview image of the first page. |
| `upload_document` | Upload a file (base64) or text, with title, tags, correspondent, type, storage path, created date, ASN and custom fields. Waits for processing and returns the new document. |
| `get_task_status` | Status of a processing task, e.g. a pending upload. |
| `update_document` | Change title, dates, correspondent, type, storage path, ASN, tags (replace, add, remove) and custom fields. |
| `delete_document` | Delete a document (moved to the trash if enabled in Paperless). |
| `bulk_edit_documents` | Change tags, correspondent, type or storage path of many documents, delete or reprocess them. |
| `list_document_notes` / `add_document_note` / `delete_document_note` | Manage document notes. |
| `list_metadata` | List tags, correspondents, document types, storage paths or custom fields. |
| `create_metadata` / `update_metadata` / `delete_metadata` | Manage tags, correspondents, document types, storage paths and custom fields, including matching rules. |
| `get_instance_info` | Connected instance, signed-in user, Paperless version and statistics. |

Tools that delete data are marked as destructive, so clients ask for confirmation before running them.

## Security

- Paperless credentials are never written to disk. The API token is encrypted with `SECRET_KEY` into the access and refresh tokens held by the MCP client. Anyone with `SECRET_KEY` and a token can decrypt it, so keep the key secret.
- Changing `SECRET_KEY` invalidates all client registrations and tokens.
- Revoking a token (OAuth revocation endpoint) is held in memory until the token expires. For a hard cut-off, regenerate the API token in Paperless.
- In open mode the server makes requests to any URL users enter. If the server can reach internal networks, set `PAPERLESS_ALLOWED_HOSTS` or use `PAPERLESS_URL`.
- Sign-in, registration and token endpoints are rate limited per client IP.

## Development

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet run --project src/PaperlessMcpServer   # http://localhost:8080/mcp
dotnet test
```

Use the [MCP Inspector](https://github.com/modelcontextprotocol/inspector) (`npx @modelcontextprotocol/inspector`) to try the tools against `http://localhost:8080/mcp`.

Build the image locally:

```sh
docker build -t paperless-mcp-server .
```

### Releases

Every push to `main` publishes `ghcr.io/mkenfenheuer/paperless-mcp-server:latest`. Tags like `v1.2.3` publish `1.2.3`, `1.2` and `1`. Images are built for `linux/amd64` and `linux/arm64`.

## License

[MIT](LICENSE)
