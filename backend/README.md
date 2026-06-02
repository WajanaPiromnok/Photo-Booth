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
- `GET /world-tour/:jobId`
- `GET /d/:jobId`
- `GET /healthz`

## Local PrintBridge

The Unity runtime can call a local print bridge instead of the simulated print client.

For kiosk/app setup, install it as a macOS LaunchAgent so it starts automatically on login:

```bash
cd /Users/ezreal/Desktop/Photo-Booth
DEFAULT_PRINTER_NAME="Noah_Test_Printer" ./scripts/install-print-bridge-launchagent.sh
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
cd /Users/ezreal/Desktop/Photo-Booth/backend/print-bridge
PORT=18080 DEFAULT_PRINTER_NAME="Noah_Test_Printer" ALLOWED_PRINTER_NAMES="Noah_Test_Printer" OVERRIDE_REQUESTED_PRINTER=true npm start
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

On macOS it submits jobs with `lp -d <printer> -n <copies> -o ... <image_path>`. With `OVERRIDE_REQUESTED_PRINTER=true`, PrintBridge ignores any printer name from Unity and always uses `DEFAULT_PRINTER_NAME`, so moving kiosks only requires changing the local PrintBridge env. Keep `ALLOWED_PRINTER_NAMES` restricted to the installed kiosk printer name. On this machine, `lpstat -p` currently reports `Noah_Test_Printer`.

Default CUPS options match the current kiosk print dialog:

```text
PageSize=w4h6
orientation-requested=3
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

## Local startup on OrbStack

1. Copy `.env.example` to `.env`
2. Set `PUBLIC_HOSTNAME`, `PUBLIC_BASE_URL`, `POSTGRES_PASSWORD`, and `DEVICE_BEARER_TOKEN`
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
- Unity download links resolve through `/world-tour/:jobId` as the current World Tour page. Direct final image access is available at `/world-tour/:jobId/image`.
- Legacy download links resolve through `/d/:jobId` with the `chiselda/photo-booth-backend:0.2.25` style page. `/d` and `/world-tour` render separate page designs.
- The scaffold uses local-disk storage first. You can move asset storage to S3/R2 later without changing the Unity contract much.
