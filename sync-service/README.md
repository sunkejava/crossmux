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

## Smart Standby Studio

Open `/standby` to create server-rendered standby dashboards for individual readers. The studio includes weather, market and lyrics presets plus a JSON editor for custom layouts and HTTP JSON data sources.

The delivery path is intentionally small on the device:

1. The service resolves template variables and HTTP JSON bindings.
2. SkiaSharp renders an 800×480 four-level grayscale BMP.
3. The service publishes a retained notification to `crossmux/device/{deviceId}/standby/v1`.
4. The reader downloads the BMP over HTTP and atomically replaces its cached copy.

The device ID is the same 16-character ID used by AirPage. Configure the service base URL plus the matching MQTT host and port in the reader's web settings, then cycle to the remote face under **Apps > Standby**. Device MQTT authentication is not yet supported, so use an anonymous broker only on a trusted LAN/VPN.

```yaml
SmartStandby:
  StoragePath: smart-standby
  RefreshSeconds: 300
  MqttHost: mqtt-cn.uipcat.com
  MqttPort: 1883
```

Template values use `{{name}}`. A data-source binding maps a destination variable to a dotted JSON path:

```json
{
  "id": "weather",
  "url": "https://weather.example/api/current",
  "bindings": {
    "temp": "current.temperature",
    "weather": "current.summary"
  }
}
```

Only connect data sources you trust. The service follows the configured URLs from the server network, so deployments exposed to multiple users should add a URL allowlist at the reverse proxy or application layer.
