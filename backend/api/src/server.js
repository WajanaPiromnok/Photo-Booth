const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const { spawn } = require("child_process");
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
    fileSize: Number.parseInt(process.env.MAX_UPLOAD_BYTES || "104857600", 10)
  }
});

const config = {
  port: Number.parseInt(process.env.PORT || "8080", 10),
  publicBaseUrl: (process.env.PUBLIC_BASE_URL || "http://localhost:8080").replace(/\/$/, ""),
  uploadsRoot: path.resolve(process.env.UPLOADS_ROOT || path.join(process.cwd(), "uploads")),
  publicRoot: path.resolve(process.env.PUBLIC_ROOT || path.join(process.cwd(), "public")),
  ffmpegPath: (process.env.FFMPEG_PATH || "ffmpeg").trim(),
  requiredDeviceToken: (process.env.DEVICE_BEARER_TOKEN || "").trim(),
  requiredDeviceIdPrefix: (process.env.DEVICE_ID_PREFIX || "").trim(),
  allowCorsOrigin: (process.env.CORS_ORIGIN || "*").trim(),
  downloadPageTitle: (process.env.DOWNLOAD_PAGE_TITLE || "MRKREME Photo Session").trim()
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
app.use("/assets", express.static(config.publicRoot, {
  fallthrough: false,
  maxAge: "30d",
  immutable: true
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
  { name: "live_image_file", maxCount: 1 },
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
    const sessionStartedAtUtc = normalizeOptional(req.body.session_started_at_utc);

    await ensureJob(client, {
      jobId,
      deviceId,
      themeId,
      amountMinorUnits,
      currencyCode,
      paymentReference,
      sessionStartedAtUtc
    });

    const sessionFolder = await ensureJobSessionFolder(client, jobId, sessionStartedAtUtc);
    const composedAsset = writeUploadFile(sessionFolder, "composed", composedFile);
    const assets = [composedAsset];

    const thumbnailFile = req.files?.thumbnail_file?.[0];
    if (thumbnailFile) {
      assets.push(writeUploadFile(sessionFolder, "thumbnail", thumbnailFile));
    }

    const liveImageFile = req.files?.live_image_file?.[0];
    if (liveImageFile) {
      assets.push(writeUploadFile(sessionFolder, "live_image", liveImageFile));
    }

    const motionVideoFile = req.files?.motion_video_file?.[0];
    if (motionVideoFile) {
      assets.push(writeUploadFile(sessionFolder, "motion_video", motionVideoFile));
    }

    const motionFrameFiles = req.files?.motion_frame_files || [];
    for (const motionFrameFile of motionFrameFiles) {
      assets.push(writeUploadFile(sessionFolder, "motion_frame", motionFrameFile));
    }

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

    console.log("asset_upload_completed", {
      jobId,
      sessionFolder,
      assetTypes: assets.map((asset) => asset.assetType),
      assetCount: assets.length
    });

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

app.post("/v1/jobs/:jobId/assets/raw-capture", requireDeviceAuth, upload.fields([
  { name: "raw_capture_file", maxCount: 1 }
]), async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).json(errorEnvelope("INVALID_JOB_ID", "Job id is required."));
  }

  const rawCaptureFile = req.files?.raw_capture_file?.[0];
  if (!rawCaptureFile) {
    return res.status(400).json(errorEnvelope("RAW_CAPTURE_FILE_REQUIRED", "raw_capture_file is required."));
  }

  const captureIndex = normalizeInteger(req.body.capture_index, 0);
  if (captureIndex < 1) {
    return res.status(400).json(errorEnvelope("INVALID_CAPTURE_INDEX", "capture_index must be greater than zero."));
  }

  const captureTotal = Math.max(captureIndex, normalizeInteger(req.body.capture_total, captureIndex));
  const sessionStartedAtUtc = normalizeOptional(req.body.session_started_at_utc);
  const captureTakenAtUtc = normalizeOptional(req.body.capture_taken_at_utc);
  const client = await pool.connect();

  try {
    await client.query("BEGIN");

    const deviceId = normalizeOptional(req.get("X-Device-Id")) || normalizeOptional(req.body.device_id) || "booth-local";
    await ensureJob(client, {
      jobId,
      deviceId,
      themeId: normalizeOptional(req.body.theme_id),
      amountMinorUnits: normalizeInteger(req.body.amount_minor_units, 0),
      currencyCode: normalizeCurrency(req.body.currency),
      paymentReference: normalizeOptional(req.body.payment_reference),
      sessionStartedAtUtc
    });

    const sessionFolder = await ensureJobSessionFolder(client, jobId, sessionStartedAtUtc);
    const rawAsset = writeRawCaptureFile(sessionFolder, captureIndex, captureTakenAtUtc, rawCaptureFile);
    await upsertAsset(client, jobId, rawAsset);
    const rawCaptureCountResult = await client.query(
      `SELECT COUNT(1)::int AS raw_capture_count
       FROM booth_assets
       WHERE job_id = $1 AND asset_type = 'raw_capture'`,
      [jobId]
    );
    const rawCaptureCount = Number(rawCaptureCountResult.rows[0]?.raw_capture_count || 0);
    const rawCapturesComplete = rawCaptureCount >= captureTotal;
    const downloadUrl = `${config.publicBaseUrl}/d/${encodeURIComponent(jobId)}`;
    await client.query(
      `UPDATE booth_jobs
       SET status = CASE WHEN $2 THEN 'LINK_READY' ELSE status END,
           upload_status = CASE WHEN $2 THEN 'LINK_READY' ELSE upload_status END,
           remote_asset_key = CASE WHEN $2 THEN COALESCE(remote_asset_key, $3) ELSE remote_asset_key END,
           download_url = CASE WHEN $2 THEN COALESCE(download_url, $4) ELSE download_url END,
           published_at = CASE WHEN $2 THEN COALESCE(published_at, NOW()) ELSE published_at END,
           updated_at = NOW()
       WHERE job_id = $1`,
      [jobId, rawCapturesComplete, rawAsset.remoteKey, downloadUrl]
    );

    await client.query("COMMIT");

    console.log("raw_capture_upload_completed", {
      jobId,
      sessionFolder,
      captureIndex,
      captureTotal,
      remoteKey: rawAsset.remoteKey,
      rawCaptureCount,
      rawCapturesComplete,
      downloadUrl: rawCapturesComplete ? downloadUrl : null
    });

    return res.status(201).json({
      success: true,
      data: {
        accepted: true,
        capture_index: captureIndex,
        capture_total: captureTotal,
        session_folder: sessionFolder,
        remote_asset_key: rawAsset.remoteKey,
        assets: [toAssetResponse(rawAsset)]
      },
      error: null
    });
  } catch (error) {
    await client.query("ROLLBACK");
    console.error("raw_capture_upload_failed", { jobId, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "RAW_CAPTURE_UPLOAD_FAILED", error.message));
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

    console.log("job_publish_completed", {
      jobId,
      primaryAssetType,
      downloadUrl,
      hasMotionVideo,
      motionFrameCount
    });

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
      `SELECT job_id, status, payment_status, upload_status, download_url, remote_asset_key, session_folder, session_started_at_utc, theme_id, amount_minor_units, currency_code, created_at, updated_at
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
        session_folder: row.session_folder,
        session_started_at_utc: row.session_started_at_utc,
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
      const rawCaptureResult = await pool.query(
        `SELECT COUNT(1)::int AS raw_capture_count
         FROM booth_assets
         WHERE job_id = $1 AND asset_type = 'raw_capture'`,
        [jobId]
      );
      const rawCaptureCount = Number(rawCaptureResult.rows[0]?.raw_capture_count || 0);
      if (rawCaptureCount < 4) {
        return res.status(202).json(errorEnvelope("DOWNLOAD_NOT_READY", "Download link is still processing."));
      }
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

app.get("/d/:jobId/clip-download.mp4", async (req, res) => {
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
    const absolutePath = absoluteUploadPath(video.remote_key);
    if (!fs.existsSync(absolutePath)) {
      return res.status(404).send("Countdown video file was not found.");
    }

    return sendAttachmentFile(res, absolutePath, video.content_type || "video/mp4", `${jobId}-countdown.mp4`);
  } catch (error) {
    console.error("motion_video_attachment_download_failed", { jobId, error });
    return res.status(500).send("Internal server error.");
  }
});

app.get("/d/:jobId/liveview.mp4", async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const { job, assets } = await loadDownloadJobAssets(jobId);
    const rawCaptures = sortRawCaptureAssets(assets.filter((asset) => asset.asset_type === "raw_capture"));
    if (rawCaptures.length === 0) {
      return res.status(404).send("Raw captures were not uploaded for this job.");
    }

    const outputPath = await ensureFramedLiveviewVideo(job, rawCaptures);
    res.setHeader("Content-Type", "video/mp4");
    res.setHeader("Cache-Control", "public, max-age=300");
    return res.sendFile(outputPath);
  } catch (error) {
    console.error("framed_liveview_video_failed", { jobId, error });
    return res.status(error.statusCode || 500).send(error.message || "Internal server error.");
  }
});

app.get("/d/:jobId/liveview-download.mp4", async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const { job, assets } = await loadDownloadJobAssets(jobId);
    const rawCaptures = sortRawCaptureAssets(assets.filter((asset) => asset.asset_type === "raw_capture"));
    if (rawCaptures.length === 0) {
      return res.status(404).send("Raw captures were not uploaded for this job.");
    }

    const outputPath = await ensureFramedLiveviewVideo(job, rawCaptures);
    return sendAttachmentFile(res, outputPath, "video/mp4", `${jobId}-liveview.mp4`);
  } catch (error) {
    console.error("framed_liveview_video_download_failed", { jobId, error });
    return res.status(error.statusCode || 500).send(error.message || "Internal server error.");
  }
});

app.get("/d/:jobId/framed-countdown.mp4", async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const { job, assets } = await loadDownloadJobAssets(jobId);
    const motionFrames = assets.filter((asset) => asset.asset_type === "motion_frame");
    if (motionFrames.length === 0) {
      return res.status(404).send("Motion frames were not uploaded for this job.");
    }

    const outputPath = await ensureFramedCountdownVideo(job, motionFrames);
    res.setHeader("Content-Type", "video/mp4");
    res.setHeader("Cache-Control", "public, max-age=300");
    return res.sendFile(outputPath);
  } catch (error) {
    console.error("framed_countdown_video_failed", { jobId, error });
    return res.status(error.statusCode || 500).send(error.message || "Internal server error.");
  }
});

app.get("/d/:jobId/countdown-download.mp4", async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const { job, assets } = await loadDownloadJobAssets(jobId);
    const motionFrames = assets.filter((asset) => asset.asset_type === "motion_frame");
    if (motionFrames.length === 0) {
      return res.status(404).send("Motion frames were not uploaded for this job.");
    }

    const outputPath = await ensureFramedCountdownVideo(job, motionFrames);
    return sendAttachmentFile(res, outputPath, "video/mp4", `${jobId}-countdown.mp4`);
  } catch (error) {
    console.error("framed_countdown_video_download_failed", { jobId, error });
    return res.status(error.statusCode || 500).send(error.message || "Internal server error.");
  }
});

app.get("/d/:jobId/image", async (req, res) => {
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
    console.error("image_download_redirect_failed", { jobId, error });
    return res.status(500).send("Internal server error.");
  }
});

app.get("/d/:jobId/image-download", async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const { job, assets } = await loadDownloadJobAssets(jobId);
    const rawCaptures = sortRawCaptureAssets(assets.filter((asset) => asset.asset_type === "raw_capture"));
    if (rawCaptures.length > 0) {
      const outputPath = await ensureFramedPhotoImage(job, rawCaptures);
      return sendAttachmentFile(res, outputPath, "image/png", `${jobId}-photo.png`);
    }

    const composed = findLatestAsset(assets, "composed") || { remote_key: job.remote_asset_key };
    const composedPath = absoluteUploadPath(composed.remote_key);
    return sendAttachmentFile(res, composedPath, composed.content_type || "image/png", `${jobId}-photo.png`);
  } catch (error) {
    console.error("image_download_failed", { jobId, error });
    return res.status(error.statusCode || 500).send(error.message || "Internal server error.");
  }
});

app.get("/d/:jobId", async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const jobResult = await pool.query(
      `SELECT job_id, status, upload_status, remote_asset_key, download_url, session_folder, session_started_at_utc, theme_id, created_at, published_at
       FROM booth_jobs
       WHERE job_id = $1
       LIMIT 1`,
      [jobId]
    );

    if (jobResult.rowCount === 0) {
      return res.status(404).send("Job not found.");
    }

    const assetsResult = await pool.query(
      `SELECT asset_type, remote_key, content_type, original_file_name, created_at
       FROM booth_assets
       WHERE job_id = $1
       ORDER BY created_at ASC`,
      [jobId]
    );

    const job = jobResult.rows[0];
    const assets = assetsResult.rows;
    const rawCaptures = sortRawCaptureAssets(assets.filter((asset) => asset.asset_type === "raw_capture"));
    if ((job.upload_status !== "LINK_READY" || !job.remote_asset_key) && rawCaptures.length < 4) {
      return res.status(202).send("Download is still processing.");
    }

    const composed = findLatestAsset(assets, "composed") || { remote_key: job.remote_asset_key };
    const thumbnail = findLatestAsset(assets, "thumbnail") || composed;
    const liveImage = findLatestAsset(assets, "live_image");
    const motionVideo = findLatestAsset(assets, "motion_video");
    const motionFrames = assets.filter((asset) => asset.asset_type === "motion_frame");
    const page = renderDownloadPage({
      job,
      composed,
      thumbnail,
      liveImage,
      motionVideo,
      rawCaptures,
      motionFrames
    });

    res.setHeader("Content-Type", "text/html; charset=utf-8");
    res.setHeader("Cache-Control", "public, max-age=300");
    res.setHeader("Content-Security-Policy", "default-src 'self'; img-src 'self' data:; media-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'");
    return res.send(page);
  } catch (error) {
    console.error("download_page_failed", { jobId, error });
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
    CREATE TABLE IF NOT EXISTS booth_jobs (
      job_id TEXT PRIMARY KEY,
      device_id TEXT NOT NULL,
      theme_id TEXT,
      status TEXT NOT NULL DEFAULT 'CREATED',
      payment_status TEXT NOT NULL DEFAULT 'UNKNOWN',
      upload_status TEXT NOT NULL DEFAULT 'PENDING',
      amount_minor_units BIGINT NOT NULL DEFAULT 0,
      currency_code TEXT NOT NULL DEFAULT 'THB',
      payment_reference TEXT,
      remote_asset_key TEXT,
      session_folder TEXT,
      session_started_at_utc TIMESTAMPTZ,
      download_url TEXT,
      published_at TIMESTAMPTZ,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
    )`);
  await pool.query(`
    CREATE TABLE IF NOT EXISTS booth_assets (
      id BIGSERIAL PRIMARY KEY,
      job_id TEXT NOT NULL REFERENCES booth_jobs(job_id) ON DELETE CASCADE,
      asset_type TEXT NOT NULL,
      remote_key TEXT NOT NULL,
      content_type TEXT NOT NULL,
      checksum TEXT,
      file_size_bytes BIGINT,
      original_file_name TEXT,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      UNIQUE (job_id, asset_type, remote_key)
    )`);
  await pool.query("ALTER TABLE booth_jobs ADD COLUMN IF NOT EXISTS session_folder TEXT");
  await pool.query("ALTER TABLE booth_jobs ADD COLUMN IF NOT EXISTS session_started_at_utc TIMESTAMPTZ");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_booth_jobs_status ON booth_jobs(status)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_booth_jobs_upload_status ON booth_jobs(upload_status)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_booth_jobs_device_id ON booth_jobs(device_id)");
  await pool.query("CREATE UNIQUE INDEX IF NOT EXISTS idx_booth_jobs_session_folder ON booth_jobs(session_folder) WHERE session_folder IS NOT NULL");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_booth_assets_job_id ON booth_assets(job_id)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_booth_assets_type ON booth_assets(asset_type)");
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

function writeUploadFile(sessionFolder, assetType, file) {
  const originalName = sanitizeFilename(file.originalname || `${assetType}.bin`);
  const assetDirectory = path.join(config.uploadsRoot, "jobs", sessionFolder, assetType);
  fs.mkdirSync(assetDirectory, { recursive: true });

  const extension = path.extname(originalName) || defaultExtensionForMimeType(file.mimetype);
  const safeBaseName = path.basename(originalName, extension);
  const uniqueName = `${safeBaseName}-${Date.now()}${extension}`;
  const absolutePath = path.join(assetDirectory, uniqueName);
  fs.writeFileSync(absolutePath, file.buffer);

  const checksum = sha256(file.buffer);
  return {
    assetType,
    remoteKey: path.posix.join("jobs", sessionFolder, assetType, uniqueName),
    contentType: file.mimetype || "application/octet-stream",
    checksum,
    fileSizeBytes: file.size ?? file.buffer.length,
    originalFileName: originalName
  };
}

function writeRawCaptureFile(sessionFolder, captureIndex, captureTakenAtUtc, file) {
  const originalName = sanitizeFilename(file.originalname || `capture_${captureIndex}.png`);
  const assetDirectory = path.join(config.uploadsRoot, "jobs", sessionFolder, "raw");
  fs.mkdirSync(assetDirectory, { recursive: true });

  const extension = path.extname(originalName) || defaultExtensionForMimeType(file.mimetype);
  const capturedAt = formatBangkokTimestamp(parseUtcDate(captureTakenAtUtc) || new Date());
  const uniqueName = `capture_${String(captureIndex).padStart(2, "0")}_${capturedAt.time}${extension}`;
  const absolutePath = path.join(assetDirectory, uniqueName);
  fs.writeFileSync(absolutePath, file.buffer);

  const checksum = sha256(file.buffer);
  return {
    assetType: "raw_capture",
    remoteKey: path.posix.join("jobs", sessionFolder, "raw", uniqueName),
    contentType: file.mimetype || "application/octet-stream",
    checksum,
    fileSizeBytes: file.size ?? file.buffer.length,
    originalFileName: originalName
  };
}

async function loadDownloadJobAssets(jobId) {
  const jobResult = await pool.query(
    `SELECT job_id, status, upload_status, remote_asset_key, download_url, session_folder, session_started_at_utc, theme_id, created_at, published_at
     FROM booth_jobs
     WHERE job_id = $1
     LIMIT 1`,
    [jobId]
  );

  if (jobResult.rowCount === 0) {
    throw httpError(404, "JOB_NOT_FOUND", "Job not found.");
  }

  const assetsResult = await pool.query(
    `SELECT asset_type, remote_key, content_type, original_file_name, created_at
     FROM booth_assets
     WHERE job_id = $1
     ORDER BY created_at ASC`,
    [jobId]
  );

  const job = jobResult.rows[0];
  const assets = assetsResult.rows;
  const rawCaptureCount = assets.filter((asset) => asset.asset_type === "raw_capture").length;
  if ((job.upload_status !== "LINK_READY" || !job.remote_asset_key) && rawCaptureCount < 4) {
    throw httpError(202, "DOWNLOAD_PROCESSING", "Download is still processing.");
  }

  return { job, assets };
}

function absoluteUploadPath(remoteKey) {
  const absolutePath = path.resolve(config.uploadsRoot, remoteKey || "");
  if (!absolutePath.startsWith(`${config.uploadsRoot}${path.sep}`)) {
    throw httpError(400, "INVALID_ASSET_PATH", "Asset path is invalid.");
  }

  if (!fs.existsSync(absolutePath)) {
    throw httpError(404, "ASSET_NOT_FOUND", "Asset file was not found.");
  }

  return absolutePath;
}

function generatedDirectory(job) {
  const folder = normalizeOptional(job.session_folder) || sanitizeFilename(job.job_id);
  const directory = path.join(config.uploadsRoot, "jobs", folder, "generated");
  fs.mkdirSync(directory, { recursive: true });
  return directory;
}

function sendAttachmentFile(res, absolutePath, contentType, fileName) {
  const safeName = sanitizeFilename(fileName || "download.bin");
  const encodedName = encodeURIComponent(safeName);
  res.setHeader("Content-Type", contentType || "application/octet-stream");
  res.setHeader("Content-Disposition", `attachment; filename="${safeName}"; filename*=UTF-8''${encodedName}`);
  res.setHeader("Content-Length", fs.statSync(absolutePath).size);
  res.setHeader("Cache-Control", "private, max-age=0, no-store");
  return res.sendFile(absolutePath);
}

async function ensureFramedPhotoImage(job, rawCaptures) {
  const outputPath = path.join(generatedDirectory(job), "photo_4096.png");
  if (fs.existsSync(outputPath)) {
    return outputPath;
  }

  const rawPaths = rawCaptures.slice(0, 4).map((asset) => absoluteUploadPath(asset.remote_key));
  await renderFramedPng(rawPaths, outputPath);
  return outputPath;
}

async function ensureFramedLiveviewVideo(job, rawCaptures) {
  const outputPath = path.join(generatedDirectory(job), "liveview_1080_4fps.mp4");
  if (fs.existsSync(outputPath)) {
    return outputPath;
  }

  const rawPaths = rawCaptures.slice(0, 4).map((asset) => absoluteUploadPath(asset.remote_key));
  const frameSets = rawPaths.map((_, rotation) => rawPaths.map((__, slotIndex) => rawPaths[(slotIndex + rotation) % rawPaths.length]));
  await renderFramedVideo(frameSets, 0.25, outputPath, path.join(generatedDirectory(job), "liveview_frames"));
  return outputPath;
}

async function ensureFramedCountdownVideo(job, motionFrames) {
  const outputPath = path.join(generatedDirectory(job), "framed-countdown_1080.mp4");
  if (fs.existsSync(outputPath)) {
    return outputPath;
  }

  const slotFrames = buildCountdownSlotFrameAssets(motionFrames)
    .map((frames) => frames.map((asset) => absoluteUploadPath(asset.remote_key)));
  const maxFrameCount = Math.max(...slotFrames.map((frames) => frames.length));
  if (maxFrameCount <= 0) {
    throw httpError(404, "MOTION_FRAMES_NOT_FOUND", "Motion frame files were not found.");
  }

  const frameSets = Array.from({ length: maxFrameCount }, (_, frameIndex) =>
    slotFrames.map((frames) => frames.length > 0 ? frames[frameIndex % frames.length] : ""));
  await renderFramedVideo(frameSets, 0.25, outputPath, path.join(generatedDirectory(job), "countdown_frames"));
  return outputPath;
}

async function renderFramedVideo(frameSets, frameDurationSeconds, outputPath, frameDirectory) {
  fs.mkdirSync(frameDirectory, { recursive: true });
  const framePaths = [];
  for (let index = 0; index < frameSets.length; index += 1) {
    const framePath = path.join(frameDirectory, `frame_${String(index).padStart(3, "0")}.png`);
    await renderFramedPng(frameSets[index], framePath);
    framePaths.push(framePath);
  }

  const concatPath = path.join(frameDirectory, "frames.txt");
  const concatLines = [];
  for (const framePath of framePaths) {
    concatLines.push(`file '${escapeFfmpegConcatPath(framePath)}'`);
    concatLines.push(`duration ${frameDurationSeconds}`);
  }

  concatLines.push(`file '${escapeFfmpegConcatPath(framePaths[framePaths.length - 1])}'`);
  fs.writeFileSync(concatPath, concatLines.join("\n"));
  await runFfmpeg([
    "-y",
    "-f", "concat",
    "-safe", "0",
    "-i", concatPath,
    "-vf", "fps=30,scale=1080:1080:flags=lanczos,format=yuv420p",
    "-c:v", "libx264",
    "-preset", "medium",
    "-profile:v", "main",
    "-level", "4.0",
    "-movflags", "+faststart",
    outputPath
  ]);
}

async function renderFramedPng(imagePaths, outputPath) {
  const templatePath = path.join(config.publicRoot, "assets", "piece_03.png");
  if (!fs.existsSync(templatePath)) {
    throw httpError(500, "FRAME_TEMPLATE_NOT_FOUND", "Frame template was not found.");
  }

  const inputs = ["-y", "-i", templatePath];
  const normalizedImagePaths = imagePaths.filter(Boolean).slice(0, 4);
  for (const imagePath of normalizedImagePaths) {
    inputs.push("-i", imagePath);
  }

  const slotDimensions = [
    { x: 680, y: 1565, width: 1267, height: 912 },
    { x: 2143, y: 1565, width: 1267, height: 912 },
    { x: 680, y: 2728, width: 1267, height: 913 },
    { x: 2143, y: 2728, width: 1267, height: 913 }
  ];
  const filters = [];
  let baseLabel = "[0:v]";
  normalizedImagePaths.forEach((_, index) => {
    const slot = slotDimensions[index];
    const scaledLabel = `slot${index}`;
    const outputLabel = index === normalizedImagePaths.length - 1 ? "out" : `tmp${index}`;
    filters.push(`[${index + 1}:v]scale=${slot.width}:${slot.height}:force_original_aspect_ratio=increase,crop=${slot.width}:${slot.height}[${scaledLabel}]`);
    filters.push(`${baseLabel}[${scaledLabel}]overlay=${slot.x}:${slot.y}[${outputLabel}]`);
    baseLabel = `[${outputLabel}]`;
  });

  if (normalizedImagePaths.length === 0) {
    await runFfmpeg([...inputs, "-frames:v", "1", outputPath]);
    return;
  }

  await runFfmpeg([
    ...inputs,
    "-filter_complex", filters.join(";"),
    "-map", "[out]",
    "-frames:v", "1",
    outputPath
  ]);
}

function escapeFfmpegConcatPath(filePath) {
  return String(filePath).replace(/'/g, "'\\''");
}

function runFfmpeg(args) {
  return new Promise((resolve, reject) => {
    const child = spawn(config.ffmpegPath, args, { stdio: ["ignore", "ignore", "pipe"] });
    let stderr = "";
    child.stderr.on("data", (chunk) => {
      stderr += chunk.toString();
    });
    child.on("error", reject);
    child.on("close", (code) => {
      if (code === 0) {
        resolve();
        return;
      }

      reject(new Error(`ffmpeg exited with code ${code}: ${stderr.slice(-1200)}`));
    });
  });
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
        payment_reference,
        session_started_at_utc,
        session_folder
      )
      VALUES ($1, $2, $3, 'CREATED', 'UNKNOWN', 'PENDING', $4, $5, $6, $7, $8)
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
        session_started_at_utc = COALESCE(booth_jobs.session_started_at_utc, EXCLUDED.session_started_at_utc),
        session_folder = COALESCE(booth_jobs.session_folder, EXCLUDED.session_folder),
        updated_at = NOW()`,
    [
      input.jobId,
      input.deviceId,
      input.themeId,
      input.amountMinorUnits,
      input.currencyCode,
      input.paymentReference,
      parseUtcDate(input.sessionStartedAtUtc),
      buildSessionFolder(input.jobId, input.sessionStartedAtUtc)
    ]
  );
}

async function ensureJobSessionFolder(client, jobId, sessionStartedAtUtc) {
  const result = await client.query(
    `SELECT session_folder
     FROM booth_jobs
     WHERE job_id = $1
     LIMIT 1`,
    [jobId]
  );

  const existing = normalizeOptional(result.rows[0]?.session_folder);
  if (existing) {
    return existing;
  }

  const sessionFolder = buildSessionFolder(jobId, sessionStartedAtUtc);
  await client.query(
    `UPDATE booth_jobs
     SET session_folder = $2,
         session_started_at_utc = COALESCE(session_started_at_utc, $3),
         updated_at = NOW()
     WHERE job_id = $1`,
    [jobId, sessionFolder, parseUtcDate(sessionStartedAtUtc)]
  );
  return sessionFolder;
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

function parseUtcDate(value) {
  const normalized = normalizeOptional(value);
  if (!normalized) {
    return null;
  }

  const parsed = new Date(normalized);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

function buildSessionFolder(jobId, sessionStartedAtUtc) {
  const timestamp = formatBangkokTimestamp(parseUtcDate(sessionStartedAtUtc) || new Date());
  return `${timestamp.date}_${timestamp.time}_${sanitizeFilename(jobId)}`;
}

function formatBangkokTimestamp(date) {
  const parts = new Intl.DateTimeFormat("en-CA", {
    timeZone: "Asia/Bangkok",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hourCycle: "h23"
  }).formatToParts(date);

  const byType = Object.fromEntries(parts.map((part) => [part.type, part.value]));
  return {
    date: `${byType.year}${byType.month}${byType.day}`,
    time: `${byType.hour}${byType.minute}${byType.second}`
  };
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

function findLatestAsset(assets, assetType) {
  for (let index = assets.length - 1; index >= 0; index -= 1) {
    if (assets[index]?.asset_type === assetType) {
      return assets[index];
    }
  }

  return null;
}

function sortRawCaptureAssets(assets) {
  return [...assets].sort((left, right) => {
    const leftName = left.original_file_name || left.remote_key || "";
    const rightName = right.original_file_name || right.remote_key || "";
    const leftIndex = captureSortIndex(leftName);
    const rightIndex = captureSortIndex(rightName);
    if (leftIndex !== rightIndex) {
      return leftIndex - rightIndex;
    }

    return String(left.remote_key || "").localeCompare(String(right.remote_key || ""));
  });
}

function captureSortIndex(value) {
  const match = String(value || "").match(/capture[_-]?(\d+)/i);
  return match ? Number.parseInt(match[1], 10) : Number.MAX_SAFE_INTEGER;
}

function motionCaptureSortIndex(value) {
  const match = String(value || "").match(/motion[_-]?(\d+)/i);
  return match ? Number.parseInt(match[1], 10) : Number.MAX_SAFE_INTEGER;
}

function motionFrameSortIndex(value) {
  const match = String(value || "").match(/motion[_-]?\d+[_-](\d+)/i);
  return match ? Number.parseInt(match[1], 10) : Number.MAX_SAFE_INTEGER;
}

function assetUrl(asset) {
  return asset?.remote_key ? `/files/${encodeURIPath(asset.remote_key)}` : "";
}

function formatDisplayDate(value) {
  const date = value ? new Date(value) : new Date();
  if (Number.isNaN(date.getTime())) {
    return "";
  }

  const parts = new Intl.DateTimeFormat("en-US", {
    timeZone: "Asia/Bangkok",
    year: "numeric",
    month: "long",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    hourCycle: "h23"
  }).formatToParts(date).reduce((accumulator, part) => {
    accumulator[part.type] = part.value;
    return accumulator;
  }, {});
  const hour = Number.parseInt(parts.hour || "0", 10);
  const suffix = hour >= 12 ? "PM" : "AM";
  return `${parts.day} ${parts.month} ${parts.year} ${parts.hour}:${parts.minute} ${suffix}`;
}

function renderFramedTemplate(imageUrls, altPrefix, fallbackUrl = "") {
  const slots = [0, 1, 2, 3]
    .map((slotIndex) => {
      const imageUrl = imageUrls[slotIndex];
      if (!imageUrl) {
        return `<div class="frame-slot frame-slot-${slotIndex + 1}"></div>`;
      }

      return `<div class="frame-slot frame-slot-${slotIndex + 1}"><img src="${imageUrl}" alt="${escapeHtml(altPrefix)} ${slotIndex + 1}" loading="${slotIndex === 0 ? "eager" : "lazy"}"></div>`;
    })
    .join("");
  const fallback = fallbackUrl ? `<img class="frame-fallback" src="${fallbackUrl}" alt="${escapeHtml(altPrefix)} fallback">` : "";
  return `<div class="frame-template" aria-label="${escapeHtml(altPrefix)}">${slots}${fallback}</div>`;
}

function renderRotatingFramedLivePhoto(imageUrls) {
  const initialUrls = [0, 1, 2, 3].map((slotIndex) => imageUrls[slotIndex] || "");
  const slots = initialUrls
    .map((imageUrl, slotIndex) => {
      if (!imageUrl) {
        return `<div class="frame-slot frame-slot-${slotIndex + 1}"></div>`;
      }

      return `<div class="frame-slot frame-slot-${slotIndex + 1}"><img id="live-frame-${slotIndex + 1}" src="${imageUrl}" alt="Live photo slot ${slotIndex + 1}" loading="${slotIndex === 0 ? "eager" : "lazy"}"></div>`;
    })
    .join("");

  return `<div class="frame-template" aria-label="Framed live photo">${slots}</div>
  <script>
    (() => {
      const liveFrames = ${JSON.stringify(imageUrls.slice(0, 4))};
      const liveImages = [1, 2, 3, 4].map((slot) => document.getElementById('live-frame-' + slot));
      let rotation = 0;
      function tickLiveFrames() {
        if (liveFrames.length < 2) return;
        liveImages.forEach((image, slotIndex) => {
          if (!image) return;
          image.src = liveFrames[(slotIndex + rotation) % liveFrames.length];
        });
        rotation = (rotation + 1) % liveFrames.length;
      }
      setInterval(tickLiveFrames, 250);
    })();
  </script>`;
}

function buildCountdownSlotFrameUrls(motionFrames) {
  return buildCountdownSlotFrameAssets(motionFrames).map((frames) => frames.map(assetUrl));
}

function buildCountdownSlotFrameAssets(motionFrames) {
  const slotFrameUrls = [[], [], [], []];
  const sortedFrames = [...motionFrames].sort((left, right) => {
    const leftName = left.original_file_name || left.remote_key || "";
    const rightName = right.original_file_name || right.remote_key || "";
    const leftCaptureIndex = motionCaptureSortIndex(leftName);
    const rightCaptureIndex = motionCaptureSortIndex(rightName);
    if (leftCaptureIndex !== rightCaptureIndex) {
      return leftCaptureIndex - rightCaptureIndex;
    }

    const leftFrameIndex = motionFrameSortIndex(leftName);
    const rightFrameIndex = motionFrameSortIndex(rightName);
    if (leftFrameIndex !== rightFrameIndex) {
      return leftFrameIndex - rightFrameIndex;
    }

    return String(left.remote_key || "").localeCompare(String(right.remote_key || ""));
  });

  for (const frame of sortedFrames) {
    const frameName = frame.original_file_name || frame.remote_key || "";
    const captureIndex = motionCaptureSortIndex(frameName);
    const slotIndex = Number.isFinite(captureIndex) ? captureIndex - 1 : -1;
    if (slotIndex >= 0 && slotIndex < slotFrameUrls.length) {
      slotFrameUrls[slotIndex].push(frame);
    }
  }

  return slotFrameUrls;
}

function renderFramedCountdownClip(slotFrameUrls) {
  const slots = slotFrameUrls
    .map((frames, slotIndex) => {
      if (frames.length === 0) {
        return `<div class="frame-slot frame-slot-${slotIndex + 1}"></div>`;
      }

      return `<div class="frame-slot frame-slot-${slotIndex + 1}"><img id="countdown-frame-${slotIndex + 1}" src="${frames[0]}" alt="Countdown capture ${slotIndex + 1}" loading="${slotIndex === 0 ? "eager" : "lazy"}"></div>`;
    })
    .join("");

  return `<div class="frame-template" aria-label="Video Loop">${slots}</div>
  <script>
    (() => {
      const slotFrames = ${JSON.stringify(slotFrameUrls)};
      const slotImages = [1, 2, 3, 4].map((slot) => document.getElementById('countdown-frame-' + slot));
      let frameIndex = 0;
      function tickCountdownFrames() {
        slotFrames.forEach((frames, slotIndex) => {
          const image = slotImages[slotIndex];
          if (!image || frames.length === 0) return;
          image.src = frames[frameIndex % frames.length];
        });
        frameIndex += 1;
      }
      setInterval(tickCountdownFrames, 250);
    })();
  </script>`;
}

function renderDownloadPage({ job, composed, thumbnail, liveImage, motionVideo, rawCaptures, motionFrames }) {
  const jobId = job.job_id;
  const safeJobId = escapeHtml(jobId);
  const displayTitle = escapeHtml(formatSessionTitle(job));
  const takenAt = escapeHtml(formatDisplayDate(job.session_started_at_utc || job.created_at));
  const reference = escapeHtml(shortReference(jobId));
  const imageUrl = assetUrl(composed);
  const thumbnailUrl = assetUrl(thumbnail);
  const heroPreviewUrl = imageUrl || thumbnailUrl;
  const liveImageUrl = assetUrl(liveImage);
  const imageDownloadUrl = `/d/${encodeURIComponent(jobId)}/image-download`;
  const liveviewVideoDownloadUrl = `/d/${encodeURIComponent(jobId)}/liveview-download.mp4`;
  const framedCountdownVideoDownloadUrl = `/d/${encodeURIComponent(jobId)}/countdown-download.mp4`;
  const qrUrl = `/d/${encodeURIComponent(jobId)}/qr`;
  const motionVideoUrl = motionVideo ? `/d/${encodeURIComponent(jobId)}/clip.mp4` : "";
  const motionVideoDownloadUrl = motionVideo ? `/d/${encodeURIComponent(jobId)}/clip-download.mp4` : "";
  const motionClipUrl = motionFrames.length > 0 ? `/d/${encodeURIComponent(jobId)}/clip` : "";
  const rawCaptureUrls = rawCaptures.map(assetUrl).filter(Boolean);
  const countdownSlotFrameUrls = buildCountdownSlotFrameUrls(motionFrames);
  const canDownloadCountdownPreview = countdownSlotFrameUrls.some((frames) => frames.length > 0);
  const canDownloadWebPreview = rawCaptureUrls.length > 0;
  const framedTemplateMarkup = rawCaptureUrls.length > 0
    ? renderFramedTemplate(rawCaptureUrls, "Framed web preview")
    : "";
  const countdownClipMarkup = motionFrames.length > 0
    ? renderFramedCountdownClip(countdownSlotFrameUrls)
    : motionVideoUrl
    ? `<video class="media" controls playsinline loop muted poster="${thumbnailUrl}"><source src="${motionVideoUrl}" type="video/mp4"></video>`
    : motionClipUrl
    ? `<iframe class="media media-frame" src="${motionClipUrl}" title="Countdown Clip" loading="lazy"></iframe>`
    : "";

  const liveViewMarkup = rawCaptureUrls.length > 0
    ? renderRotatingFramedLivePhoto(rawCaptureUrls)
    : liveImageUrl
    ? `<img class="media" src="${liveImageUrl}" alt="Framed live image" loading="eager">`
    : `<img class="media" src="${imageUrl}" alt="Framed liveview fallback" loading="lazy">`;

  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>${displayTitle}</title>
  <style>
    :root {
      color-scheme: light;
      --ink: #050505;
      --lime: #c8e719;
      font-family: Inter, ui-rounded, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      min-height: 100vh;
      color: var(--ink);
      background: #c8e719 url("/assets/website/green-bg.png") center top / cover repeat-y;
    }
    .page { width: min(100%, 640px); margin: 0 auto; padding: 72px 34px 48px; }
    .brand-signature { display: block; width: 284px; max-width: 44vw; margin: 0 auto 82px; }
    .paper-card {
      width: 100%;
      padding: 52px 34px 48px;
      border-radius: 30px 30px 0 0;
      background: #fbf8e8 url("/assets/website/bg.png") center top / 100% auto repeat-y;
      overflow: hidden;
    }
    .metro-logo { display: block; width: 288px; max-width: 80%; margin: 0 0 32px; }
    .taken-at { margin: 0 0 34px; font-size: 25px; line-height: 1; font-weight: 900; color: #050505; }
    .asset-row { display: grid; grid-template-columns: 64px minmax(0, 1fr) minmax(150px, 264px); gap: 20px; align-items: center; margin: 0 0 32px; }
    .asset-row.video-row { margin-top: 54px; }
    .asset-icon { width: 48px; height: 48px; object-fit: contain; }
    .asset-icon.video-icon { width: 56px; height: 56px; }
    .asset-label { min-width: 0; font-size: 29px; line-height: 1; font-weight: 950; color: #050505; overflow-wrap: anywhere; }
    .download-image-button { display: block; width: 264px; height: 66px; background: url("/assets/website/download.png") center / contain no-repeat; text-indent: -9999px; overflow: hidden; border: 0; }
    .download-image-button.button-reset { cursor: pointer; appearance: none; padding: 0; }
    .frame-template { position: relative; width: 100%; aspect-ratio: 1 / 1; margin: 0 auto; background: url("/assets/assets/piece_03.png") center / contain no-repeat; }
    .frame-slot { position: absolute; overflow: hidden; background: #241f1f; }
    .frame-slot img { width: 100%; height: 100%; object-fit: cover; display: block; }
    .frame-slot-1 { left: 16.6016%; top: 38.208%; width: 30.9326%; height: 22.2656%; }
    .frame-slot-2 { left: 52.3193%; top: 38.208%; width: 30.9326%; height: 22.2656%; }
    .frame-slot-3 { left: 16.6016%; top: 66.6016%; width: 30.9326%; height: 22.29%; }
    .frame-slot-4 { left: 52.3193%; top: 66.6016%; width: 30.9326%; height: 22.29%; }
    .video-stage { position: relative; margin-top: 8px; }
    .footer-image { display: block; width: 100%; margin: 96px auto 0; }
    .media { display: block; width: 100%; background: #f7f0ea; }
    img.media { height: auto; }
    .frame-fallback { display: none; }
    @media (max-width: 720px) {
      .page { padding: 46px 16px 34px; overflow: hidden; }
      .brand-signature { margin-bottom: 58px; }
      .paper-card { padding: 42px 22px 42px; }
      .metro-logo { width: 280px; margin-bottom: 30px; }
      .taken-at { font-size: clamp(20px, 5vw, 25px); }
      .asset-row { grid-template-columns: 44px minmax(0, 1fr) clamp(112px, 31vw, 160px); gap: 12px; }
      .asset-icon { width: 44px; height: 44px; }
      .asset-icon.video-icon { width: 48px; height: 48px; }
      .asset-label { font-size: clamp(22px, 6vw, 28px); }
      .download-image-button { width: 100%; height: auto; aspect-ratio: 4 / 1; }
      .footer-image { margin-top: 86px; }
    }
    @media (max-width: 380px) {
      .page { padding-left: 14px; padding-right: 14px; }
      .paper-card { padding-left: 18px; padding-right: 18px; }
      .asset-row { grid-template-columns: 40px minmax(0, 1fr) 104px; gap: 10px; }
      .asset-icon { width: 40px; height: 40px; }
      .asset-icon.video-icon { width: 44px; height: 44px; }
      .asset-label { font-size: 21px; }
    }
  </style>
</head>
<body>
  <main class="page">
    <img class="brand-signature" src="/assets/website/mrkreme-logo.png" alt="MRKREME">
    <section class="paper-card" aria-label="${displayTitle}">
      <img class="metro-logo" src="/assets/website/metro-guide.png" alt="MRKREME Metro Guide">
      <p class="taken-at">${takenAt}</p>
      <div class="asset-row">
        <img class="asset-icon" src="/assets/website/insert-picture-icon.png" alt="">
        <div class="asset-label">Image</div>
        <a class="download-image-button" id="photo-download-button" href="${imageDownloadUrl}" download>Download</a>
      </div>
      ${framedTemplateMarkup || `<img class="media" src="${imageUrl}" alt="Framed picture" loading="eager">`}
      <div class="asset-row video-row">
        <img class="asset-icon video-icon" src="/assets/website/video.png" alt="">
        <div class="asset-label">VDO</div>
        ${canDownloadCountdownPreview ? `<a class="download-image-button" href="${framedCountdownVideoDownloadUrl}" download>Download MP4</a>` : canDownloadWebPreview ? `<a class="download-image-button" href="${liveviewVideoDownloadUrl}" download>Download MP4</a>` : motionVideoDownloadUrl ? `<a class="download-image-button" href="${motionVideoDownloadUrl}" download>Download MP4</a>` : `<span></span>`}
      </div>
      <div class="video-stage">
        ${countdownClipMarkup || liveViewMarkup}
      </div>
      <div class="asset-row video-row">
        <img class="asset-icon video-icon" src="/assets/website/live.png" alt="">
        <div class="asset-label">Liveview</div>
        ${canDownloadWebPreview ? `<a class="download-image-button" href="${liveviewVideoDownloadUrl}" download>Download MP4</a>` : liveImageUrl ? `<a class="download-image-button" href="${liveImageUrl}" download>Download</a>` : `<span></span>`}
      </div>
      <div class="video-stage">
        ${liveViewMarkup}
      </div>
      <img class="footer-image" src="/assets/website/footer.png" alt="©2026 Hello.MRKREME.com">
    </section>
  </main>
  ${canDownloadWebPreview ? `<script>
    (() => {
      const downloadButton = document.getElementById('photo-download-button');
      const templateUrl = '/assets/assets/piece_03.png';
      const rawImages = ${JSON.stringify(rawCaptureUrls.slice(0, 4))};
      const slots = [
        { x: 680, y: 1565, width: 1267, height: 912 },
        { x: 2143, y: 1565, width: 1267, height: 912 },
        { x: 680, y: 2728, width: 1267, height: 913 },
        { x: 2143, y: 2728, width: 1267, height: 913 }
      ];

      function loadImage(url) {
        return new Promise((resolve, reject) => {
          const image = new Image();
          image.onload = () => resolve(image);
          image.onerror = reject;
          image.src = url;
        });
      }

      function drawCover(context, image, slot) {
        const imageAspect = image.naturalWidth / image.naturalHeight;
        const slotAspect = slot.width / slot.height;
        let sourceX = 0;
        let sourceY = 0;
        let sourceWidth = image.naturalWidth;
        let sourceHeight = image.naturalHeight;
        if (imageAspect > slotAspect) {
          sourceWidth = image.naturalHeight * slotAspect;
          sourceX = (image.naturalWidth - sourceWidth) / 2;
        } else {
          sourceHeight = image.naturalWidth / slotAspect;
          sourceY = (image.naturalHeight - sourceHeight) / 2;
        }

        context.drawImage(image, sourceX, sourceY, sourceWidth, sourceHeight, slot.x, slot.y, slot.width, slot.height);
      }

      async function createCanvas() {
        const template = await loadImage(templateUrl);
        const canvas = document.createElement('canvas');
        canvas.width = template.naturalWidth;
        canvas.height = template.naturalHeight;
        const context = canvas.getContext('2d');
        return { canvas, context, template };
      }

      async function drawFrame(context, template, imageUrls) {
        const captures = await Promise.all(imageUrls.map((url) => url ? loadImage(url) : Promise.resolve(null)));
        context.clearRect(0, 0, template.naturalWidth, template.naturalHeight);
        context.drawImage(template, 0, 0);
        captures.forEach((capture, index) => {
          if (capture && slots[index]) {
            drawCover(context, capture, slots[index]);
          }
        });
      }

      async function downloadPreviewImage(event) {
        event.preventDefault();
        try {
          const { canvas, context, template } = await createCanvas();
          await drawFrame(context, template, rawImages);
          const link = document.createElement('a');
          link.download = '${safeJobId}-photo.png';
          link.href = canvas.toDataURL('image/png');
          link.click();
        } catch (error) {
          window.location.href = downloadButton.href;
        }
      }

      // Server-side attachment downloads work more consistently with mobile browsers.
    })();
  </script>` : ""}
</body>
</html>`;
}

function formatSessionTitle(job) {
  return config.downloadPageTitle || "Photo Booth Session";
}

function shortReference(jobId) {
  const normalized = String(jobId || "").replace(/^JOB-?/i, "");
  return normalized.length > 22 ? normalized.slice(-22) : normalized;
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
