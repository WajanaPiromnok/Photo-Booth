# Photo Booth Backend

Minimal backend scaffold for the Unity booth runtime.

## What it includes

- `api/`: Express API for upload, asset registration, publish, job lookup, and download redirect
- `db/init/`: PostgreSQL schema bootstrap
- `print-bridge/`: local-only PrintBridge for sending final booth artwork to the kiosk printer
- `docker-compose.yml`: OrbStack-friendly stack for `api + db + caddy`
- `Caddyfile`: reverse proxy / HTTPS entrypoint

## Endpoints

- `POST /v1/jobs/:jobId/assets/upload`
- `POST /v1/jobs/:jobId/assets/raw-capture`
- `POST /v1/jobs/:jobId/assets`
- `POST /v1/jobs/:jobId/publish`
- `GET /v1/jobs/:jobId`
- `GET /v1/assets/composed/featured`
- `GET /v1/assets/raw/featured`
- `GET /v1/assets/:projectRoute/:assetKind/featured`
- `GET /admin/print-quota`
- `GET /api/admin/v1/print-quota/status`
- `POST /api/admin/v1/print-quota/reset`
- `GET /world-tour/:jobId`
- `GET /d/:jobId`
- `GET /healthz`

## Local PrintBridge

The Unity runtime can call a local print bridge instead of the simulated print client.

Current kiosk builds keep Unity pointed at `http://127.0.0.1:18080`. If the team-provided bridge exists at `/Users/ezreal/Downloads/Furryways2_Claude/Build/backend/print-bridge`, the runner scripts use that folder and `/Users/ezreal/Downloads/Furryways2_Claude/print-bridge.env` automatically. Otherwise they fall back to this repo's `backend/print-bridge`.

Team-provided env:

```text
DEFAULT_PRINTER_NAME=DS-RX1 4x6 Cut
ALLOWED_PRINTER_NAMES=DS-RX1 4x6 Cut
OVERRIDE_REQUESTED_PRINTER=true
```

On the Windows kiosk, place that env file at:

```text
C:\Users\Administrator\.photo-booth\print-bridge.env
```

For kiosk/app setup, install it as a macOS LaunchAgent so it starts automatically on login:

```bash
cd /Users/ezreal/Desktop/Photo-Booth
./scripts/install-print-bridge-launchagent.sh
```

The installer writes:

- `~/Library/LaunchAgents/com.readyverse.photobooth.printbridge.plist`
- `~/.photo-booth/print-bridge.env`
- logs under `~/Library/Logs/PhotoBooth/`

Uninstall:

```bash
cd /Users/ezreal/Desktop/Photo-Booth
./scripts/uninstall-print-bridge-launchagent.sh
```

Manual foreground run for debugging:

```bash
cd /Users/ezreal/Desktop/Photo-Booth
./scripts/run-print-bridge.sh
```

PrintBridge listens on `http://127.0.0.1:18080` and exposes:

- `GET /healthz`
- `POST /api/print/jobs`

Request body:

```json
{
  "job_id": "JOB-20260531-120000-abc12345",
  "image_path": "/absolute/path/to/composed.jpg",
  "printer_name": null,
  "copies": 1
}
```

On macOS it submits jobs with `lp -d <printer> -n <copies> -o ... <image_path>`. On Windows it uses PowerShell printing from the bridge folder. With `OVERRIDE_REQUESTED_PRINTER=true`, PrintBridge ignores any printer name from Unity and always uses `DEFAULT_PRINTER_NAME`, so moving kiosks only requires changing the local PrintBridge env. Keep `ALLOWED_PRINTER_NAMES` restricted to the installed kiosk printer name.

## Print Quota Admin

Open `/admin/print-quota` to see how many photos have been printed from the 700 photo quota, how many remain, and reset the counter to zero. The page uses `ADMIN_BEARER_TOKEN` for the status and reset APIs.

The counter is based on print-completed backend analytics events from Unity (`booth_frontend_print_completed` and `booth_print_completed`) after the latest reset. Resetting does not delete historical events; it stores a new reset timestamp and starts the displayed count from that point.

## DigitalOcean Spaces Storage

Set the Spaces env vars to upload booth media to S3-compatible object storage. The backend still keeps a local cache under `UPLOADS_ROOT` for image/video processing, but `/files/...` redirects or proxies to Spaces when storage is enabled.

```text
STORAGE_DRIVER=auto
SPACES_ENDPOINT=https://<region>.digitaloceanspaces.com
SPACES_REGION=<region>
SPACES_BUCKET=<bucket>
SPACES_ACCESS_KEY_ID=<access-key>
SPACES_SECRET_ACCESS_KEY=<secret-key>
SPACES_PUBLIC_BASE_URL=https://<bucket>.<region>.digitaloceanspaces.com
SPACES_KEY_PREFIX=
```

When Spaces is enabled, treat Spaces as the source of truth and local `UPLOADS_ROOT` as cache/working storage. The backend hydrates missing local files from Spaces before generating downloads. To protect a 60GB production server from filling up, configure local cache cleanup thresholds:

```text
LOCAL_CACHE_TTL_HOURS=24
LOCAL_CACHE_CLEANUP_INTERVAL_MINUTES=60
LOCAL_CACHE_MIN_FREE_GB=15
LOCAL_CACHE_AGGRESSIVE_FREE_GB=10
LOCAL_CACHE_CRITICAL_FREE_GB=5
LOCAL_CACHE_AGGRESSIVE_TTL_HOURS=6
```

Cleanup only runs when Spaces/object storage is enabled. If free disk is below `LOCAL_CACHE_MIN_FREE_GB`, files under `UPLOADS_ROOT` older than `LOCAL_CACHE_TTL_HOURS` are deleted locally. If disk is still below `LOCAL_CACHE_AGGRESSIVE_FREE_GB`, only generated/cache files older than `LOCAL_CACHE_AGGRESSIVE_TTL_HOURS` are removed. Do not use `docker system prune --volumes` for this project; production uploads live under `/opt/photo-booth/uploads`, while `.env` and compose files live outside that cache path.

Use `STORAGE_DRIVER=local` to force local filesystem storage for development.

Default CUPS options match the current kiosk print dialog:

```text
PageSize=w6h4
orientation-requested=4
fit-to-page
MediaMethod=Normal
PaperType=LabelGaps
GapsHeight=3
PostAction=TearOff
Occurrence=Every
Brightness=0
HalftoneType=Stucki
Origin=Default
MirrorImage=False
NegativeImage=False
PrintSpeed=2
Darkness=13
```

Override them with `PRINT_OPTIONS=key=value,key=value,...` if a future printer driver uses different option names.

## Admin Console

- Local URL: `http://localhost:8080/admin/vouchers`
- Through Caddy: `https://localhost/admin/vouchers`
- Local default admin token from `docker-compose.yml`: `dev-admin-token`

Set `ADMIN_BEARER_TOKEN` in `.env` before using this outside local development.

## API Docs

- Swagger UI: `http://localhost:8080/api/docs`
- OpenAPI JSON: `http://localhost:8080/api/docs/openapi.json`
- Voucher test tool: `http://localhost:8080/tools/voucher-test`
- Voucher scan page: `http://localhost:8080/voucher-scan`
- Voucher link page: `http://localhost:8080/voucher-link`
- Production HTTPS voucher test tool: `https://api.wajanapir.com/tools/voucher-test`
- Production HTTPS voucher scan page: `https://api.wajanapir.com/voucher-scan`
- Production HTTPS voucher link page: `https://api.wajanapir.com/voucher-link`

## Voucher Status API

When `/api/kiosk/v1/checkout/voucher/reserve` returns `APPLIED`, the voucher has already consumed one use. The backend increments `vouchers.used_count`; if `used_count >= max_uses`, the code can no longer be used.

Frontend websites can check a code without a bearer token, without creating a booth job, and without consuming usage:

```bash
curl -X POST http://localhost:8080/api/web/v1/vouchers/status \
  -H 'Content-Type: application/json' \
  -d '{"voucher_code":"PB-ADF2633C"}'
```

Important response fields:

- `has_been_used`: true when the code has consumed at least one use.
- `quota_exhausted`: true when the code has no remaining uses.
- `usable_now`: true when the code exists, is active, is in its validity window, and has remaining uses.
- `status`: one of `AVAILABLE`, `USED`, `EXPIRED`, `NOT_STARTED`, `INACTIVE`, or `NOT_FOUND`.

## Voucher QR PNG

AUTO voucher codes use the `PB-XXXXXXXX` format. The backend can render a PNG QR image for that code without requiring a bearer token:

```html
<img src="https://api.wajanapir.com/api/web/v1/vouchers/PB-ADF2633C/qr.png" alt="Voucher QR">
```

The QR payload is the voucher code text itself, for example `PB-ADF2633C`. `POST /api/admin/v1/vouchers/generate` returns `qr_png_url` for AUTO vouchers and `null` for manual/non-`PB-XXXXXXXX` formats.

## Local startup on OrbStack

1. Copy `.env.example` to `.env`
2. Set `PUBLIC_HOSTNAME`, `PUBLIC_BASE_URL`, `VOUCHER_LINK_BASE_URL`, `VOUCHER_SCAN_BASE_URL`, `VOUCHER_LINK_PATH`, `VOUCHER_SCAN_PATH`, `POSTGRES_PASSWORD`, and `DEVICE_BEARER_TOKEN`
3. Run:

```bash
cd /Users/ezreal/Desktop/Photo-Booth/backend
docker compose up --build -d
```

4. Check health:

```bash
curl http://localhost/healthz
```

If Caddy is configured with a real public hostname and your DNS points to the Mac mini, use:

```bash
curl https://<your-hostname>/healthz
```

## Unity mapping

Set these fields in `BoothRuntimeBootstrap`:

- `backendDeviceId`
- `backendDeviceToken`
- `backendBoothApiBaseUrl`
- `backendPublishApiBaseUrl`
- `backendDownloadBaseUrl`
- `backendAssetUploadPathTemplate`
- `backendAssetRegistrationPathTemplate`
- `backendPublishPathTemplate`
- `usePrintBridge`
- `printBridgeBaseUrl`
- `defaultPrinterName`
- `preferredCameraDeviceNames`

Recommended values:

- `backendBoothApiBaseUrl`: `https://api.example.com`
- `backendPublishApiBaseUrl`: `https://api.example.com`
- `backendDownloadBaseUrl`: `https://api.example.com/world-tour`
- `backendAssetUploadPathTemplate`: `/v1/jobs/{jobId}/assets/upload`
- `backendRawCaptureUploadPathTemplate`: `/v1/jobs/{jobId}/assets/raw-capture`
- `backendAssetRegistrationPathTemplate`: `/v1/jobs/{jobId}/assets`
- `backendPublishPathTemplate`: `/v1/jobs/{jobId}/publish`
- `backendSeparateAssetRegistration`: `true`
- `backendUploadThumbnail`: `true`
- `usePrintBridge`: `true` on kiosk builds; set `false` only when you intentionally want simulated printing
- `printBridgeBaseUrl`: `http://127.0.0.1:18080`
- `defaultPrinterName`: no longer required for normal kiosk printing; PrintBridge uses local `DEFAULT_PRINTER_NAME`
- `preferredCameraDeviceNames`: `OBSBOT Virtual Camera`, `OBSBOT`
- `DEFAULT_DOWNLOAD_ROUTE_PREFIX`: `world-tour` for the current Unity project, or `d` for the legacy backend flow
- `VOUCHER_SCAN_BASE_URL`: the public origin that should open the phone scan page, usually the same as `PUBLIC_BASE_URL`
- `VOUCHER_LINK_BASE_URL`: the public origin that should open the mobile voucher-link page, usually the same as `PUBLIC_BASE_URL`
- `VOUCHER_LINK_PATH`: the path that the QR should open for the new loop, default `/voucher-link`
- `VOUCHER_SCAN_PATH`: the path that the QR should open, default `/voucher-scan`
- `KIOSK_SESSION_TTL_SECONDS`: kiosk session lifetime for the voucher-link flow, default `900`
- `VOUCHER_SCAN_SESSION_TTL_SECONDS`: session lifetime for the phone-to-kiosk scan flow, default `300`

## Expected upload form fields

Raw capture upload (`/v1/jobs/:jobId/assets/raw-capture`):

- `raw_capture_file`
- `capture_index`
- `capture_total`
- `session_started_at_utc`
- `capture_taken_at_utc`
- `job_id`
- `device_id`
- `theme_id`
- `currency`
- `amount_minor_units`
- `payment_reference`

Final asset upload (`/v1/jobs/:jobId/assets/upload`):

- `job_id`
- `device_id`
- `theme_id`
- `currency`
- `amount_minor_units`
- `payment_reference`
- `composed_checksum`
- `composed_file`
- `thumbnail_checksum` optional
- `thumbnail_file` optional
- `live_image_checksum` optional
- `live_image_file` optional PNG contact sheet / live image
- `motion_video_checksum` optional
- `motion_video_file` optional MP4/MOV countdown clip
- `motion_frame_checksum_<index>` optional fallback frame checksums
- `motion_frame_files` optional fallback PNG frame sequence

## Notes

- Files are stored in a Docker volume mounted at `/var/photo-booth/uploads`
- Job files are grouped under `jobs/<yyyyMMdd_HHmmss_JOB-ID>/...` using `session_started_at_utc` converted to `Asia/Bangkok`
- New project uploads are grouped under `<project-route>/jobs/<yyyyMMdd_HHmmss_JOB-ID>/...`, such as `world-tour/jobs/...` and `kooky-world/jobs/...`. Existing `jobs/...` assets remain valid.
- Unity download links resolve through `/world-tour/:jobId` as the current World Tour page. Direct final image access is available at `/world-tour/:jobId/image`.
- Clients can request the current main-project label at `/v1/assets/composed/featured`. The endpoint finds the newest `prj_main` job captured within the last 90 seconds, checks whether that exact job is ready with a raw capture and composed asset, and renders it into label `1.png` or `2.png` with the passenger name in `BatteryPark.ttf`. If that newest job is not ready, or if no job was captured in the 90-second window, it randomly selects from all ready `prj_main` jobs. The selection response is not cached. Override the window with `FEATURED_COMPOSED_RECENCY_SECONDS`; add `?format=json` to inspect the selected job, selection mode, capture time, template, layout version, and stable rendered-image URL.
- Unity can request a featured raw capture image at `/v1/assets/raw/featured`. The endpoint redirects to the newest `raw_capture` asset from the raw folder in the recent window, or a random raw capture if no recent one exists. Override the window with `FEATURED_RAW_RECENCY_SECONDS`.
- Unity can request a project-filtered featured image at `/v1/assets/:projectRoute/:assetKind/featured`, for example `/v1/assets/kooky-world/raw/featured` or `/v1/assets/world-tour/composed/featured`.
- Legacy download links resolve through `/d/:jobId` with the `chiselda/photo-booth-backend:0.2.25` style page. `/d` and `/world-tour` render separate page designs.
- The scaffold uses local-disk storage first. You can move asset storage to S3/R2 later without changing the Unity contract much.
