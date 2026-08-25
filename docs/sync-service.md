# File Sync Service Protocol

CrossMux Sync Service provides a bounded, paginated manifest for one-way synchronization from a .NET service to the SD card. It complements the existing OPDS client: `/opds` works immediately for manual ebook downloads, while `/api/v1/sync/*` is the device-pull protocol.

## Manifest

`GET /api/v1/sync/manifest?page=1&pageSize=20&type=book|image`

The device must cap `pageSize` at 20. A response contains `version`, paging fields and items with `id`, `type`, `fileName`, `targetPath`, `size`, `sha256`, `modifiedUtc`, and `downloadUrl`.

Only server-provided `targetPath` values beginning with `/Books/` or `/Images/` are valid. The device must reject `..`, backslashes, control characters, query strings, and paths longer than its fixed path buffer.

## Download and commit

1. Download to `<targetPath>.part` using `downloadUrl`.
2. Stream SHA-256 verification from SD; do not buffer a file in RAM.
3. Remove an older backup, rename the current file to `.bak`, and rename `.part` to the final path.
4. Restore `.bak` if the final rename fails; remove `.bak` after success.
5. Clear the EPUB cache for overwritten EPUB files.

`GET /api/v1/sync/files/{id}` supports `Range`, `ETag`, and `Last-Modified`. The first firmware implementation may restart an interrupted item from byte zero; the endpoint permits a later resumable implementation without changing the protocol.

## Security

The service is designed for a trusted LAN. Upload/delete operations require `X-Api-Key`. Device routes are read-only. For routed networks, put the service behind HTTPS and device authentication before use.
