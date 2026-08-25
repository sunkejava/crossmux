# CrossMux Sync Service

An optional .NET 10 service for one-way delivery of ebooks and images to CrossMux devices.

## Supported files

- Books: EPUB, TXT, XTC
- Images: PNG, BMP, JPEG

The manifest maps books to `/Books` and images to `/Images`. Downloads support HTTP range requests and ETags.

## Run

```bash
dotnet run --project CrossMux.SyncService
```

Set `Sync__ApiKey` before exposing the service. `Sync__PublicBaseUrl` must be reachable by the reader; leave it empty only when proxy forwarding headers are configured correctly.

Upload:

```bash
curl -H "X-Api-Key: change-me" -F "type=auto" -F "file=@book.epub" http://localhost:5000/api/admin/files
```

Manifest:

```bash
curl "http://localhost:5000/api/v1/sync/manifest?page=1&pageSize=20"
```

The OPDS-compatible book catalog is available at `/opds` and can already be added to CrossMux under **Settings > OPDS Servers**.

## Publish and Docker

```bash
docker compose up -d --build
```

The admin API requires `X-Api-Key`. Device-facing manifest/download routes are intentionally read-only; keep the service on a trusted LAN or place authentication and TLS at a reverse proxy.
