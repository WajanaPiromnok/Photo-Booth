# Photo Booth Backend

Minimal backend scaffold for the Unity booth runtime.

## What it includes

- `api/`: Express API for upload, asset registration, publish, job lookup, and download redirect
- `db/init/`: PostgreSQL schema bootstrap
- `docker-compose.yml`: OrbStack-friendly stack for `api + db + caddy`
- `Caddyfile`: reverse proxy / HTTPS entrypoint

## Endpoints

- `POST /v1/jobs/:jobId/assets/upload`
- `POST /v1/jobs/:jobId/assets/raw-capture`
- `POST /v1/jobs/:jobId/assets`
- `POST /v1/jobs/:jobId/publish`
- `GET /v1/jobs/:jobId`
- `GET /d/:jobId`
- `GET /healthz`

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

Recommended values:

- `backendBoothApiBaseUrl`: `https://api.example.com`
- `backendPublishApiBaseUrl`: `https://api.example.com`
- `backendDownloadBaseUrl`: `https://api.example.com/d`
- `backendAssetUploadPathTemplate`: `/v1/jobs/{jobId}/assets/upload`
- `backendRawCaptureUploadPathTemplate`: `/v1/jobs/{jobId}/assets/raw-capture`
- `backendAssetRegistrationPathTemplate`: `/v1/jobs/{jobId}/assets`
- `backendPublishPathTemplate`: `/v1/jobs/{jobId}/publish`
- `backendSeparateAssetRegistration`: `true`
- `backendUploadThumbnail`: `true`

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
- Download links resolve through `/d/:jobId` as a mobile-friendly session page with QR, framed image, liveview/clip, and download buttons. Direct final image access is available at `/d/:jobId/image`.
- The scaffold uses local-disk storage first. You can move asset storage to S3/R2 later without changing the Unity contract much.
