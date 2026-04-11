# Photo Booth Backend

Minimal backend scaffold for the Unity booth runtime.

## What it includes

- `api/`: Express API for upload, asset registration, publish, job lookup, and download redirect
- `db/init/`: PostgreSQL schema bootstrap
- `docker-compose.yml`: OrbStack-friendly stack for `api + db + caddy`
- `Caddyfile`: reverse proxy / HTTPS entrypoint

## Endpoints

- `POST /v1/jobs/:jobId/assets/upload`
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
- `backendAssetRegistrationPathTemplate`: `/v1/jobs/{jobId}/assets`
- `backendPublishPathTemplate`: `/v1/jobs/{jobId}/publish`
- `backendSeparateAssetRegistration`: `true`
- `backendUploadThumbnail`: `true`

## Expected upload form fields

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

## Notes

- Files are stored in a Docker volume mounted at `/var/photo-booth/uploads`
- Download links resolve through `/d/:jobId` and redirect to `/files/...`
- The scaffold uses local-disk storage first. You can move asset storage to S3/R2 later without changing the Unity contract much.
