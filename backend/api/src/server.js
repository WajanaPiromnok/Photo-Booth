const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const express = require("express");
const multer = require("multer");
const helmet = require("helmet");
const cors = require("cors");
const QRCode = require("qrcode");
const { Pool } = require("pg");
require("dotenv").config();

const app = express();
const upload = multer({
  storage: multer.memoryStorage(),
  limits: {
    fileSize: Number.parseInt(process.env.MAX_UPLOAD_BYTES || "26214400", 10)
  }
});

const config = {
  port: Number.parseInt(process.env.PORT || "8080", 10),
  publicBaseUrl: (process.env.PUBLIC_BASE_URL || "http://localhost:8080").replace(/\/$/, ""),
  uploadsRoot: path.resolve(process.env.UPLOADS_ROOT || path.join(process.cwd(), "uploads")),
  requiredDeviceToken: (process.env.DEVICE_BEARER_TOKEN || "").trim(),
  requiredDeviceIdPrefix: (process.env.DEVICE_ID_PREFIX || "").trim(),
  allowCorsOrigin: (process.env.CORS_ORIGIN || "*").trim()
};

const pool = new Pool({
  host: process.env.POSTGRES_HOST || "db",
  port: Number.parseInt(process.env.POSTGRES_PORT || "5432", 10),
  database: process.env.POSTGRES_DB || "photo_booth",
  user: process.env.POSTGRES_USER || "photo_booth",
  password: process.env.POSTGRES_PASSWORD || "photo_booth"
});

fs.mkdirSync(config.uploadsRoot, { recursive: true });

app.disable("x-powered-by");
app.use(helmet({
  crossOriginResourcePolicy: false
}));
app.use(cors({
  origin: config.allowCorsOrigin === "*" ? true : config.allowCorsOrigin,
  credentials: false
}));
app.use(express.json({ limit: "2mb" }));
app.use("/files", express.static(config.uploadsRoot, {
  fallthrough: false,
  maxAge: "7d",
  immutable: false
}));

app.get("/healthz", async (req, res) => {
  try {
    await pool.query("SELECT 1");
    return res.json({ success: true, data: { status: "ok" }, error: null });
  } catch (error) {
    return res.status(500).json(errorEnvelope("DB_UNAVAILABLE", error.message));
  }
});

app.post("/v1/jobs/:jobId/assets/upload", requireDeviceAuth, upload.fields([
  { name: "composed_file", maxCount: 1 },
  { name: "thumbnail_file", maxCount: 1 },
  { name: "motion_video_file", maxCount: 1 },
  { name: "motion_frame_files", maxCount: 48 }
]), async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).json(errorEnvelope("INVALID_JOB_ID", "Job id is required."));
  }

  const composedFile = req.files?.composed_file?.[0];
  if (!composedFile) {
    return res.status(400).json(errorEnvelope("COMPOSED_FILE_REQUIRED", "composed_file is required."));
  }

  const client = await pool.connect();
  try {
    await client.query("BEGIN");

    const deviceId = normalizeOptional(req.get("X-Device-Id")) || normalizeOptional(req.body.device_id) || "booth-local";
    const themeId = normalizeOptional(req.body.theme_id);
    const currencyCode = normalizeCurrency(req.body.currency);
    const amountMinorUnits = normalizeInteger(req.body.amount_minor_units, 0);
    const paymentReference = normalizeOptional(req.body.payment_reference);

    const composedAsset = writeUploadFile(jobId, "composed", composedFile);
    const assets = [composedAsset];

    const thumbnailFile = req.files?.thumbnail_file?.[0];
    if (thumbnailFile) {
      assets.push(writeUploadFile(jobId, "thumbnail", thumbnailFile));
    }

    const motionVideoFile = req.files?.motion_video_file?.[0];
    if (motionVideoFile) {
      assets.push(writeUploadFile(jobId, "motion_video", motionVideoFile));
    }

    const motionFrameFiles = req.files?.motion_frame_files || [];
    for (const motionFrameFile of motionFrameFiles) {
      assets.push(writeUploadFile(jobId, "motion_frame", motionFrameFile));
    }

    await ensureJob(client, {
      jobId,
      deviceId,
      themeId,
      amountMinorUnits,
      currencyCode,
      paymentReference
    });

    for (const asset of assets) {
      await upsertAsset(client, jobId, asset);
    }

    await client.query(
      `UPDATE booth_jobs
       SET status = $2,
           upload_status = $3,
           remote_asset_key = $4,
           updated_at = NOW()
       WHERE job_id = $1`,
      [jobId, "UPLOADED", "UPLOADED", composedAsset.remoteKey]
    );

    await client.query("COMMIT");

    return res.status(201).json({
      success: true,
      data: {
        remote_asset_key: composedAsset.remoteKey,
        assets: assets.map(toAssetResponse)
      },
      error: null
    });
  } catch (error) {
    await client.query("ROLLBACK");
    console.error("upload_failed", { jobId, error });
    return res.status(500).json(errorEnvelope("UPLOAD_FAILED", error.message));
  } finally {
    client.release();
  }
});

app.post("/v1/jobs/:jobId/assets", requireDeviceAuth, async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).json(errorEnvelope("INVALID_JOB_ID", "Job id is required."));
  }

  const assets = Array.isArray(req.body?.assets) ? req.body.assets : [];
  if (assets.length === 0) {
    return res.status(400).json(errorEnvelope("ASSETS_REQUIRED", "assets array is required."));
  }

  const client = await pool.connect();
  try {
    await client.query("BEGIN");
    await ensureJob(client, {
      jobId,
      deviceId: normalizeOptional(req.get("X-Device-Id")) || "booth-local",
      themeId: null,
      amountMinorUnits: 0,
      currencyCode: "THB",
      paymentReference: null
    });

    let primaryRemoteKey = null;
    for (const asset of assets) {
      validateAssetRegistration(asset);
      await upsertAsset(client, jobId, {
        assetType: asset.asset_type,
        remoteKey: asset.remote_key,
        contentType: asset.content_type || "application/octet-stream",
        checksum: asset.checksum || null,
        fileSizeBytes: null,
        originalFileName: null
      });

      if (!primaryRemoteKey && asset.asset_type === "composed") {
        primaryRemoteKey = asset.remote_key;
      }
    }

    await client.query(
      `UPDATE booth_jobs
       SET status = $2,
           upload_status = $3,
           remote_asset_key = COALESCE($4, remote_asset_key),
           updated_at = NOW()
       WHERE job_id = $1`,
      [jobId, "UPLOADED", "UPLOADED", primaryRemoteKey]
    );

    await client.query("COMMIT");

    const motionFrameResult = await pool.query(
      `SELECT COUNT(1)::int AS motion_frame_count
       FROM booth_assets
       WHERE job_id = $1 AND asset_type = 'motion_frame'`,
      [jobId]
    );
    const motionFrameCount = Number(motionFrameResult.rows[0]?.motion_frame_count || 0);

    return res.json({
      success: true,
      data: {
        accepted: true,
        remote_asset_key: primaryRemoteKey
      },
      error: null
    });
  } catch (error) {
    await client.query("ROLLBACK");
    console.error("asset_registration_failed", { jobId, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "ASSET_REGISTRATION_FAILED", error.message));
  } finally {
    client.release();
  }
});

app.post("/v1/jobs/:jobId/publish", requireDeviceAuth, async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).json(errorEnvelope("INVALID_JOB_ID", "Job id is required."));
  }

  const primaryAssetType = normalizeOptional(req.body?.primary_asset_type) || "composed";
  const client = await pool.connect();
  try {
    await client.query("BEGIN");

    const jobResult = await client.query(
      `SELECT job_id, status, upload_status, remote_asset_key
       FROM booth_jobs
       WHERE job_id = $1
       LIMIT 1`,
      [jobId]
    );

    if (jobResult.rowCount === 0) {
      throw httpError(404, "JOB_NOT_FOUND", `Job ${jobId} was not found.`);
    }

    const assetResult = await client.query(
      `SELECT asset_type, remote_key, content_type
       FROM booth_assets
       WHERE job_id = $1 AND asset_type = $2
       ORDER BY created_at DESC
       LIMIT 1`,
      [jobId, primaryAssetType]
    );

    if (assetResult.rowCount === 0) {
      throw httpError(409, "UPLOAD_NOT_READY", `Asset ${primaryAssetType} is not uploaded for ${jobId}.`);
    }

    const asset = assetResult.rows[0];
    const downloadUrl = `${config.publicBaseUrl}/d/${encodeURIComponent(jobId)}`;
    const motionVideoResult = await client.query(
      `SELECT remote_key
       FROM booth_assets
       WHERE job_id = $1 AND asset_type = 'motion_video'
       ORDER BY created_at DESC
       LIMIT 1`,
      [jobId]
    );
    const motionFrameResult = await client.query(
      `SELECT COUNT(1)::int AS motion_frame_count
       FROM booth_assets
       WHERE job_id = $1 AND asset_type = 'motion_frame'`,
      [jobId]
    );
    const hasMotionVideo = motionVideoResult.rowCount > 0;
    const motionFrameCount = Number(motionFrameResult.rows[0]?.motion_frame_count || 0);
    const motionClipUrl = hasMotionVideo
      ? `${downloadUrl}/clip.mp4`
      : motionFrameCount > 0
        ? `${downloadUrl}/clip`
        : null;

    await client.query(
      `UPDATE booth_jobs
       SET status = $2,
           upload_status = $3,
           remote_asset_key = $4,
           download_url = $5,
           published_at = NOW(),
           updated_at = NOW()
       WHERE job_id = $1`,
      [jobId, "LINK_READY", "LINK_READY", asset.remote_key, downloadUrl]
    );

    await client.query("COMMIT");

    return res.json({
      success: true,
      data: {
        job_id: jobId,
        upload_status: "LINK_READY",
        download_url: downloadUrl,
        motion_clip_url: motionClipUrl,
        motion_video_url: hasMotionVideo ? `${downloadUrl}/clip.mp4` : null,
        motion_frame_count: motionFrameCount,
        remote_asset_key: asset.remote_key
      },
      error: null
    });
  } catch (error) {
    await client.query("ROLLBACK");
    console.error("publish_failed", { jobId, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "PUBLISH_FAILED", error.message));
  } finally {
    client.release();
  }
});

app.get("/v1/jobs/:jobId", async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).json(errorEnvelope("INVALID_JOB_ID", "Job id is required."));
  }

  try {
    const result = await pool.query(
      `SELECT job_id, status, payment_status, upload_status, download_url, remote_asset_key, theme_id, amount_minor_units, currency_code, created_at, updated_at
       FROM booth_jobs
       WHERE job_id = $1
       LIMIT 1`,
      [jobId]
    );

    if (result.rowCount === 0) {
      return res.status(404).json(errorEnvelope("JOB_NOT_FOUND", `Job ${jobId} was not found.`));
    }

    const row = result.rows[0];
    return res.json({
      success: true,
      data: {
        job_id: row.job_id,
        status: row.status,
        payment_status: row.payment_status,
        upload_status: row.upload_status,
        theme_id: row.theme_id,
        amount_minor_units: Number(row.amount_minor_units || 0),
        currency_code: row.currency_code,
        download_url: row.download_url,
        remote_asset_key: row.remote_asset_key,
        created_at: row.created_at,
        updated_at: row.updated_at
      },
      error: null
    });
  } catch (error) {
    console.error("job_lookup_failed", { jobId, error });
    return res.status(500).json(errorEnvelope("JOB_LOOKUP_FAILED", error.message));
  }
});

app.post("/v1/analytics/events", requireDeviceAuth, async (req, res) => {
  const eventName = normalizeEventName(req.body?.event_name);
  if (!eventName) {
    return res.status(400).json(errorEnvelope("INVALID_EVENT_NAME", "event_name is required."));
  }

  const deviceId = normalizeOptional(req.get("X-Device-Id")) || normalizeOptional(req.body?.device_id) || "booth-local";
  const durationSeconds = normalizeNullableInteger(req.body?.duration_seconds);
  const metadata = normalizeMetadata(req.body?.metadata);

  try {
    const result = await pool.query(
      `INSERT INTO booth_events (
          event_name,
          job_id,
          device_id,
          theme_id,
          screen_id,
          duration_seconds,
          metadata
        )
        VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb)
        RETURNING id, created_at`,
      [
        eventName,
        normalizeOptional(req.body?.job_id),
        deviceId,
        normalizeOptional(req.body?.theme_id),
        normalizeOptional(req.body?.screen_id),
        durationSeconds,
        JSON.stringify(metadata)
      ]
    );

    return res.status(201).json({
      success: true,
      data: {
        event_id: result.rows[0].id,
        accepted: true,
        created_at: result.rows[0].created_at
      },
      error: null
    });
  } catch (error) {
    console.error("analytics_event_failed", { eventName, error });
    return res.status(500).json(errorEnvelope("ANALYTICS_EVENT_FAILED", error.message));
  }
});

app.get("/d/:jobId/qr", async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).json(errorEnvelope("INVALID_JOB_ID", "Job id is required."));
  }

  try {
    const result = await pool.query(
      `SELECT upload_status, download_url
       FROM booth_jobs
       WHERE job_id = $1
       LIMIT 1`,
      [jobId]
    );

    if (result.rowCount === 0) {
      return res.status(404).json(errorEnvelope("JOB_NOT_FOUND", `Job ${jobId} was not found.`));
    }

    const job = result.rows[0];
    if (job.upload_status !== "LINK_READY") {
      return res.status(202).json(errorEnvelope("DOWNLOAD_NOT_READY", "Download link is still processing."));
    }

    const downloadUrl = job.download_url || `${config.publicBaseUrl}/d/${encodeURIComponent(jobId)}`;
    const png = await QRCode.toBuffer(downloadUrl, {
      type: "png",
      errorCorrectionLevel: "M",
      margin: 1,
      width: 320
    });

    res.setHeader("Content-Type", "image/png");
    res.setHeader("Cache-Control", "public, max-age=300");
    return res.send(png);
  } catch (error) {
    console.error("qr_generation_failed", { jobId, error });
    return res.status(500).json(errorEnvelope("QR_GENERATION_FAILED", error.message));
  }
});

app.get("/d/:jobId/clip", async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const jobResult = await pool.query(
      `SELECT upload_status
       FROM booth_jobs
       WHERE job_id = $1
       LIMIT 1`,
      [jobId]
    );

    if (jobResult.rowCount === 0) {
      return res.status(404).send("Job not found.");
    }

    if (jobResult.rows[0].upload_status !== "LINK_READY") {
      return res.status(202).send("Countdown clip is still processing.");
    }

    const frameResult = await pool.query(
      `SELECT remote_key, original_file_name
       FROM booth_assets
       WHERE job_id = $1 AND asset_type = 'motion_frame'
       ORDER BY original_file_name ASC, created_at ASC`,
      [jobId]
    );

    if (frameResult.rowCount === 0) {
      return res.status(404).send("Countdown clip was not uploaded for this job.");
    }

    const frameUrls = frameResult.rows.map((row) => `/files/${encodeURIPath(row.remote_key)}`);
    res.setHeader("Content-Type", "text/html; charset=utf-8");
    res.setHeader("Cache-Control", "public, max-age=300");
    res.setHeader("Content-Security-Policy", "default-src 'self'; img-src 'self' data:; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'");
    return res.send(renderMotionClipPage(jobId, frameUrls));
  } catch (error) {
    console.error("clip_render_failed", { jobId, error });
    return res.status(500).send("Internal server error.");
  }
});

app.get("/d/:jobId/clip.mp4", async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const jobResult = await pool.query(
      `SELECT upload_status
       FROM booth_jobs
       WHERE job_id = $1
       LIMIT 1`,
      [jobId]
    );

    if (jobResult.rowCount === 0) {
      return res.status(404).send("Job not found.");
    }

    if (jobResult.rows[0].upload_status !== "LINK_READY") {
      return res.status(202).send("Countdown video is still processing.");
    }

    const videoResult = await pool.query(
      `SELECT remote_key, content_type
       FROM booth_assets
       WHERE job_id = $1 AND asset_type = 'motion_video'
       ORDER BY created_at DESC
       LIMIT 1`,
      [jobId]
    );

    if (videoResult.rowCount === 0) {
      return res.status(404).send("Countdown video was not uploaded for this job.");
    }

    const video = videoResult.rows[0];
    const absolutePath = path.resolve(config.uploadsRoot, video.remote_key);
    if (!absolutePath.startsWith(`${config.uploadsRoot}${path.sep}`) || !fs.existsSync(absolutePath)) {
      return res.status(404).send("Countdown video file was not found.");
    }

    res.setHeader("Content-Type", video.content_type || "video/mp4");
    res.setHeader("Cache-Control", "public, max-age=300");
    return res.sendFile(absolutePath);
  } catch (error) {
    console.error("motion_video_download_failed", { jobId, error });
    return res.status(500).send("Internal server error.");
  }
});

app.get("/d/:jobId", async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const result = await pool.query(
      `SELECT status, upload_status, remote_asset_key
       FROM booth_jobs
       WHERE job_id = $1
       LIMIT 1`,
      [jobId]
    );

    if (result.rowCount === 0) {
      return res.status(404).send("Job not found.");
    }

    const job = result.rows[0];
    if (job.upload_status !== "LINK_READY" || !job.remote_asset_key) {
      return res.status(202).send("Download is still processing.");
    }

    return res.redirect(302, `/files/${encodeURIPath(job.remote_asset_key)}`);
  } catch (error) {
    console.error("download_redirect_failed", { jobId, error });
    return res.status(500).send("Internal server error.");
  }
});

app.use((error, req, res, next) => {
  console.error("unhandled_error", error);
  return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "INTERNAL_ERROR", error.message || "Unexpected server error."));
});

initialize().then(() => {
  app.listen(config.port, "0.0.0.0", () => {
    console.log(`photo-booth-backend listening on ${config.port}`);
  });
}).catch((error) => {
  console.error("startup_failed", error);
  process.exit(1);
});

async function initialize() {
  await pool.query("SELECT 1");
  await pool.query(`
    CREATE TABLE IF NOT EXISTS booth_events (
      id BIGSERIAL PRIMARY KEY,
      event_name TEXT NOT NULL,
      job_id TEXT,
      device_id TEXT NOT NULL,
      theme_id TEXT,
      screen_id TEXT,
      duration_seconds INTEGER,
      metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
    )`);
  await pool.query("CREATE INDEX IF NOT EXISTS idx_booth_events_event_name ON booth_events(event_name)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_booth_events_job_id ON booth_events(job_id)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_booth_events_device_id ON booth_events(device_id)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_booth_events_created_at ON booth_events(created_at DESC)");
}

function requireDeviceAuth(req, res, next) {
  const deviceId = normalizeOptional(req.get("X-Device-Id"));
  const authHeader = normalizeOptional(req.get("Authorization"));

  if (config.requiredDeviceIdPrefix && (!deviceId || !deviceId.startsWith(config.requiredDeviceIdPrefix))) {
    return res.status(403).json(errorEnvelope("DEVICE_UNAUTHORIZED", "Device id is not allowed."));
  }

  if (config.requiredDeviceToken) {
    const expected = `Bearer ${config.requiredDeviceToken}`;
    if (authHeader !== expected) {
      return res.status(401).json(errorEnvelope("DEVICE_UNAUTHORIZED", "Device token is invalid."));
    }
  }

  return next();
}

function writeUploadFile(jobId, assetType, file) {
  const originalName = sanitizeFilename(file.originalname || `${assetType}.bin`);
  const assetDirectory = path.join(config.uploadsRoot, "jobs", jobId, assetType);
  fs.mkdirSync(assetDirectory, { recursive: true });

  const extension = path.extname(originalName) || defaultExtensionForMimeType(file.mimetype);
  const safeBaseName = path.basename(originalName, extension);
  const uniqueName = `${safeBaseName}-${Date.now()}${extension}`;
  const absolutePath = path.join(assetDirectory, uniqueName);
  fs.writeFileSync(absolutePath, file.buffer);

  const checksum = sha256(file.buffer);
  return {
    assetType,
    remoteKey: path.posix.join("jobs", jobId, assetType, uniqueName),
    contentType: file.mimetype || "application/octet-stream",
    checksum,
    fileSizeBytes: file.size ?? file.buffer.length,
    originalFileName: originalName
  };
}

async function ensureJob(client, input) {
  await client.query(
    `INSERT INTO booth_jobs (
        job_id,
        device_id,
        theme_id,
        status,
        payment_status,
        upload_status,
        amount_minor_units,
        currency_code,
        payment_reference
      )
      VALUES ($1, $2, $3, 'CREATED', 'UNKNOWN', 'PENDING', $4, $5, $6)
      ON CONFLICT (job_id)
      DO UPDATE SET
        device_id = COALESCE(NULLIF(EXCLUDED.device_id, ''), booth_jobs.device_id),
        theme_id = COALESCE(EXCLUDED.theme_id, booth_jobs.theme_id),
        amount_minor_units = CASE
          WHEN booth_jobs.amount_minor_units = 0 THEN EXCLUDED.amount_minor_units
          ELSE booth_jobs.amount_minor_units
        END,
        currency_code = COALESCE(NULLIF(EXCLUDED.currency_code, ''), booth_jobs.currency_code),
        payment_reference = COALESCE(EXCLUDED.payment_reference, booth_jobs.payment_reference),
        updated_at = NOW()`,
    [
      input.jobId,
      input.deviceId,
      input.themeId,
      input.amountMinorUnits,
      input.currencyCode,
      input.paymentReference
    ]
  );
}

async function upsertAsset(client, jobId, asset) {
  await client.query(
    `INSERT INTO booth_assets (
        job_id,
        asset_type,
        remote_key,
        content_type,
        checksum,
        file_size_bytes,
        original_file_name
      )
      VALUES ($1, $2, $3, $4, $5, $6, $7)
      ON CONFLICT (job_id, asset_type, remote_key)
      DO UPDATE SET
        content_type = EXCLUDED.content_type,
        checksum = COALESCE(EXCLUDED.checksum, booth_assets.checksum),
        file_size_bytes = COALESCE(EXCLUDED.file_size_bytes, booth_assets.file_size_bytes),
        original_file_name = COALESCE(EXCLUDED.original_file_name, booth_assets.original_file_name)`,
    [
      jobId,
      asset.assetType,
      asset.remoteKey,
      asset.contentType,
      asset.checksum,
      asset.fileSizeBytes,
      asset.originalFileName
    ]
  );
}

function validateAssetRegistration(asset) {
  if (!asset || !normalizeOptional(asset.asset_type) || !normalizeOptional(asset.remote_key)) {
    throw httpError(400, "INVALID_ASSET", "Each asset must include asset_type and remote_key.");
  }
}

function toAssetResponse(asset) {
  return {
    asset_type: asset.assetType,
    remote_key: asset.remoteKey,
    content_type: asset.contentType,
    checksum: asset.checksum
  };
}

function normalizeOptional(value) {
  if (value === undefined || value === null) {
    return null;
  }

  const normalized = String(value).trim();
  return normalized === "" ? null : normalized;
}

function normalizeJobId(value) {
  const normalized = normalizeOptional(value);
  if (!normalized) {
    return null;
  }

  return normalized.replace(/[^A-Za-z0-9._-]/g, "_");
}

function normalizeCurrency(value) {
  const normalized = normalizeOptional(value);
  return normalized ? normalized.toUpperCase() : "THB";
}

function normalizeInteger(value, fallback) {
  const parsed = Number.parseInt(String(value ?? ""), 10);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function normalizeNullableInteger(value) {
  if (value === undefined || value === null || value === "") {
    return null;
  }

  const parsed = Number.parseInt(String(value), 10);
  return Number.isFinite(parsed) ? Math.max(0, parsed) : null;
}

function normalizeEventName(value) {
  const normalized = normalizeOptional(value);
  if (!normalized) {
    return null;
  }

  return normalized.replace(/[^A-Za-z0-9._:-]/g, "_").slice(0, 96);
}

function normalizeMetadata(value) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return {};
  }

  const normalized = {};
  for (const [key, entryValue] of Object.entries(value)) {
    const normalizedKey = normalizeOptional(key);
    if (!normalizedKey) {
      continue;
    }

    normalized[normalizedKey.slice(0, 96)] = entryValue === undefined || entryValue === null
      ? null
      : String(entryValue).slice(0, 512);
  }

  return normalized;
}

function sanitizeFilename(fileName) {
  return fileName.replace(/[^A-Za-z0-9._-]/g, "_");
}

function defaultExtensionForMimeType(mimeType) {
  switch (mimeType) {
    case "image/jpeg":
      return ".jpg";
    case "image/png":
      return ".png";
    case "image/webp":
      return ".webp";
    case "video/mp4":
      return ".mp4";
    case "video/quicktime":
      return ".mov";
    default:
      return ".bin";
  }
}

function sha256(buffer) {
  return `sha256:${crypto.createHash("sha256").update(buffer).digest("hex")}`;
}

function encodeURIPath(relativePath) {
  return relativePath.split("/").map((segment) => encodeURIComponent(segment)).join("/");
}

function renderMotionClipPage(jobId, frameUrls) {
  const safeJobId = escapeHtml(jobId);
  const framesJson = JSON.stringify(frameUrls);
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Countdown Clip ${safeJobId}</title>
  <style>
    :root { color-scheme: dark; font-family: ui-rounded, system-ui, -apple-system, BlinkMacSystemFont, sans-serif; }
    body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: radial-gradient(circle at top, #26384a, #060708 68%); color: #fff; }
    main { width: min(920px, 94vw); text-align: center; }
    h1 { margin: 0 0 18px; font-size: clamp(28px, 6vw, 58px); letter-spacing: 0.02em; }
    .stage { border: 1px solid rgba(255,255,255,0.18); border-radius: 28px; padding: 18px; background: rgba(255,255,255,0.08); box-shadow: 0 24px 80px rgba(0,0,0,0.38); }
    img { width: 100%; max-height: 70vh; object-fit: contain; border-radius: 18px; background: #101214; }
    p { opacity: 0.74; font-size: 15px; }
  </style>
</head>
<body>
  <main>
    <h1>Countdown Clip</h1>
    <section class="stage">
      <img id="frame" alt="Countdown clip frame">
    </section>
    <p>Job ${safeJobId} · ${frameUrls.length} frames</p>
  </main>
  <script>
    const frames = ${framesJson};
    const image = document.getElementById('frame');
    let index = 0;
    function tick() {
      image.src = frames[index % frames.length];
      index += 1;
    }
    tick();
    setInterval(tick, 250);
  </script>
</body>
</html>`;
}

function escapeHtml(value) {
  return String(value ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function errorEnvelope(code, message) {
  return {
    success: false,
    data: null,
    error: {
      code,
      message
    }
  };
}

function httpError(statusCode, code, message) {
  const error = new Error(message);
  error.statusCode = statusCode;
  error.code = code;
  return error;
}
