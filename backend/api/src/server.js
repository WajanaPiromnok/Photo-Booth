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
const sharp = require("sharp");
const opentype = require("opentype.js");
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
  requiredAdminToken: (process.env.ADMIN_BEARER_TOKEN || "").trim(),
  defaultProjectId: normalizeProjectId(process.env.DEFAULT_PROJECT_ID || "prj_world_tour"),
  allowCorsOrigin: (process.env.CORS_ORIGIN || "*").trim(),
  downloadPageTitle: (process.env.DOWNLOAD_PAGE_TITLE || "MRKREME Photo Session").trim(),
  defaultDownloadRoutePrefix: normalizeDownloadRoutePrefix(process.env.DEFAULT_DOWNLOAD_ROUTE_PREFIX || "world-tour")
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

app.get("/admin/vouchers", (req, res) => {
  res.setHeader("Content-Security-Policy", "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'");
  return res.send(renderAdminVoucherConsolePage());
});

app.get("/api/admin/v1/projects", requireAdminAuth, async (req, res) => {
  try {
    const result = await pool.query(
      `SELECT id, code, name, result_route_prefix, status, created_at, updated_at
       FROM projects
       ORDER BY created_at ASC, id ASC`
    );
    return res.json({ success: true, data: { projects: result.rows }, error: null });
  } catch (error) {
    console.error("projects_list_failed", error);
    return res.status(500).json(errorEnvelope("PROJECTS_LIST_FAILED", error.message));
  }
});

app.get("/api/admin/v1/vouchers", requireAdminAuth, async (req, res) => {
  const projectId = normalizeProjectId(req.query?.project_id);
  try {
    const result = await pool.query(
      `SELECT v.id, v.project_id, p.code AS project_code, v.campaign_id, vc.title AS campaign_name,
              v.code_masked, v.benefit_type, v.benefit_value_minor, v.benefit_percent,
              v.max_uses, v.used_count, v.valid_from, v.valid_until, v.purpose,
              v.status, v.created_by, v.created_at, v.updated_at
       FROM vouchers v
       JOIN projects p ON p.id = v.project_id
       LEFT JOIN voucher_campaigns vc ON vc.id = v.campaign_id
       WHERE ($1::text IS NULL OR v.project_id = $1)
       ORDER BY v.created_at DESC, v.id DESC
       LIMIT 250`,
      [projectId]
    );
    return res.json({ success: true, data: { vouchers: result.rows.map(toVoucherAdminResponse) }, error: null });
  } catch (error) {
    console.error("vouchers_list_failed", { projectId, error });
    return res.status(500).json(errorEnvelope("VOUCHERS_LIST_FAILED", error.message));
  }
});

app.get("/api/admin/v1/redemptions", requireAdminAuth, async (req, res) => {
  const projectId = normalizeProjectId(req.query?.project_id);
  try {
    const result = await pool.query(
      `SELECT vr.id, vr.project_id, p.code AS project_code, vr.voucher_id, v.code_masked,
              vr.job_id, vr.status, vr.gross_amount_minor, vr.discount_amount_minor,
              vr.net_amount_minor, vr.currency_code, vr.reserved_at, vr.applied_at,
              vr.released_at, vr.created_at
       FROM voucher_redemptions vr
       JOIN projects p ON p.id = vr.project_id
       JOIN vouchers v ON v.id = vr.voucher_id
       WHERE ($1::text IS NULL OR vr.project_id = $1)
       ORDER BY vr.created_at DESC, vr.id DESC
       LIMIT 250`,
      [projectId]
    );
    return res.json({ success: true, data: { redemptions: result.rows.map(toRedemptionAdminResponse) }, error: null });
  } catch (error) {
    console.error("redemptions_list_failed", { projectId, error });
    return res.status(500).json(errorEnvelope("REDEMPTIONS_LIST_FAILED", error.message));
  }
});

app.post("/api/admin/v1/vouchers/generate", requireAdminAuth, async (req, res) => {
  const projectId = normalizeProjectId(req.body?.project_id);
  if (!projectId) {
    return res.status(400).json(errorEnvelope("PROJECT_REQUIRED", "project_id is required."));
  }

  const benefitType = normalizeVoucherBenefitType(req.body?.benefit_type);
  if (!benefitType) {
    return res.status(400).json(errorEnvelope("INVALID_BENEFIT_TYPE", "benefit_type is invalid."));
  }

  const quantity = Math.min(Math.max(normalizeInteger(req.body?.quantity, 1), 1), 500);
  const maxUsesPerCode = Math.max(normalizeInteger(req.body?.max_uses_per_code, 1), 1);
  const codeMode = String(req.body?.code_mode || "AUTO").trim().toUpperCase();
  const manualCode = normalizeVoucherCode(req.body?.code_name || req.body?.voucher_code);
  if (codeMode === "MANUAL" && (!manualCode || quantity !== 1)) {
    return res.status(400).json(errorEnvelope("INVALID_MANUAL_CODE", "MANUAL code_mode requires one code_name and quantity must be 1."));
  }

  const client = await pool.connect();
  try {
    await client.query("BEGIN");
    await ensureProject(client, projectId);

    const campaign = await ensureVoucherCampaign(client, {
      projectId,
      title: normalizeOptional(req.body?.campaign_name) || "Manual voucher campaign",
      purpose: normalizeOptional(req.body?.purpose),
      benefitType,
      createdBy: req.adminId
    });

    const codes = [];
    for (let index = 0; index < quantity; index += 1) {
      const voucherCode = codeMode === "MANUAL" ? manualCode : await generateUniqueVoucherCode(client, projectId);
      const result = await client.query(
        `INSERT INTO vouchers (
            project_id,
            campaign_id,
            code_hash,
            code_masked,
            benefit_type,
            benefit_value_minor,
            benefit_percent,
            max_uses,
            valid_from,
            valid_until,
            purpose,
            created_by
          )
          VALUES ($1, $2, $3, $4, $5, $6, $7, $8, COALESCE($9, NOW()), $10, $11, $12)
          RETURNING id, project_id, code_masked, benefit_type, benefit_value_minor, benefit_percent, max_uses, used_count, status, valid_from, valid_until, created_at`,
        [
          projectId,
          campaign.id,
          hashVoucherCode(projectId, voucherCode),
          maskVoucherCode(voucherCode),
          benefitType,
          normalizeVoucherBenefitValueMinor(benefitType, req.body?.benefit_value_minor),
          normalizeVoucherBenefitPercent(benefitType, req.body?.benefit_percent),
          maxUsesPerCode,
          parseUtcDate(req.body?.valid_from),
          parseUtcDate(req.body?.valid_until),
          normalizeOptional(req.body?.purpose),
          req.adminId
        ]
      );

      codes.push({
        voucher_id: result.rows[0].id,
        code: voucherCode,
        ...toVoucherResponse(result.rows[0])
      });
    }

    await client.query("COMMIT");
    return res.status(201).json({
      success: true,
      data: {
        project_id: projectId,
        campaign_id: campaign.id,
        quantity: codes.length,
        vouchers: codes
      },
      error: null
    });
  } catch (error) {
    await client.query("ROLLBACK");
    const statusCode = error.code === "23505" ? 409 : error.statusCode || 500;
    const errorCode = error.code === "23505" ? "VOUCHER_CODE_EXISTS" : error.code || "VOUCHER_GENERATE_FAILED";
    console.error("voucher_generate_failed", { projectId, error });
    return res.status(statusCode).json(errorEnvelope(errorCode, error.message));
  } finally {
    client.release();
  }
});

app.post("/api/admin/v1/projects/:projectId/devices", requireAdminAuth, async (req, res) => {
  const projectId = normalizeProjectId(req.params.projectId);
  const deviceId = normalizeOptional(req.body?.device_id);
  if (!projectId || !deviceId) {
    return res.status(400).json(errorEnvelope("INVALID_PROJECT_DEVICE", "project id and device_id are required."));
  }

  try {
    await ensureProject(pool, projectId);
    const result = await pool.query(
      `INSERT INTO project_devices (
          project_id,
          device_id,
          api_key_hash,
          active
        )
        VALUES ($1, $2, $3, COALESCE($4, TRUE))
        ON CONFLICT (device_id)
        DO UPDATE SET
          project_id = EXCLUDED.project_id,
          api_key_hash = COALESCE(EXCLUDED.api_key_hash, project_devices.api_key_hash),
          active = EXCLUDED.active,
          updated_at = NOW()
        RETURNING project_id, device_id, active, created_at, updated_at`,
      [
        projectId,
        deviceId,
        req.body?.api_key ? sha256(Buffer.from(String(req.body.api_key))) : null,
        req.body?.active === undefined ? true : Boolean(req.body.active)
      ]
    );

    return res.status(201).json({
      success: true,
      data: result.rows[0],
      error: null
    });
  } catch (error) {
    console.error("project_device_upsert_failed", { projectId, deviceId, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "PROJECT_DEVICE_UPSERT_FAILED", error.message));
  }
});

app.post("/api/kiosk/v1/checkout/voucher/validate", requireDeviceAuth, async (req, res) => {
  const jobId = normalizeJobId(req.body?.job_id);
  const voucherCode = normalizeVoucherCode(req.body?.voucher_code);
  if (!jobId || !voucherCode) {
    return res.status(400).json(errorEnvelope("INVALID_VOUCHER_REQUEST", "job_id and voucher_code are required."));
  }

  const deviceId = normalizeOptional(req.get("X-Device-Id")) || normalizeOptional(req.body?.device_id) || "booth-local";
  const client = await pool.connect();
  try {
    const projectId = await resolveProjectIdForDevice(client, deviceId);
    const voucher = await loadUsableVoucher(client, projectId, voucherCode);
    const checkoutToken = buildCheckoutToken(projectId, jobId, voucher.id, voucherCode);
    return res.json({
      success: true,
      data: {
        valid: true,
        project_id: projectId,
        project_code: voucher.project_code,
        voucher_id: voucher.id,
        benefit_type: voucher.benefit_type,
        benefit_value_minor: Number(voucher.benefit_value_minor || 0),
        benefit_percent: Number(voucher.benefit_percent || 0),
        remaining_uses: Math.max(0, Number(voucher.max_uses) - Number(voucher.used_count)),
        checkout_token: checkoutToken
      },
      error: null
    });
  } catch (error) {
    console.error("voucher_validate_failed", { jobId, deviceId, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "VOUCHER_VALIDATE_FAILED", error.message));
  } finally {
    client.release();
  }
});

app.post("/api/kiosk/v1/checkout/voucher/reserve", requireDeviceAuth, async (req, res) => {
  const jobId = normalizeJobId(req.body?.job_id);
  const checkoutToken = normalizeOptional(req.body?.checkout_token);
  const idempotencyKey = normalizeOptional(req.body?.idempotency_key);
  if (!jobId || !checkoutToken || !idempotencyKey) {
    return res.status(400).json(errorEnvelope("INVALID_RESERVATION_REQUEST", "job_id, checkout_token, and idempotency_key are required."));
  }

  const deviceId = normalizeOptional(req.get("X-Device-Id")) || normalizeOptional(req.body?.device_id) || "booth-local";
  const client = await pool.connect();
  try {
    await client.query("BEGIN");
    const projectId = await resolveProjectIdForDevice(client, deviceId);
    const token = verifyCheckoutToken(checkoutToken, projectId, jobId);

    await ensureJob(client, {
      jobId,
      deviceId,
      projectId,
      themeId: normalizeOptional(req.body?.theme_id),
      imagePreviewId: normalizeImagePreviewId(req.body?.image_preview_id || req.body?.theme_id),
      passengerName: normalizePassengerName(req.body?.passenger_name),
      amountMinorUnits: normalizeInteger(req.body?.amount_minor_units, 0),
      currencyCode: normalizeCurrency(req.body?.currency),
      paymentReference: normalizeOptional(req.body?.payment_reference),
      sessionStartedAtUtc: normalizeOptional(req.body?.session_started_at_utc)
    });

    const existing = await client.query(
      `SELECT id, project_id, job_id, status, gross_amount_minor, discount_amount_minor, net_amount_minor, currency_code
       FROM voucher_redemptions
       WHERE idempotency_key = $1
       LIMIT 1`,
      [idempotencyKey]
    );
    if (existing.rowCount > 0) {
      const row = existing.rows[0];
      if (row.project_id !== projectId || row.job_id !== jobId) {
        throw httpError(409, "IDEMPOTENCY_KEY_CONFLICT", "idempotency_key was already used for another checkout.");
      }

      await client.query("COMMIT");
      return res.json({
        success: true,
        data: {
          redemption_id: row.id,
          status: row.status,
          gross_amount_minor: Number(row.gross_amount_minor || 0),
          discount_amount_minor: Number(row.discount_amount_minor || 0),
          net_amount_minor: Number(row.net_amount_minor || 0),
          currency_code: row.currency_code,
          payment_required: Number(row.net_amount_minor || 0) > 0
        },
        error: null
      });
    }

    const voucher = await lockUsableVoucher(client, projectId, token.voucherId);
    const grossAmountMinor = Math.max(0, normalizeInteger(req.body?.gross_amount_minor ?? req.body?.amount_minor_units, 0));
    const currencyCode = normalizeCurrency(req.body?.currency);
    const discountAmountMinor = calculateVoucherDiscount(voucher, grossAmountMinor);
    const netAmountMinor = Math.max(0, grossAmountMinor - discountAmountMinor);
    const finalStatus = netAmountMinor === 0 ? "APPLIED" : "RESERVED";

    const redemption = await client.query(
      `INSERT INTO voucher_redemptions (
          project_id,
          voucher_id,
          job_id,
          status,
          idempotency_key,
          gross_amount_minor,
          discount_amount_minor,
          net_amount_minor,
          currency_code,
          applied_at
        )
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, CASE WHEN $4 = 'APPLIED' THEN NOW() ELSE NULL END)
        RETURNING id, status, gross_amount_minor, discount_amount_minor, net_amount_minor, currency_code`,
      [
        projectId,
        voucher.id,
        jobId,
        finalStatus,
        idempotencyKey,
        grossAmountMinor,
        discountAmountMinor,
        netAmountMinor,
        currencyCode
      ]
    );

    await consumeVoucherUse(client, voucher.id);

    await client.query(
      `UPDATE booth_jobs
       SET checkout_status = $2,
           payment_status = $3,
           voucher_redemption_id = $4,
           amount_minor_units = $5,
           currency_code = $6,
           updated_at = NOW()
       WHERE job_id = $1`,
      [
        jobId,
        netAmountMinor === 0 ? "READY_TO_CAPTURE" : "WAITING_FOR_PAYMENT",
        netAmountMinor === 0 ? "WAIVED_BY_VOUCHER" : "PENDING",
        redemption.rows[0].id,
        grossAmountMinor,
        currencyCode
      ]
    );

    await client.query("COMMIT");
    return res.status(201).json({
      success: true,
      data: {
        redemption_id: redemption.rows[0].id,
        status: redemption.rows[0].status,
        gross_amount_minor: Number(redemption.rows[0].gross_amount_minor || 0),
        discount_amount_minor: Number(redemption.rows[0].discount_amount_minor || 0),
        net_amount_minor: Number(redemption.rows[0].net_amount_minor || 0),
        currency_code: redemption.rows[0].currency_code,
        payment_required: netAmountMinor > 0
      },
      error: null
    });
  } catch (error) {
    await client.query("ROLLBACK");
    console.error("voucher_reserve_failed", { jobId, deviceId, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "VOUCHER_RESERVE_FAILED", error.message));
  } finally {
    client.release();
  }
});

app.post("/api/kiosk/v1/checkout/voucher/:redemptionId/apply", requireDeviceAuth, async (req, res) => {
  const redemptionId = normalizeInteger(req.params.redemptionId, 0);
  if (redemptionId < 1) {
    return res.status(400).json(errorEnvelope("INVALID_REDEMPTION_ID", "redemption id is invalid."));
  }

  const client = await pool.connect();
  try {
    await client.query("BEGIN");
    const deviceId = normalizeOptional(req.get("X-Device-Id")) || normalizeOptional(req.body?.device_id) || "booth-local";
    const projectId = await resolveProjectIdForDevice(client, deviceId);
    const result = await client.query(
      `SELECT vr.id, vr.status, vr.voucher_id, vr.job_id, vr.net_amount_minor
       FROM voucher_redemptions vr
       WHERE vr.id = $1 AND vr.project_id = $2
       FOR UPDATE`,
      [redemptionId, projectId]
    );
    if (result.rowCount === 0) {
      throw httpError(404, "REDEMPTION_NOT_FOUND", "Voucher redemption was not found.");
    }

    const redemption = result.rows[0];
    if (redemption.status === "RELEASED") {
      throw httpError(409, "REDEMPTION_RELEASED", "Voucher redemption was already released.");
    }
    if (redemption.status !== "APPLIED") {
      await client.query(
        `UPDATE voucher_redemptions
         SET status = 'APPLIED',
             applied_at = COALESCE(applied_at, NOW())
         WHERE id = $1`,
        [redemptionId]
      );
    }

    await client.query(
      `UPDATE booth_jobs
       SET checkout_status = 'READY_TO_CAPTURE',
           payment_status = CASE WHEN $2 = 0 THEN 'WAIVED_BY_VOUCHER' ELSE 'CONFIRMED' END,
           updated_at = NOW()
       WHERE job_id = $1`,
      [redemption.job_id, Number(redemption.net_amount_minor || 0)]
    );

    await client.query("COMMIT");
    return res.json({ success: true, data: { redemption_id: redemptionId, status: "APPLIED" }, error: null });
  } catch (error) {
    await client.query("ROLLBACK");
    console.error("voucher_apply_failed", { redemptionId, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "VOUCHER_APPLY_FAILED", error.message));
  } finally {
    client.release();
  }
});

app.post("/api/kiosk/v1/checkout/voucher/:redemptionId/release", requireDeviceAuth, async (req, res) => {
  const redemptionId = normalizeInteger(req.params.redemptionId, 0);
  if (redemptionId < 1) {
    return res.status(400).json(errorEnvelope("INVALID_REDEMPTION_ID", "redemption id is invalid."));
  }

  try {
    const deviceId = normalizeOptional(req.get("X-Device-Id")) || normalizeOptional(req.body?.device_id) || "booth-local";
    const projectId = await resolveProjectIdForDevice(pool, deviceId);
    const client = await pool.connect();
    try {
      await client.query("BEGIN");
      const result = await client.query(
        `UPDATE voucher_redemptions
         SET status = 'RELEASED',
             released_at = COALESCE(released_at, NOW())
         WHERE id = $1 AND project_id = $2 AND status = 'RESERVED'
         RETURNING id, job_id, voucher_id`,
        [redemptionId, projectId]
      );
      if (result.rowCount > 0) {
        await releaseVoucherUse(client, result.rows[0].voucher_id);
        await client.query(
          `UPDATE booth_jobs
           SET checkout_status = 'CHECKOUT_CREATED',
               voucher_redemption_id = NULL,
               updated_at = NOW()
           WHERE job_id = $1`,
          [result.rows[0].job_id]
        );
      }

      await client.query("COMMIT");
      return res.json({ success: true, data: { redemption_id: redemptionId, released: result.rowCount > 0 }, error: null });
    } catch (error) {
      await client.query("ROLLBACK");
      throw error;
    } finally {
      client.release();
    }
  } catch (error) {
    console.error("voucher_release_failed", { redemptionId, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "VOUCHER_RELEASE_FAILED", error.message));
  }
});

app.post("/api/kiosk/v1/payments/create", requireDeviceAuth, async (req, res) => {
  const jobId = normalizeJobId(req.body?.job_id);
  const redemptionId = normalizeInteger(req.body?.redemption_id, 0);
  if (!jobId) {
    return res.status(400).json(errorEnvelope("INVALID_PAYMENT_REQUEST", "job_id is required."));
  }

  const deviceId = normalizeOptional(req.get("X-Device-Id")) || normalizeOptional(req.body?.device_id) || "booth-local";
  const client = await pool.connect();
  try {
    await client.query("BEGIN");
    const projectId = await resolveProjectIdForDevice(client, deviceId);
    const jobResult = await client.query(
      `SELECT job_id, project_id, amount_minor_units, currency_code
       FROM booth_jobs
       WHERE job_id = $1 AND project_id = $2
       LIMIT 1`,
      [jobId, projectId]
    );
    if (jobResult.rowCount === 0) {
      throw httpError(404, "JOB_NOT_FOUND", "Job was not found for this project.");
    }

    let grossAmountMinor = Number(jobResult.rows[0].amount_minor_units || 0);
    let discountAmountMinor = 0;
    let netAmountMinor = grossAmountMinor;
    let currencyCode = jobResult.rows[0].currency_code || "THB";
    let voucherRedemptionId = null;

    if (redemptionId > 0) {
      const redemptionResult = await client.query(
        `SELECT id, status, gross_amount_minor, discount_amount_minor, net_amount_minor, currency_code
         FROM voucher_redemptions
         WHERE id = $1 AND project_id = $2 AND job_id = $3
         LIMIT 1`,
        [redemptionId, projectId, jobId]
      );
      if (redemptionResult.rowCount === 0) {
        throw httpError(404, "REDEMPTION_NOT_FOUND", "Voucher redemption was not found for this job.");
      }

      const redemption = redemptionResult.rows[0];
      if (redemption.status === "RELEASED") {
        throw httpError(409, "REDEMPTION_RELEASED", "Voucher redemption was already released.");
      }

      voucherRedemptionId = redemption.id;
      grossAmountMinor = Number(redemption.gross_amount_minor || 0);
      discountAmountMinor = Number(redemption.discount_amount_minor || 0);
      netAmountMinor = Number(redemption.net_amount_minor || 0);
      currencyCode = redemption.currency_code || currencyCode;
    }

    const paymentResult = await client.query(
      `INSERT INTO payments (
          project_id,
          job_id,
          voucher_redemption_id,
          gross_amount_minor,
          discount_amount_minor,
          net_amount_minor,
          currency_code,
          method,
          provider,
          status
        )
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
        RETURNING id, status, net_amount_minor, currency_code`,
      [
        projectId,
        jobId,
        voucherRedemptionId,
        grossAmountMinor,
        discountAmountMinor,
        netAmountMinor,
        currencyCode,
        normalizeOptional(req.body?.method) || "UNSPECIFIED",
        normalizeOptional(req.body?.provider) || "manual",
        netAmountMinor === 0 ? "WAIVED" : "PENDING"
      ]
    );

    await client.query(
      `UPDATE booth_jobs
       SET checkout_status = $2,
           payment_status = $3,
           updated_at = NOW()
       WHERE job_id = $1`,
      [
        jobId,
        netAmountMinor === 0 ? "READY_TO_CAPTURE" : "PAYMENT_PENDING",
        netAmountMinor === 0 ? "WAIVED_BY_VOUCHER" : "PENDING"
      ]
    );

    await client.query("COMMIT");
    return res.status(201).json({
      success: true,
      data: {
        payment_id: paymentResult.rows[0].id,
        status: paymentResult.rows[0].status,
        net_amount_minor: Number(paymentResult.rows[0].net_amount_minor || 0),
        currency_code: paymentResult.rows[0].currency_code,
        payment_payload: null
      },
      error: null
    });
  } catch (error) {
    await client.query("ROLLBACK");
    console.error("payment_create_failed", { jobId, redemptionId, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "PAYMENT_CREATE_FAILED", error.message));
  } finally {
    client.release();
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
    const imagePreviewId = normalizeImagePreviewId(req.body.image_preview_id || themeId);
    const currencyCode = normalizeCurrency(req.body.currency);
    const amountMinorUnits = normalizeInteger(req.body.amount_minor_units, 0);
    const paymentReference = normalizeOptional(req.body.payment_reference);
    const sessionStartedAtUtc = normalizeOptional(req.body.session_started_at_utc);
    const passengerName = normalizePassengerName(req.body.passenger_name);

    await ensureJob(client, {
      jobId,
      deviceId,
      themeId,
      imagePreviewId,
      passengerName,
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
      imagePreviewId: normalizeImagePreviewId(req.body.image_preview_id || req.body.theme_id),
      passengerName: normalizePassengerName(req.body.passenger_name),
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
    const themeId = normalizeOptional(req.body.theme_id);
    const imagePreviewId = normalizeImagePreviewId(req.body.image_preview_id || themeId);
    const routePrefix = resolveDownloadRoutePrefix(themeId, imagePreviewId);
    const downloadUrl = `${config.publicBaseUrl}/${routePrefix}/${encodeURIComponent(jobId)}`;
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
      imagePreviewId: normalizeImagePreviewId(req.body?.image_preview_id),
      passengerName: normalizePassengerName(req.body?.passenger_name),
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
      `SELECT job_id, status, upload_status, remote_asset_key, theme_id, image_preview_id, passenger_name
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
    const job = jobResult.rows[0];
    const publishPassengerName = normalizePassengerName(req.body?.passenger_name);
    if (publishPassengerName) {
      await client.query(
        `UPDATE booth_jobs
         SET passenger_name = $2,
             updated_at = NOW()
         WHERE job_id = $1`,
        [jobId, publishPassengerName]
      );
      job.passenger_name = publishPassengerName;
    }

    const publishImagePreviewId = normalizeImagePreviewId(req.body?.image_preview_id || job.image_preview_id || job.theme_id);
    if (publishImagePreviewId) {
      await client.query(
        `UPDATE booth_jobs
         SET image_preview_id = $2,
             updated_at = NOW()
         WHERE job_id = $1`,
        [jobId, publishImagePreviewId]
      );
      job.image_preview_id = publishImagePreviewId;
    }

    const themeId = job.theme_id;
    const routePrefix = resolveDownloadRoutePrefix(themeId, job.image_preview_id);
    const downloadUrl = `${config.publicBaseUrl}/${routePrefix}/${encodeURIComponent(jobId)}`;
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
      `SELECT job_id, project_id, checkout_status, voucher_redemption_id, status, payment_status, upload_status, download_url, remote_asset_key, session_folder, session_started_at_utc, theme_id, image_preview_id, passenger_name, amount_minor_units, currency_code, created_at, updated_at
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
        project_id: row.project_id,
        checkout_status: row.checkout_status,
        voucher_redemption_id: row.voucher_redemption_id,
        status: row.status,
        payment_status: row.payment_status,
        upload_status: row.upload_status,
        theme_id: row.theme_id,
        image_preview_id: row.image_preview_id,
        passenger_name: row.passenger_name,
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
    const projectId = await resolveProjectIdForDevice(pool, deviceId);
    const result = await pool.query(
      `INSERT INTO booth_events (
          event_name,
          project_id,
          job_id,
          device_id,
          theme_id,
          screen_id,
          duration_seconds,
          metadata
        )
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb)
        RETURNING id, created_at`,
      [
        eventName,
        projectId,
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

app.get(["/d/:jobId/qr", "/world-tour/:jobId/qr"], async (req, res) => {
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

    const routePrefix = downloadRoutePrefixFromRequest(req);
    const downloadUrl = buildDownloadUrl(routePrefix, jobId);
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

app.get(["/d/:jobId/clip", "/world-tour/:jobId/clip"], async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const routePrefix = downloadRoutePrefixFromRequest(req);
    const jobResult = await pool.query(
      `SELECT job_id, upload_status, session_folder, theme_id, image_preview_id, passenger_name
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

app.get(["/d/:jobId/clip.mp4", "/world-tour/:jobId/clip.mp4"], async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const routePrefix = downloadRoutePrefixFromRequest(req);
    const jobResult = await pool.query(
      `SELECT job_id, upload_status, session_folder, theme_id, image_preview_id, passenger_name
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

    const responsePath = routePrefix === "world-tour"
      ? await ensureWorldTourMotionVideo(jobResult.rows[0], absolutePath)
      : absolutePath;
    res.setHeader("Content-Type", "video/mp4");
    res.setHeader("Cache-Control", "public, max-age=300");
    return res.sendFile(responsePath);
  } catch (error) {
    console.error("motion_video_download_failed", { jobId, error });
    return res.status(500).send("Internal server error.");
  }
});

app.get(["/d/:jobId/clip-download.mp4", "/world-tour/:jobId/clip-download.mp4"], async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const routePrefix = downloadRoutePrefixFromRequest(req);
    const jobResult = await pool.query(
      `SELECT job_id, upload_status, session_folder, theme_id, image_preview_id, passenger_name
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

    const responsePath = routePrefix === "world-tour"
      ? await ensureWorldTourMotionVideo(jobResult.rows[0], absolutePath)
      : absolutePath;
    return sendAttachmentFile(res, responsePath, "video/mp4", `${jobId}-countdown.mp4`);
  } catch (error) {
    console.error("motion_video_attachment_download_failed", { jobId, error });
    return res.status(500).send("Internal server error.");
  }
});

app.get(["/d/:jobId/liveview.mp4", "/world-tour/:jobId/liveview.mp4"], async (req, res) => {
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

app.get(["/d/:jobId/liveview-download.mp4", "/world-tour/:jobId/liveview-download.mp4"], async (req, res) => {
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

app.get(["/d/:jobId/framed-countdown.mp4", "/world-tour/:jobId/framed-countdown.mp4"], async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const routePrefix = downloadRoutePrefixFromRequest(req);
    const { job, assets } = await loadDownloadJobAssets(jobId);
    const motionFrames = assets.filter((asset) => asset.asset_type === "motion_frame");
    if (motionFrames.length === 0) {
      return res.status(404).send("Motion frames were not uploaded for this job.");
    }

    const outputPath = routePrefix === "world-tour"
      ? await ensureWorldTourCountdownVideo(job, motionFrames)
      : await ensureLegacyFramedCountdownVideo(job, motionFrames);
    res.setHeader("Content-Type", "video/mp4");
    res.setHeader("Cache-Control", "public, max-age=300");
    return res.sendFile(outputPath);
  } catch (error) {
    console.error("framed_countdown_video_failed", { jobId, error });
    return res.status(error.statusCode || 500).send(error.message || "Internal server error.");
  }
});

app.get(["/d/:jobId/countdown-download.mp4", "/world-tour/:jobId/countdown-download.mp4"], async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const routePrefix = downloadRoutePrefixFromRequest(req);
    const { job, assets } = await loadDownloadJobAssets(jobId);
    const motionFrames = assets.filter((asset) => asset.asset_type === "motion_frame");
    if (motionFrames.length === 0) {
      return res.status(404).send("Motion frames were not uploaded for this job.");
    }

    const outputPath = routePrefix === "world-tour"
      ? await ensureWorldTourCountdownVideo(job, motionFrames)
      : await ensureLegacyFramedCountdownVideo(job, motionFrames);
    return sendAttachmentFile(res, outputPath, "video/mp4", `${jobId}-countdown.mp4`);
  } catch (error) {
    console.error("framed_countdown_video_download_failed", { jobId, error });
    return res.status(error.statusCode || 500).send(error.message || "Internal server error.");
  }
});

app.get(["/d/:jobId/image", "/world-tour/:jobId/image"], async (req, res) => {
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

app.get(["/d/:jobId/image-download", "/world-tour/:jobId/image-download"], async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const routePrefix = downloadRoutePrefixFromRequest(req);
    const { job, assets } = await loadDownloadJobAssets(jobId);
    const rawCaptures = sortRawCaptureAssets(assets.filter((asset) => asset.asset_type === "raw_capture"));
    if (routePrefix === "world-tour") {
      const photoAsset = rawCaptures[0] || findLatestAsset(assets, "composed") || findLatestAsset(assets, "live_image");
      if (photoAsset?.remote_key) {
        const outputPath = await ensureWorldTourPhotoImage(job, absoluteUploadPath(photoAsset.remote_key));
        return sendAttachmentFile(res, outputPath, "image/jpeg", `${jobId}-photo.jpg`);
      }

      return res.status(202).send("Photo is still processing.");
    }

    if (rawCaptures.length > 0) {
      const outputPath = await ensureLegacyFramedPhotoImage(job, rawCaptures);
      return sendAttachmentFile(res, outputPath, "image/jpeg", `${jobId}-photo.jpg`);
    }

    const composed = findLatestAsset(assets, "composed") || { remote_key: job.remote_asset_key };
    if (composed?.remote_key) {
      const composedPath = absoluteUploadPath(composed.remote_key);
      if (fs.existsSync(composedPath)) {
        const outputPath = await ensureCompressedComposedImage(job, composedPath);
        return sendAttachmentFile(res, outputPath, "image/jpeg", `${jobId}-photo.jpg`);
      }
    }

    return res.status(202).send("Photo is still processing.");
  } catch (error) {
    console.error("image_download_failed", { jobId, error });
    return res.status(error.statusCode || 500).send(error.message || "Internal server error.");
  }
});

app.get(["/d/:jobId", "/world-tour/:jobId"], async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const jobResult = await pool.query(
      `SELECT job_id, status, upload_status, remote_asset_key, download_url, session_folder, session_started_at_utc, theme_id, image_preview_id, passenger_name, created_at, published_at
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

    const routePrefix = downloadRoutePrefixFromRequest(req);
    const renderPage = routePrefix === "world-tour"
      ? renderWorldTourDownloadPage
      : renderLegacyDownloadPage;
    const page = renderPage({
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
    CREATE TABLE IF NOT EXISTS projects (
      id TEXT PRIMARY KEY,
      code TEXT NOT NULL UNIQUE,
      name TEXT NOT NULL,
      result_route_prefix TEXT NOT NULL DEFAULT 'world-tour',
      status TEXT NOT NULL DEFAULT 'ACTIVE',
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
    )`);
  await pool.query(
    `INSERT INTO projects (id, code, name, result_route_prefix, status)
     VALUES
       ('prj_main', 'MAIN', 'Main Photo Booth', 'd', 'ACTIVE'),
       ('prj_world_tour', 'WORLD_TOUR', 'World Tour Photo Booth', 'world-tour', 'ACTIVE')
     ON CONFLICT (id) DO NOTHING`
  );
  await pool.query(
    `INSERT INTO projects (id, code, name, result_route_prefix, status)
     VALUES ($1, $2, $3, $4, 'ACTIVE')
     ON CONFLICT (id) DO NOTHING`,
    [
      config.defaultProjectId,
      config.defaultProjectId.replace(/^prj_/, "").toUpperCase(),
      "Default Photo Booth Project",
      config.defaultDownloadRoutePrefix
    ]
  );
  await pool.query(`
    CREATE TABLE IF NOT EXISTS booth_jobs (
      job_id TEXT PRIMARY KEY,
      project_id TEXT NOT NULL DEFAULT '${config.defaultProjectId}',
      device_id TEXT NOT NULL,
      theme_id TEXT,
      image_preview_id TEXT,
      passenger_name TEXT,
      checkout_status TEXT NOT NULL DEFAULT 'CHECKOUT_CREATED',
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
      voucher_redemption_id BIGINT,
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
  await pool.query("ALTER TABLE booth_jobs ADD COLUMN IF NOT EXISTS project_id TEXT");
  await pool.query("UPDATE booth_jobs SET project_id = $1 WHERE project_id IS NULL", [config.defaultProjectId]);
  await pool.query(`ALTER TABLE booth_jobs ALTER COLUMN project_id SET DEFAULT '${config.defaultProjectId}'`);
  await pool.query("ALTER TABLE booth_jobs ALTER COLUMN project_id SET NOT NULL");
  await pool.query("ALTER TABLE booth_jobs ADD COLUMN IF NOT EXISTS checkout_status TEXT NOT NULL DEFAULT 'CHECKOUT_CREATED'");
  await pool.query("ALTER TABLE booth_jobs ADD COLUMN IF NOT EXISTS voucher_redemption_id BIGINT");
  await pool.query("ALTER TABLE booth_jobs ADD COLUMN IF NOT EXISTS session_folder TEXT");
  await pool.query("ALTER TABLE booth_jobs ADD COLUMN IF NOT EXISTS session_started_at_utc TIMESTAMPTZ");
  await pool.query("ALTER TABLE booth_jobs ADD COLUMN IF NOT EXISTS image_preview_id TEXT");
  await pool.query("ALTER TABLE booth_jobs ADD COLUMN IF NOT EXISTS passenger_name TEXT");
  await pool.query("CREATE UNIQUE INDEX IF NOT EXISTS idx_booth_jobs_project_job ON booth_jobs(project_id, job_id)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_booth_jobs_status ON booth_jobs(status)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_booth_jobs_project_id ON booth_jobs(project_id)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_booth_jobs_checkout_status ON booth_jobs(checkout_status)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_booth_jobs_upload_status ON booth_jobs(upload_status)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_booth_jobs_device_id ON booth_jobs(device_id)");
  await pool.query("CREATE UNIQUE INDEX IF NOT EXISTS idx_booth_jobs_session_folder ON booth_jobs(session_folder) WHERE session_folder IS NOT NULL");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_booth_assets_job_id ON booth_assets(job_id)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_booth_assets_type ON booth_assets(asset_type)");
  await pool.query(`
    CREATE TABLE IF NOT EXISTS project_devices (
      id BIGSERIAL PRIMARY KEY,
      project_id TEXT NOT NULL REFERENCES projects(id),
      device_id TEXT NOT NULL UNIQUE,
      api_key_hash TEXT,
      active BOOLEAN NOT NULL DEFAULT TRUE,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
    )`);
  await pool.query("CREATE INDEX IF NOT EXISTS idx_project_devices_project_id ON project_devices(project_id)");
  await pool.query(`
    CREATE TABLE IF NOT EXISTS voucher_campaigns (
      id BIGSERIAL PRIMARY KEY,
      project_id TEXT NOT NULL REFERENCES projects(id),
      title TEXT NOT NULL,
      purpose TEXT,
      default_benefit_type TEXT,
      status TEXT NOT NULL DEFAULT 'ACTIVE',
      created_by TEXT,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
    )`);
  await pool.query("CREATE INDEX IF NOT EXISTS idx_voucher_campaigns_project_id ON voucher_campaigns(project_id)");
  await pool.query(`
    CREATE TABLE IF NOT EXISTS vouchers (
      id BIGSERIAL PRIMARY KEY,
      project_id TEXT NOT NULL REFERENCES projects(id),
      campaign_id BIGINT REFERENCES voucher_campaigns(id),
      code_hash TEXT NOT NULL,
      code_masked TEXT NOT NULL,
      benefit_type TEXT NOT NULL,
      benefit_value_minor BIGINT NOT NULL DEFAULT 0,
      benefit_percent INTEGER NOT NULL DEFAULT 0,
      max_uses INTEGER NOT NULL DEFAULT 1,
      used_count INTEGER NOT NULL DEFAULT 0,
      valid_from TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      valid_until TIMESTAMPTZ,
      purpose TEXT,
      status TEXT NOT NULL DEFAULT 'ACTIVE',
      created_by TEXT,
      metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      UNIQUE (project_id, code_hash),
      UNIQUE (project_id, id)
    )`);
  await pool.query("CREATE INDEX IF NOT EXISTS idx_vouchers_project_id ON vouchers(project_id)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_vouchers_status ON vouchers(status)");
  await pool.query(`
    CREATE TABLE IF NOT EXISTS voucher_redemptions (
      id BIGSERIAL PRIMARY KEY,
      project_id TEXT NOT NULL,
      voucher_id BIGINT NOT NULL,
      job_id TEXT NOT NULL,
      status TEXT NOT NULL DEFAULT 'RESERVED',
      idempotency_key TEXT NOT NULL UNIQUE,
      gross_amount_minor BIGINT NOT NULL DEFAULT 0,
      discount_amount_minor BIGINT NOT NULL DEFAULT 0,
      net_amount_minor BIGINT NOT NULL DEFAULT 0,
      currency_code TEXT NOT NULL DEFAULT 'THB',
      reserved_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      applied_at TIMESTAMPTZ,
      released_at TIMESTAMPTZ,
      metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      FOREIGN KEY (project_id, voucher_id) REFERENCES vouchers(project_id, id),
      FOREIGN KEY (project_id, job_id) REFERENCES booth_jobs(project_id, job_id)
    )`);
  await pool.query("CREATE INDEX IF NOT EXISTS idx_voucher_redemptions_project_id ON voucher_redemptions(project_id)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_voucher_redemptions_job_id ON voucher_redemptions(job_id)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_voucher_redemptions_voucher_id ON voucher_redemptions(voucher_id)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_voucher_redemptions_status ON voucher_redemptions(status)");
  await pool.query(`
    CREATE TABLE IF NOT EXISTS payments (
      id BIGSERIAL PRIMARY KEY,
      project_id TEXT NOT NULL REFERENCES projects(id),
      job_id TEXT NOT NULL,
      voucher_redemption_id BIGINT REFERENCES voucher_redemptions(id),
      gross_amount_minor BIGINT NOT NULL DEFAULT 0,
      discount_amount_minor BIGINT NOT NULL DEFAULT 0,
      net_amount_minor BIGINT NOT NULL DEFAULT 0,
      currency_code TEXT NOT NULL DEFAULT 'THB',
      method TEXT,
      provider TEXT,
      provider_reference TEXT,
      status TEXT NOT NULL DEFAULT 'PENDING',
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      FOREIGN KEY (project_id, job_id) REFERENCES booth_jobs(project_id, job_id)
    )`);
  await pool.query("CREATE INDEX IF NOT EXISTS idx_payments_project_job ON payments(project_id, job_id)");
  await pool.query(`
    CREATE TABLE IF NOT EXISTS booth_events (
      id BIGSERIAL PRIMARY KEY,
      event_name TEXT NOT NULL,
      project_id TEXT,
      job_id TEXT,
      device_id TEXT NOT NULL,
      theme_id TEXT,
      screen_id TEXT,
      duration_seconds INTEGER,
      metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
    )`);
  await pool.query("ALTER TABLE booth_events ADD COLUMN IF NOT EXISTS project_id TEXT");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_booth_events_event_name ON booth_events(event_name)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_booth_events_project_id ON booth_events(project_id)");
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

function requireAdminAuth(req, res, next) {
  req.adminId = normalizeOptional(req.get("X-Admin-Id")) || "admin";
  if (!config.requiredAdminToken) {
    return res.status(503).json(errorEnvelope("ADMIN_AUTH_NOT_CONFIGURED", "ADMIN_BEARER_TOKEN must be configured before using admin endpoints."));
  }

  const authHeader = normalizeOptional(req.get("Authorization"));
  const expected = `Bearer ${config.requiredAdminToken}`;
  if (authHeader !== expected) {
    return res.status(401).json(errorEnvelope("ADMIN_UNAUTHORIZED", "Admin token is invalid."));
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
    `SELECT job_id, status, upload_status, remote_asset_key, download_url, session_folder, session_started_at_utc, theme_id, image_preview_id, passenger_name, created_at, published_at
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

async function ensureCompressedComposedImage(job, sourcePath) {
  const outputPath = path.join(generatedDirectory(job), "photo_composed_3072_q86.jpg");
  if (fs.existsSync(outputPath)) {
    return outputPath;
  }

  await runFfmpeg([
    "-y",
    "-i", sourcePath,
    "-vf", "scale=3072:3072:flags=lanczos,format=yuvj420p",
    "-q:v", "4",
    outputPath
  ]);
  return outputPath;
}

async function ensureLegacyFramedPhotoImage(job, rawCaptures) {
  const outputPath = path.join(generatedDirectory(job), "photo_legacy_piece03_3072_q86.jpg");
  if (fs.existsSync(outputPath)) {
    return outputPath;
  }

  const sourcePath = path.join(generatedDirectory(job), "photo_legacy_piece03_source_4096.png");
  const rawPaths = rawCaptures.slice(0, 4).map((asset) => absoluteUploadPath(asset.remote_key));
  if (!fs.existsSync(sourcePath)) {
    await renderLegacyFramedPng(rawPaths, sourcePath);
  }

  await compressStillImage(sourcePath, outputPath, "scale=3072:3072:flags=lanczos,format=yuvj420p");
  return outputPath;
}

async function ensureWorldTourPhotoImage(job, photoPath) {
  const labelTemplateId = resolveLabelTemplateId(job.image_preview_id || job.theme_id);
  const nameKey = passengerNameCacheKey(job.passenger_name);
  const outputPath = path.join(generatedDirectory(job), `photo_world-tour_frame-${labelTemplateId}_name-${nameKey}_3072_q86.jpg`);
  if (fs.existsSync(outputPath)) {
    return outputPath;
  }

  const template = resolveWorldTourTemplate(labelTemplateId);
  const templatePath = await ensureWorldTourNamedTemplate(job, labelTemplateId, template.path, template.fromName);
  const sourcePath = path.join(generatedDirectory(job), `photo_world-tour_frame-${labelTemplateId}_name-${nameKey}_source.png`);
  if (!fs.existsSync(sourcePath)) {
    await renderSingleSlotFramedPng(photoPath, sourcePath, templatePath, template.slot);
  }

  await compressStillImage(sourcePath, outputPath, "format=yuvj420p");
  return outputPath;
}

async function compressStillImage(sourcePath, outputPath, videoFilter) {
  await runFfmpeg([
    "-y",
    "-i", sourcePath,
    "-vf", videoFilter,
    "-q:v", "4",
    outputPath
  ]);
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

async function ensureLegacyFramedCountdownVideo(job, motionFrames) {
  const outputPath = path.join(generatedDirectory(job), "framed-countdown_legacy_1080.mp4");
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

async function ensureWorldTourCountdownVideo(job, motionFrames) {
  const labelTemplateId = resolveLabelTemplateId(job.image_preview_id || job.theme_id);
  const nameKey = passengerNameCacheKey(job.passenger_name);
  const outputPath = path.join(generatedDirectory(job), `framed-countdown_world-tour_frame-${labelTemplateId}_name-${nameKey}_v3.mp4`);
  if (fs.existsSync(outputPath)) {
    return outputPath;
  }

  const sortedFrames = sortMotionFrameAssets(motionFrames).map((asset) => absoluteUploadPath(asset.remote_key));
  if (sortedFrames.length === 0) {
    throw httpError(404, "MOTION_FRAMES_NOT_FOUND", "Motion frame files were not found.");
  }

  const template = resolveWorldTourTemplate(labelTemplateId);
  const templatePath = await ensureWorldTourNamedTemplate(job, labelTemplateId, template.path, template.fromName);
  await renderSingleSlotFramedVideo(
    sortedFrames,
    0.25,
    outputPath,
    path.join(generatedDirectory(job), `countdown_world-tour_frame-${labelTemplateId}_name-${nameKey}_frames`),
    templatePath,
    template.slot
  );
  return outputPath;
}

async function ensureWorldTourMotionVideo(job, videoPath) {
  const labelTemplateId = resolveLabelTemplateId(job.image_preview_id || job.theme_id);
  const nameKey = passengerNameCacheKey(job.passenger_name);
  const outputPath = path.join(generatedDirectory(job), `motion-video_world-tour_frame-${labelTemplateId}_name-${nameKey}_v3.mp4`);
  if (fs.existsSync(outputPath)) {
    return outputPath;
  }

  const template = resolveWorldTourTemplate(labelTemplateId);
  const templatePath = await ensureWorldTourNamedTemplate(job, labelTemplateId, template.path, template.fromName);
  await renderSingleSlotFramedMotionVideo(videoPath, outputPath, templatePath, template.slot);
  return outputPath;
}

function passengerNameCacheKey(value) {
  const normalized = normalizePassengerName(value) || "none";
  return crypto.createHash("sha1").update(normalized).digest("hex").slice(0, 10);
}

function worldTourLabelFontPath() {
  return path.join(config.publicRoot, "label", "BatteryPark.ttf");
}

async function ensureWorldTourNamedTemplate(job, labelTemplateId, templatePath, fromName) {
  const passengerName = normalizePassengerName(job.passenger_name);
  if (!passengerName) {
    return templatePath;
  }

  const fontPath = worldTourLabelFontPath();
  if (!fs.existsSync(fontPath)) {
    throw httpError(500, "LABEL_FONT_NOT_FOUND", "Label font was not found.");
  }

  const outputPath = path.join(generatedDirectory(job), `label_world-tour_frame-${labelTemplateId}_name-${passengerNameCacheKey(passengerName)}.png`);
  if (fs.existsSync(outputPath)) {
    return outputPath;
  }

  const template = resolveWorldTourTemplate(labelTemplateId);
  const fontBuffer = fs.readFileSync(fontPath);
  const font = opentype.parse(fontBuffer.buffer.slice(fontBuffer.byteOffset, fontBuffer.byteOffset + fontBuffer.byteLength));
  const baselineY = fromName.y + Math.round(fromName.fontSize * 0.72);
  const textPath = font.getPath(passengerName, fromName.x, baselineY, fromName.fontSize).toPathData(2);
  const svg = `<?xml version="1.0" encoding="UTF-8"?>
<svg width="${template.width}" height="${template.height}" viewBox="0 0 ${template.width} ${template.height}" xmlns="http://www.w3.org/2000/svg">
  <path d="${textPath}" fill="#17477f"/>
</svg>`;

  await sharp(templatePath)
    .composite([{ input: Buffer.from(svg), top: 0, left: 0 }])
    .png()
    .toFile(outputPath);
  return outputPath;
}

function escapeSvgText(value) {
  return String(value ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

async function renderFramedVideo(frameSets, frameDurationSeconds, outputPath, frameDirectory) {
  fs.mkdirSync(frameDirectory, { recursive: true });
  const framePaths = [];
  for (let index = 0; index < frameSets.length; index += 1) {
    const framePath = path.join(frameDirectory, `frame_${String(index).padStart(3, "0")}.png`);
    await renderLegacyFramedPng(frameSets[index], framePath);
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

async function renderSingleSlotFramedVideo(imagePaths, frameDurationSeconds, outputPath, frameDirectory, templatePath, slot) {
  fs.mkdirSync(frameDirectory, { recursive: true });
  const framePaths = [];
  for (let index = 0; index < imagePaths.length; index += 1) {
    const framePath = path.join(frameDirectory, `frame_${String(index).padStart(3, "0")}.png`);
    await renderSingleSlotFramedPng(imagePaths[index], framePath, templatePath, slot);
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
    "-vf", "fps=30,scale=1080:-2:flags=lanczos,format=yuv420p",
    "-c:v", "libx264",
    "-preset", "medium",
    "-profile:v", "main",
    "-level", "4.0",
    "-movflags", "+faststart",
    outputPath
  ]);
}

async function renderSingleSlotFramedMotionVideo(videoPath, outputPath, templatePath, slot) {
  if (!fs.existsSync(templatePath)) {
    throw httpError(500, "FRAME_TEMPLATE_NOT_FOUND", "Frame template was not found.");
  }

  if (!fs.existsSync(videoPath)) {
    throw httpError(404, "VIDEO_SOURCE_NOT_FOUND", "Video source file was not found.");
  }

  const filterParts = [
    `[1:v]scale=${slot.width}:${slot.height}:force_original_aspect_ratio=increase,crop=${slot.width}:${slot.height}[photo]`,
    `[0:v][photo]overlay=${slot.x}:${slot.y}:shortest=1,scale=1080:-2:flags=lanczos,format=yuv420p[scaled]`
  ];

  await runFfmpeg([
    "-y",
    "-loop", "1",
    "-i", templatePath,
    "-i", videoPath,
    "-filter_complex", filterParts.join(";"),
    "-map", "[scaled]",
    "-an",
    "-c:v", "libx264",
    "-preset", "medium",
    "-profile:v", "main",
    "-level", "4.0",
    "-movflags", "+faststart",
    "-shortest",
    outputPath
  ]);
}

async function renderLegacyFramedPng(imagePaths, outputPath) {
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

async function renderSingleSlotFramedPng(imagePath, outputPath, templatePath, slot) {
  if (!fs.existsSync(templatePath)) {
    throw httpError(500, "FRAME_TEMPLATE_NOT_FOUND", "Frame template was not found.");
  }

  if (!fs.existsSync(imagePath)) {
    throw httpError(404, "PHOTO_SOURCE_NOT_FOUND", "Photo source file was not found.");
  }

  const scaledLabel = "photo";
  const filterParts = [
    `[1:v]scale=${slot.width}:${slot.height}:force_original_aspect_ratio=increase,crop=${slot.width}:${slot.height}[${scaledLabel}]`,
    `[0:v][${scaledLabel}]overlay=${slot.x}:${slot.y}[out]`
  ];

  await runFfmpeg([
    "-y",
    "-i", templatePath,
    "-i", imagePath,
    "-filter_complex", filterParts.join(";"),
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
  const projectId = normalizeProjectId(input.projectId) || await resolveProjectIdForDevice(client, input.deviceId);
  const existingJob = await client.query(
    `SELECT project_id
     FROM booth_jobs
     WHERE job_id = $1
     LIMIT 1`,
    [input.jobId]
  );
  if (existingJob.rowCount > 0 && existingJob.rows[0].project_id !== projectId) {
    throw httpError(409, "JOB_PROJECT_MISMATCH", "Existing job belongs to a different project.");
  }

  await client.query(
    `INSERT INTO booth_jobs (
        job_id,
        project_id,
        device_id,
        theme_id,
        image_preview_id,
        passenger_name,
        checkout_status,
        status,
        payment_status,
        upload_status,
        amount_minor_units,
        currency_code,
        payment_reference,
        session_started_at_utc,
        session_folder
      )
      VALUES ($1, $2, $3, $4, $5, $6, 'CHECKOUT_CREATED', 'CREATED', 'UNKNOWN', 'PENDING', $7, $8, $9, $10, $11)
      ON CONFLICT (job_id)
      DO UPDATE SET
        device_id = COALESCE(NULLIF(EXCLUDED.device_id, ''), booth_jobs.device_id),
        theme_id = COALESCE(EXCLUDED.theme_id, booth_jobs.theme_id),
        image_preview_id = COALESCE(EXCLUDED.image_preview_id, booth_jobs.image_preview_id),
        passenger_name = COALESCE(EXCLUDED.passenger_name, booth_jobs.passenger_name),
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
      projectId,
      input.deviceId,
      input.themeId,
      input.imagePreviewId,
      input.passengerName,
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

async function ensureProject(client, projectId) {
  const normalizedProjectId = normalizeProjectId(projectId);
  if (!normalizedProjectId) {
    throw httpError(400, "PROJECT_REQUIRED", "project_id is required.");
  }

  const result = await client.query(
    `SELECT id
     FROM projects
     WHERE id = $1 AND status = 'ACTIVE'
     LIMIT 1`,
    [normalizedProjectId]
  );
  if (result.rowCount === 0) {
    throw httpError(404, "PROJECT_NOT_FOUND", `Project ${normalizedProjectId} was not found or inactive.`);
  }
}

async function resolveProjectIdForDevice(queryable, deviceId) {
  const normalizedDeviceId = normalizeOptional(deviceId);
  if (normalizedDeviceId) {
    const result = await queryable.query(
      `SELECT project_id
       FROM project_devices
       WHERE device_id = $1 AND active = TRUE
       LIMIT 1`,
      [normalizedDeviceId]
    );
    if (result.rowCount > 0) {
      return result.rows[0].project_id;
    }
  }

  return config.defaultProjectId;
}

async function ensureVoucherCampaign(client, input) {
  const result = await client.query(
    `INSERT INTO voucher_campaigns (
        project_id,
        title,
        purpose,
        default_benefit_type,
        created_by
      )
      VALUES ($1, $2, $3, $4, $5)
      RETURNING id`,
    [
      input.projectId,
      input.title,
      input.purpose,
      input.benefitType,
      input.createdBy
    ]
  );
  return result.rows[0];
}

async function generateUniqueVoucherCode(client, projectId) {
  for (let attempt = 0; attempt < 12; attempt += 1) {
    const code = `PB-${crypto.randomBytes(4).toString("hex").toUpperCase()}`;
    const result = await client.query(
      `SELECT 1
       FROM vouchers
       WHERE project_id = $1 AND code_hash = $2
       LIMIT 1`,
      [projectId, hashVoucherCode(projectId, code)]
    );
    if (result.rowCount === 0) {
      return code;
    }
  }

  throw httpError(500, "VOUCHER_CODE_GENERATION_FAILED", "Failed to generate a unique voucher code.");
}

async function loadUsableVoucher(client, projectId, voucherCode) {
  const result = await client.query(
    `SELECT v.*, p.code AS project_code
     FROM vouchers v
     JOIN projects p ON p.id = v.project_id
     WHERE v.project_id = $1
       AND v.code_hash = $2
       AND v.status = 'ACTIVE'
       AND p.status = 'ACTIVE'
       AND v.valid_from <= NOW()
       AND (v.valid_until IS NULL OR v.valid_until >= NOW())
     LIMIT 1`,
    [projectId, hashVoucherCode(projectId, voucherCode)]
  );
  if (result.rowCount === 0) {
    throw httpError(404, "VOUCHER_NOT_FOUND", "Voucher was not found for this project or is not active.");
  }

  const voucher = result.rows[0];
  if (Number(voucher.used_count) >= Number(voucher.max_uses)) {
    throw httpError(409, "VOUCHER_QUOTA_EXHAUSTED", "Voucher usage quota is exhausted.");
  }

  return voucher;
}

async function lockUsableVoucher(client, projectId, voucherId) {
  const result = await client.query(
    `SELECT *
     FROM vouchers
     WHERE project_id = $1
       AND id = $2
       AND status = 'ACTIVE'
       AND valid_from <= NOW()
       AND (valid_until IS NULL OR valid_until >= NOW())
     FOR UPDATE`,
    [projectId, voucherId]
  );
  if (result.rowCount === 0) {
    throw httpError(404, "VOUCHER_NOT_FOUND", "Voucher was not found for this project or is not active.");
  }

  const voucher = result.rows[0];
  if (Number(voucher.used_count) >= Number(voucher.max_uses)) {
    throw httpError(409, "VOUCHER_QUOTA_EXHAUSTED", "Voucher usage quota is exhausted.");
  }

  return voucher;
}

async function consumeVoucherUse(client, voucherId) {
  const result = await client.query(
    `UPDATE vouchers
     SET used_count = used_count + 1,
         updated_at = NOW()
     WHERE id = $1 AND used_count < max_uses
     RETURNING used_count`,
    [voucherId]
  );
  if (result.rowCount === 0) {
    throw httpError(409, "VOUCHER_QUOTA_EXHAUSTED", "Voucher usage quota is exhausted.");
  }
}

async function releaseVoucherUse(client, voucherId) {
  await client.query(
    `UPDATE vouchers
     SET used_count = GREATEST(used_count - 1, 0),
         updated_at = NOW()
     WHERE id = $1`,
    [voucherId]
  );
}

function buildCheckoutToken(projectId, jobId, voucherId, voucherCode) {
  const payload = {
    projectId,
    jobId,
    voucherId,
    codeHash: hashVoucherCode(projectId, voucherCode),
    expiresAt: Date.now() + 10 * 60 * 1000
  };
  const encodedPayload = Buffer.from(JSON.stringify(payload)).toString("base64url");
  const signature = crypto
    .createHmac("sha256", checkoutTokenSecret())
    .update(encodedPayload)
    .digest("base64url");
  return `${encodedPayload}.${signature}`;
}

function verifyCheckoutToken(token, projectId, jobId) {
  const [encodedPayload, signature] = String(token || "").split(".");
  if (!encodedPayload || !signature) {
    throw httpError(401, "INVALID_CHECKOUT_TOKEN", "Checkout token is invalid.");
  }

  const expected = crypto
    .createHmac("sha256", checkoutTokenSecret())
    .update(encodedPayload)
    .digest("base64url");
  const signatureBuffer = Buffer.from(signature);
  const expectedBuffer = Buffer.from(expected);
  if (signatureBuffer.length !== expectedBuffer.length || !crypto.timingSafeEqual(signatureBuffer, expectedBuffer)) {
    throw httpError(401, "INVALID_CHECKOUT_TOKEN", "Checkout token signature is invalid.");
  }

  let payload;
  try {
    payload = JSON.parse(Buffer.from(encodedPayload, "base64url").toString("utf8"));
  } catch {
    throw httpError(401, "INVALID_CHECKOUT_TOKEN", "Checkout token payload is invalid.");
  }

  if (payload.projectId !== projectId || payload.jobId !== jobId || Date.now() > Number(payload.expiresAt || 0)) {
    throw httpError(401, "INVALID_CHECKOUT_TOKEN", "Checkout token is expired or does not match this job.");
  }

  return payload;
}

function checkoutTokenSecret() {
  return config.requiredDeviceToken || config.requiredAdminToken || "photo-booth-local-dev-token";
}

function calculateVoucherDiscount(voucher, grossAmountMinor) {
  const gross = Math.max(0, Number(grossAmountMinor || 0));
  switch (voucher.benefit_type) {
    case "FREE_SESSION":
    case "STAFF_TEST":
      return gross;
    case "FIXED_DISCOUNT":
      return Math.min(gross, Math.max(0, Number(voucher.benefit_value_minor || 0)));
    case "PERCENT_DISCOUNT":
      return Math.min(gross, Math.floor((gross * Math.max(0, Number(voucher.benefit_percent || 0))) / 100));
    case "FREE_ADDON":
    default:
      return 0;
  }
}

function normalizeProjectId(value) {
  const normalized = normalizeOptional(value);
  if (!normalized) {
    return null;
  }

  return normalized.replace(/[^A-Za-z0-9_-]/g, "_").slice(0, 80);
}

function normalizeVoucherCode(value) {
  const normalized = normalizeOptional(value);
  if (!normalized) {
    return null;
  }

  return normalized.toUpperCase().replace(/\s+/g, "").replace(/[^A-Z0-9._-]/g, "-").slice(0, 80);
}

function hashVoucherCode(projectId, voucherCode) {
  return crypto
    .createHash("sha256")
    .update(`${projectId}:${normalizeVoucherCode(voucherCode)}`)
    .digest("hex");
}

function maskVoucherCode(voucherCode) {
  const normalized = normalizeVoucherCode(voucherCode) || "";
  if (normalized.length <= 8) {
    return normalized;
  }

  return `${normalized.slice(0, 4)}...${normalized.slice(-4)}`;
}

function normalizeVoucherBenefitType(value) {
  const normalized = normalizeOptional(value)?.toUpperCase();
  const allowed = new Set(["FREE_SESSION", "FIXED_DISCOUNT", "PERCENT_DISCOUNT", "FREE_ADDON", "STAFF_TEST"]);
  return allowed.has(normalized) ? normalized : null;
}

function normalizeVoucherBenefitValueMinor(benefitType, value) {
  if (benefitType !== "FIXED_DISCOUNT") {
    return 0;
  }

  return Math.max(0, normalizeInteger(value, 0));
}

function normalizeVoucherBenefitPercent(benefitType, value) {
  if (benefitType !== "PERCENT_DISCOUNT") {
    return 0;
  }

  return Math.min(Math.max(normalizeInteger(value, 0), 0), 100);
}

function toVoucherResponse(row) {
  return {
    project_id: row.project_id,
    code_masked: row.code_masked,
    benefit_type: row.benefit_type,
    benefit_value_minor: Number(row.benefit_value_minor || 0),
    benefit_percent: Number(row.benefit_percent || 0),
    max_uses: Number(row.max_uses || 0),
    used_count: Number(row.used_count || 0),
    status: row.status,
    valid_from: row.valid_from,
    valid_until: row.valid_until,
    created_at: row.created_at
  };
}

function toVoucherAdminResponse(row) {
  return {
    id: row.id,
    project_id: row.project_id,
    project_code: row.project_code,
    campaign_id: row.campaign_id,
    campaign_name: row.campaign_name,
    code_masked: row.code_masked,
    benefit_type: row.benefit_type,
    benefit_value_minor: Number(row.benefit_value_minor || 0),
    benefit_percent: Number(row.benefit_percent || 0),
    max_uses: Number(row.max_uses || 0),
    used_count: Number(row.used_count || 0),
    remaining_uses: Math.max(0, Number(row.max_uses || 0) - Number(row.used_count || 0)),
    valid_from: row.valid_from,
    valid_until: row.valid_until,
    purpose: row.purpose,
    status: row.status,
    created_by: row.created_by,
    created_at: row.created_at,
    updated_at: row.updated_at
  };
}

function toRedemptionAdminResponse(row) {
  return {
    id: row.id,
    project_id: row.project_id,
    project_code: row.project_code,
    voucher_id: row.voucher_id,
    code_masked: row.code_masked,
    job_id: row.job_id,
    status: row.status,
    gross_amount_minor: Number(row.gross_amount_minor || 0),
    discount_amount_minor: Number(row.discount_amount_minor || 0),
    net_amount_minor: Number(row.net_amount_minor || 0),
    currency_code: row.currency_code,
    reserved_at: row.reserved_at,
    applied_at: row.applied_at,
    released_at: row.released_at,
    created_at: row.created_at
  };
}

function normalizeOptional(value) {
  if (value === undefined || value === null) {
    return null;
  }

  const normalized = String(value).trim();
  return normalized === "" ? null : normalized;
}

function normalizePassengerName(value) {
  const normalized = normalizeOptional(value);
  if (!normalized) {
    return null;
  }

  return normalized
    .replace(/\s+/g, " ")
    .toUpperCase()
    .slice(0, 15);
}

function normalizeImagePreviewId(value) {
  const normalized = normalizeOptional(value);
  if (!normalized) {
    return null;
  }

  const lower = normalized.toLowerCase();
  if (lower === "2" || lower === "image_preview_2" || lower === "theme_02" || lower.endsWith("_02")) {
    return "image_preview_2";
  }

  if (lower === "1" || lower === "image_preview_1" || lower === "theme_01" || lower.endsWith("_01")) {
    return "image_preview_1";
  }

  return normalized;
}

function resolveDownloadRoutePrefix(themeId, imagePreviewId) {
  return config.defaultDownloadRoutePrefix;
}

function normalizeDownloadRoutePrefix(value) {
  return String(value || "").trim().toLowerCase() === "d" ? "d" : "world-tour";
}

function downloadRoutePrefixFromRequest(req) {
  return req.path.startsWith("/d/") ? "d" : "world-tour";
}

function buildDownloadUrl(routePrefix, jobId) {
  return `${config.publicBaseUrl}/${normalizeDownloadRoutePrefix(routePrefix)}/${encodeURIComponent(jobId)}`;
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

function resolveWorldTourTemplate(labelTemplateId) {
  const id = labelTemplateId === "2" ? "2" : "1";
  const pathById = {
    "1": path.join(config.publicRoot, "label", "frame-1.png"),
    "2": path.join(config.publicRoot, "label", "frame-2.png")
  };
  const dimensionsById = {
    "1": { width: 2136, height: 3132 },
    "2": { width: 2138, height: 3134 }
  };
  const percentSlotById = {
    "1": { x: 0.029, y: 0.565, width: 0.943, height: 0.36 },
    "2": { x: 0.051, y: 0.2, width: 0.898, height: 0.345 }
  };
  const fromNameById = {
    "1": { x: 167, y: 528, fontSize: 104, color: "0x17477f" },
    "2": { x: 220, y: 1920, fontSize: 104, color: "0x17477f" }
  };
  const dimensions = dimensionsById[id];
  const percentSlot = percentSlotById[id];

  return {
    path: pathById[id],
    width: dimensions.width,
    height: dimensions.height,
    slot: {
      x: Math.round(dimensions.width * percentSlot.x),
      y: Math.round(dimensions.height * percentSlot.y),
      width: Math.round(dimensions.width * percentSlot.width),
      height: Math.round(dimensions.height * percentSlot.height)
    },
    fromName: fromNameById[id]
  };
}

function renderAdminVoucherConsolePage() {
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Photo Booth Voucher Admin</title>
  <style>
    :root { --bg:#f4f4f1; --shell:#10212a; --surface:#fff; --line:#deddd8; --ink:#17232c; --muted:#65717a; --teal:#14627a; --teal-soft:#e5f3f5; --orange:#c95722; --green:#16704d; --red:#b8323d; --mono:ui-monospace,SFMono-Regular,Menlo,Monaco,Consolas,monospace; }
    * { box-sizing: border-box; }
    body { margin:0; min-height:100vh; background:var(--bg); color:var(--ink); font-family:Inter,ui-sans-serif,system-ui,-apple-system,BlinkMacSystemFont,"Segoe UI",Arial,sans-serif; }
    .app { min-height:100vh; display:grid; grid-template-columns:246px minmax(0,1fr); }
    aside { background:var(--shell); color:#d8e8eb; padding:22px 16px; }
    .brand { padding:4px 8px 20px; border-bottom:1px solid rgba(255,255,255,.10); margin-bottom:18px; }
    .brand b { display:block; color:#fff; font-size:19px; margin-bottom:3px; }
    .brand span { color:#9bb9c0; font-size:12px; }
    .nav-item { padding:11px 12px; border-radius:10px; color:#bdd3d8; font-size:13px; margin-bottom:5px; }
    .nav-item.active { background:#173c47; color:#fff; }
    .nav-note { margin-top:22px; border:1px solid rgba(120,205,222,.22); background:rgba(20,98,122,.18); border-radius:13px; padding:13px; color:#cfe3e7; font-size:12px; line-height:1.5; }
    main { min-width:0; padding:24px; }
    .topbar { display:flex; justify-content:space-between; align-items:flex-start; gap:16px; margin-bottom:18px; }
    h1 { margin:0 0 4px; font-size:24px; letter-spacing:0; }
    .sub { color:var(--muted); font-size:13px; }
    .token-box { display:grid; grid-template-columns:minmax(220px,320px) auto; gap:8px; align-items:end; }
    label { display:block; color:var(--muted); font-size:11px; margin-bottom:5px; }
    input, select { width:100%; border:1px solid #d8d7d2; border-radius:9px; padding:10px; background:#fff; color:var(--ink); font:inherit; font-size:13px; }
    input:focus, select:focus { outline:2px solid #cae9ed; border-color:var(--teal); }
    button { border:0; border-radius:10px; padding:11px 13px; background:#e9e9e4; color:var(--ink); font:inherit; font-size:13px; font-weight:700; cursor:pointer; white-space:nowrap; }
    button.primary { background:var(--teal); color:#fff; }
    .grid { display:grid; grid-template-columns:minmax(330px,1fr) minmax(300px,.82fr); gap:15px; align-items:start; }
    .panel { border:1px solid var(--line); background:var(--surface); border-radius:15px; padding:17px; }
    .panel h2 { margin:0 0 14px; font-size:16px; }
    .form-grid { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:10px; }
    .full { grid-column:1/-1; }
    .actions { display:flex; gap:8px; margin-top:14px; }
    .voucher-preview { background:linear-gradient(135deg,#102a34 0%,#194759 100%); color:#fff; border-radius:16px; padding:18px; min-height:220px; position:relative; overflow:hidden; }
    .voucher-preview:after { content:""; position:absolute; width:170px; height:170px; right:-72px; top:-78px; border-radius:50%; background:rgba(105,198,216,.16); }
    .voucher-preview small { color:#a8cdd5; font-size:10px; letter-spacing:.12em; }
    .voucher-preview h3 { margin:12px 0 4px; font-size:18px; }
    .voucher-code { font-family:var(--mono); font-weight:800; letter-spacing:.08em; background:rgba(255,255,255,.12); padding:12px 11px; border-radius:10px; margin:13px 0; word-break:break-all; }
    .preview-cols { display:grid; grid-template-columns:1fr 1fr; gap:9px; }
    .preview-cols div { background:rgba(255,255,255,.08); padding:9px; border-radius:9px; font-size:11px; color:#cfe3e7; }
    .preview-cols b { display:block; color:#fff; font-size:13px; margin-top:3px; }
    .table-panel { grid-column:1/-1; }
    .table-head { display:flex; justify-content:space-between; gap:12px; align-items:center; margin-bottom:8px; }
    .tabs { display:inline-flex; padding:4px; background:#ecece7; border-radius:10px; gap:4px; }
    .tabs button { padding:8px 11px; font-size:12px; background:transparent; }
    .tabs button.active { background:#fff; box-shadow:0 1px 4px rgba(0,0,0,.08); }
    table { width:100%; border-collapse:collapse; font-size:12px; }
    th { text-align:left; color:var(--muted); text-transform:uppercase; letter-spacing:.08em; font-size:10px; padding:9px 8px; border-bottom:1px solid var(--line); }
    td { padding:11px 8px; border-top:1px solid #efeee9; vertical-align:top; }
    code, .mono { font-family:var(--mono); font-size:12px; color:var(--teal); }
    .pill { display:inline-flex; border-radius:999px; padding:4px 8px; font-size:11px; font-weight:800; }
    .pill.green { background:#e7f5ee; color:var(--green); }
    .pill.orange { background:#fff0e8; color:var(--orange); }
    .pill.red { background:#fae9eb; color:var(--red); }
    .pill.teal { background:var(--teal-soft); color:var(--teal); }
    .status { margin-top:12px; min-height:18px; color:var(--muted); font-size:12px; }
    .status.error { color:var(--red); } .status.ok { color:var(--green); }
    .empty { color:var(--muted); padding:18px 8px; }
    @media (max-width:980px) { .app{grid-template-columns:1fr} aside{display:none} main{padding:16px} .topbar,.grid{display:block} .panel{margin-bottom:14px} .token-box{grid-template-columns:1fr;margin-top:12px} .form-grid{grid-template-columns:1fr} table{min-width:780px} .table-scroll{overflow:auto} }
  </style>
</head>
<body>
  <div class="app">
    <aside>
      <div class="brand"><b>Photo Booth Admin</b><span>Voucher Console</span></div>
      <div class="nav-item active">Voucher Codes</div><div class="nav-item">Redemptions</div><div class="nav-item">Project Devices</div>
      <div class="nav-note">Voucher is always resolved by backend project/device mapping. Unity should never reduce quota locally.</div>
    </aside>
    <main>
      <div class="topbar">
        <div><h1>Voucher Codes</h1><div class="sub">Generate voucher codes, bind booth devices, and inspect recent voucher usage.</div></div>
        <form class="token-box" id="tokenForm"><div><label for="adminToken">Admin bearer token</label><input id="adminToken" type="password" autocomplete="off" placeholder="ADMIN_BEARER_TOKEN"></div><button id="saveToken" type="button">Save</button></form>
      </div>
      <div class="grid">
        <section class="panel">
          <h2>Create new voucher</h2>
          <form id="voucherForm">
            <div class="form-grid">
              <div><label for="projectId">Project</label><select id="projectId" name="project_id" required></select></div>
              <div><label for="benefitType">Benefit</label><select id="benefitType" name="benefit_type"><option>FREE_SESSION</option><option>FIXED_DISCOUNT</option><option>PERCENT_DISCOUNT</option><option>FREE_ADDON</option><option>STAFF_TEST</option></select></div>
              <div class="full"><label for="campaignName">Campaign name</label><input id="campaignName" name="campaign_name" value="World Tour VIP"></div>
              <div><label for="codeMode">Code mode</label><select id="codeMode" name="code_mode"><option>MANUAL</option><option>AUTO</option></select></div>
              <div><label for="codeName">Manual code</label><input id="codeName" name="code_name" value="WORLDTOUR-VIP-001"></div>
              <div><label for="quantity">Quantity</label><input id="quantity" name="quantity" type="number" min="1" max="500" value="1"></div>
              <div><label for="maxUses">Max uses per code</label><input id="maxUses" name="max_uses_per_code" type="number" min="1" value="3"></div>
              <div><label for="benefitValue">Fixed discount minor units</label><input id="benefitValue" name="benefit_value_minor" type="number" min="0" value="0"></div>
              <div><label for="benefitPercent">Percent discount</label><input id="benefitPercent" name="benefit_percent" type="number" min="0" max="100" value="0"></div>
              <div class="full"><label for="validUntil">Valid until</label><input id="validUntil" name="valid_until" type="datetime-local"></div>
              <div class="full"><label for="purpose">Purpose</label><input id="purpose" name="purpose" value="Sponsor guest"></div>
            </div>
            <div class="actions"><button class="primary" type="submit">Generate voucher</button><button type="button" id="refreshData">Refresh</button></div>
            <div id="formStatus" class="status"></div>
          </form>
        </section>
        <section class="panel">
          <h2>Preview</h2>
          <div class="voucher-preview"><small>PHOTO BOOTH VOUCHER</small><h3 id="previewCampaign">World Tour VIP</h3><div id="previewCode" class="voucher-code">WORLDTOUR-VIP-001</div><div class="preview-cols"><div>Project<b id="previewProject">WORLD_TOUR</b></div><div>Benefit<b id="previewBenefit">FREE_SESSION</b></div><div>Max uses<b id="previewUses">3</b></div><div>Valid until<b id="previewUntil">No expiry</b></div></div></div>
          <div class="status">Generated codes return once, because the backend stores only hashed codes.</div>
        </section>
        <section class="panel"><h2>Bind booth device</h2><form id="deviceForm"><div class="form-grid"><div><label for="deviceProjectId">Project</label><select id="deviceProjectId" name="project_id" required></select></div><div><label for="deviceId">Device ID</label><input id="deviceId" name="device_id" placeholder="booth-world-tour-01" required></div></div><div class="actions"><button class="primary" type="submit">Bind device</button></div><div id="deviceStatus" class="status"></div></form></section>
        <section class="panel"><h2>Latest generated code</h2><div id="generatedCode" class="empty">No code generated in this browser session.</div></section>
        <section class="panel table-panel"><div class="table-head"><h2 style="margin:0">Records</h2><div class="tabs"><button type="button" id="tabVouchers" class="active">Vouchers</button><button type="button" id="tabRedemptions">Redemptions</button></div></div><div class="table-scroll"><table><thead id="recordsHead"></thead><tbody id="recordsBody"></tbody></table></div></section>
      </div>
    </main>
  </div>
  <script>
    const defaultProjectId = ${JSON.stringify(config.defaultProjectId)};
    const state = { projects: [], vouchers: [], redemptions: [], tab: "vouchers" };
    const tokenInput = document.getElementById("adminToken");
    tokenInput.value = localStorage.getItem("photoBoothAdminToken") || (location.hostname === "localhost" || location.hostname === "127.0.0.1" ? "dev-admin-token" : "");
    function authHeaders(){ const token = tokenInput.value.trim(); return token ? { "Authorization": "Bearer " + token } : {}; }
    async function api(path, options){ const opts = options || {}; const headers = Object.assign({ "Content-Type":"application/json" }, authHeaders(), opts.headers || {}); const response = await fetch(path, Object.assign({}, opts, { headers })); const body = await response.json().catch(() => ({ success:false, error:{ message:response.statusText } })); if (!response.ok || body.success === false) throw new Error((body.error && body.error.message) || response.statusText); return body.data; }
    function setStatus(id, message, kind){ const el = document.getElementById(id); el.textContent = message || ""; el.className = "status" + (kind ? " " + kind : ""); }
    function formatDate(value){ if (!value) return "-"; const date = new Date(value); return Number.isNaN(date.getTime()) ? "-" : date.toLocaleString(); }
    function moneyMinor(value, currency){ const minor = Number(value || 0); return (minor / 100).toLocaleString(undefined, { minimumFractionDigits:2, maximumFractionDigits:2 }) + " " + (currency || "THB"); }
    function escapeHtml(value){ return String(value == null ? "" : value).replace(/&/g,"&amp;").replace(/</g,"&lt;").replace(/>/g,"&gt;").replace(/"/g,"&quot;").replace(/'/g,"&#39;"); }
    function statusPill(value){ const v = String(value || ""); const cls = v === "ACTIVE" || v === "APPLIED" ? "green" : v === "RESERVED" || v === "PENDING" ? "orange" : v === "RELEASED" ? "red" : "teal"; return '<span class="pill ' + cls + '">' + escapeHtml(v) + '</span>'; }
    function syncProjectOptions(){ [document.getElementById("projectId"), document.getElementById("deviceProjectId")].forEach((select) => { const current = select.value || defaultProjectId; select.innerHTML = state.projects.map((project) => '<option value="' + escapeHtml(project.id) + '">' + escapeHtml(project.code + " - " + project.name) + '</option>').join(""); if (current) select.value = current; }); updatePreview(); }
    function updatePreview(){ const project = state.projects.find((entry) => entry.id === document.getElementById("projectId").value); document.getElementById("previewCampaign").textContent = document.getElementById("campaignName").value || "Voucher campaign"; document.getElementById("previewCode").textContent = document.getElementById("codeMode").value === "AUTO" ? "AUTO-GENERATED" : (document.getElementById("codeName").value || "MANUAL-CODE"); document.getElementById("previewProject").textContent = project ? project.code : "-"; document.getElementById("previewBenefit").textContent = document.getElementById("benefitType").value; document.getElementById("previewUses").textContent = document.getElementById("maxUses").value || "1"; document.getElementById("previewUntil").textContent = document.getElementById("validUntil").value || "No expiry"; }
    function renderRecords(){ const head = document.getElementById("recordsHead"); const body = document.getElementById("recordsBody"); if (state.tab === "vouchers") { head.innerHTML = "<tr><th>Code</th><th>Project</th><th>Benefit</th><th>Usage</th><th>Status</th><th>Expires</th><th>Campaign</th></tr>"; body.innerHTML = state.vouchers.length ? state.vouchers.map((row) => "<tr><td><code>" + escapeHtml(row.code_masked) + "</code></td><td>" + escapeHtml(row.project_code || row.project_id) + "</td><td>" + escapeHtml(row.benefit_type) + "</td><td>" + row.used_count + " / " + row.max_uses + "</td><td>" + statusPill(row.status) + "</td><td>" + formatDate(row.valid_until) + "</td><td>" + escapeHtml(row.campaign_name || "-") + "</td></tr>").join("") : '<tr><td colspan="7" class="empty">No vouchers yet.</td></tr>'; } else { head.innerHTML = "<tr><th>ID</th><th>Code</th><th>Job</th><th>Status</th><th>Gross</th><th>Discount</th><th>Net</th><th>Reserved</th></tr>"; body.innerHTML = state.redemptions.length ? state.redemptions.map((row) => "<tr><td class='mono'>" + row.id + "</td><td><code>" + escapeHtml(row.code_masked) + "</code></td><td><code>" + escapeHtml(row.job_id) + "</code></td><td>" + statusPill(row.status) + "</td><td>" + moneyMinor(row.gross_amount_minor, row.currency_code) + "</td><td>" + moneyMinor(row.discount_amount_minor, row.currency_code) + "</td><td>" + moneyMinor(row.net_amount_minor, row.currency_code) + "</td><td>" + formatDate(row.reserved_at) + "</td></tr>").join("") : '<tr><td colspan="8" class="empty">No redemptions yet.</td></tr>'; } }
    async function loadData(){ setStatus("formStatus","Loading...",""); try { const projects = await api("/api/admin/v1/projects"); state.projects = projects.projects || []; syncProjectOptions(); const projectId = document.getElementById("projectId").value; const qs = projectId ? "?project_id=" + encodeURIComponent(projectId) : ""; const vouchers = await api("/api/admin/v1/vouchers" + qs); const redemptions = await api("/api/admin/v1/redemptions" + qs); state.vouchers = vouchers.vouchers || []; state.redemptions = redemptions.redemptions || []; renderRecords(); setStatus("formStatus","Loaded.","ok"); } catch (error) { setStatus("formStatus", error.message, "error"); } }
    function payloadFromForm(form){ const data = Object.fromEntries(new FormData(form).entries()); data.quantity = Number(data.quantity || 1); data.max_uses_per_code = Number(data.max_uses_per_code || 1); data.benefit_value_minor = Number(data.benefit_value_minor || 0); data.benefit_percent = Number(data.benefit_percent || 0); if (data.valid_until) data.valid_until = new Date(data.valid_until).toISOString(); else delete data.valid_until; if (data.code_mode === "AUTO") delete data.code_name; return data; }
    document.getElementById("saveToken").addEventListener("click", () => { localStorage.setItem("photoBoothAdminToken", tokenInput.value.trim()); loadData(); });
    document.getElementById("voucherForm").addEventListener("input", updatePreview);
    document.getElementById("projectId").addEventListener("change", () => { updatePreview(); loadData(); });
    document.getElementById("refreshData").addEventListener("click", loadData);
    document.getElementById("voucherForm").addEventListener("submit", async (event) => { event.preventDefault(); try { setStatus("formStatus","Generating voucher...",""); const data = await api("/api/admin/v1/vouchers/generate", { method:"POST", body:JSON.stringify(payloadFromForm(event.currentTarget)) }); const codes = data.vouchers || []; document.getElementById("generatedCode").innerHTML = codes.length ? codes.map((row) => '<div><code>' + escapeHtml(row.code) + '</code> <span class="pill green">' + escapeHtml(row.benefit_type) + '</span></div>').join("") : "No codes returned."; setStatus("formStatus","Generated " + codes.length + " voucher code(s).","ok"); await loadData(); } catch (error) { setStatus("formStatus", error.message, "error"); } });
    document.getElementById("deviceForm").addEventListener("submit", async (event) => { event.preventDefault(); const data = Object.fromEntries(new FormData(event.currentTarget).entries()); try { setStatus("deviceStatus","Binding device...",""); await api("/api/admin/v1/projects/" + encodeURIComponent(data.project_id) + "/devices", { method:"POST", body:JSON.stringify({ device_id:data.device_id }) }); setStatus("deviceStatus","Device bound to project.","ok"); } catch (error) { setStatus("deviceStatus", error.message, "error"); } });
    document.getElementById("tabVouchers").addEventListener("click", () => { state.tab = "vouchers"; document.getElementById("tabVouchers").classList.add("active"); document.getElementById("tabRedemptions").classList.remove("active"); renderRecords(); });
    document.getElementById("tabRedemptions").addEventListener("click", () => { state.tab = "redemptions"; document.getElementById("tabRedemptions").classList.add("active"); document.getElementById("tabVouchers").classList.remove("active"); renderRecords(); });
    loadData();
  </script>
</body>
</html>`;
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
  const sortedFrames = sortMotionFrameAssets(motionFrames);

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

function sortMotionFrameAssets(motionFrames) {
  return [...motionFrames].sort((left, right) => {
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

function renderLegacyDownloadPage({ job, composed, thumbnail, liveImage, motionVideo, rawCaptures, motionFrames }) {
  const jobId = job.job_id;
  const safeJobId = escapeHtml(jobId);
  const displayTitle = escapeHtml(formatSessionTitle(job));
  const takenAt = escapeHtml(formatDisplayDate(job.session_started_at_utc || job.created_at));
  const imageUrl = assetUrl(composed);
  const thumbnailUrl = assetUrl(thumbnail);
  const liveImageUrl = assetUrl(liveImage);
  const imageDownloadUrl = `/d/${encodeURIComponent(jobId)}/image-download`;
  const liveviewVideoDownloadUrl = `/d/${encodeURIComponent(jobId)}/liveview-download.mp4`;
  const framedCountdownVideoDownloadUrl = `/d/${encodeURIComponent(jobId)}/countdown-download.mp4`;
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
  const photoMarkup = framedTemplateMarkup || (imageUrl
    ? `<img class="media" src="${imageUrl}" alt="Framed picture" loading="eager">`
    : "");
  const countdownClipMarkup = motionFrames.length > 0
    ? renderFramedCountdownClip(countdownSlotFrameUrls)
    : motionVideoUrl
      ? `<video class="media" controls autoplay playsinline loop muted poster="${thumbnailUrl}"><source src="${motionVideoUrl}" type="video/mp4"></video>`
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
    .media-empty { display: grid; place-items: center; min-height: 220px; color: #080808; text-transform: uppercase; font-size: 22px; text-align: center; }
    .framed-video { height: auto; object-fit: contain; }
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
      ${photoMarkup || `<p>Photo is still processing.</p>`}
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
</body>
</html>`;
}

function renderWorldTourDownloadPage({ job, composed, thumbnail, liveImage, motionVideo, rawCaptures, motionFrames }) {
  const prefix = "world-tour";
  const jobId = job.job_id;
  const safeJobId = escapeHtml(jobId);
  const displayTitle = "MRKREME World Tour Session";
  const takenAt = escapeHtml(formatDisplayDate(job.session_started_at_utc || job.created_at));
  const reference = escapeHtml(shortReference(jobId));
  const imageUrl = assetUrl(composed);
  const thumbnailUrl = assetUrl(thumbnail);
  const heroPreviewUrl = imageUrl || thumbnailUrl;
  const liveImageUrl = assetUrl(liveImage);
  const labelTemplateId = resolveLabelTemplateId(job.image_preview_id || job.theme_id);
  const passengerName = normalizePassengerName(job.passenger_name);
  const passengerNameVersion = passengerNameCacheKey(passengerName);
  const labelTemplateUrl = `/assets/label/frame-${labelTemplateId}.png`;
  const imageDownloadUrl = `/${prefix}/${encodeURIComponent(jobId)}/image-download`;
  const framedCountdownVideoDownloadUrl = `/${prefix}/${encodeURIComponent(jobId)}/countdown-download.mp4`;
  const motionVideoUrl = motionVideo ? `/${prefix}/${encodeURIComponent(jobId)}/clip.mp4?v=frame-${labelTemplateId}-name-${passengerNameVersion}-v3` : "";
  const motionVideoDownloadUrl = motionVideo ? `/${prefix}/${encodeURIComponent(jobId)}/clip-download.mp4?v=frame-${labelTemplateId}-name-${passengerNameVersion}-v3` : "";
  const rawCaptureUrls = rawCaptures.map(assetUrl).filter(Boolean);
  const hasCountdownPreview = motionFrames.length > 0;
  const framedCountdownVideoUrl = hasCountdownPreview ? `/${prefix}/${encodeURIComponent(jobId)}/framed-countdown.mp4?v=frame-${labelTemplateId}-name-${passengerNameVersion}-v3` : "";
  const rawMotionVideoUrl = assetUrl(motionVideo);
  const passengerNameMarkup = passengerName
    ? `<div class="label-passenger-name">${escapeHtml(passengerName)}</div>`
    : "";
  const photoUrl = rawCaptureUrls[0] || heroPreviewUrl || liveImageUrl;
  const photoMarkup = photoUrl
    ? `<img class="label-photo" src="${photoUrl}" alt="Captured photo" loading="eager">`
    : `<div class="label-photo label-photo-empty">Processing</div>`;
  const videoMarkup = framedCountdownVideoUrl
    ? `<video class="media framed-video" controls autoplay playsinline loop muted poster="${photoUrl || thumbnailUrl}"><source src="${framedCountdownVideoUrl}" type="video/mp4"></video>`
    : (rawMotionVideoUrl || motionVideoUrl)
      ? `<div class="label-stage label-stage-${labelTemplateId} video-label-stage">
          <img class="label-template" src="${labelTemplateUrl}" alt="Furryways frame ${labelTemplateId}">
          <video class="label-photo label-video" controls autoplay playsinline loop muted poster="${photoUrl || thumbnailUrl}"><source src="${rawMotionVideoUrl || motionVideoUrl}" type="video/mp4"></video>
          ${passengerNameMarkup}
        </div>`
      : "";
  const videoDownloadUrl = hasCountdownPreview
    ? framedCountdownVideoDownloadUrl
    : motionVideoDownloadUrl;
  const videoDownloadMarkup = videoDownloadUrl
    ? `<a class="download-image-button" href="${videoDownloadUrl}" download>Download Video</a>`
    : `<div class="download-placeholder">Video is processing</div>`;

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
      --cream: #f2ead3;
      --yellow: #ffd713;
      font-family: Impact, Haettenschweiler, "Arial Black", ui-sans-serif, system-ui, sans-serif;
    }
    @font-face {
      font-family: "Battery Park";
      src: url("/assets/label/BatteryPark.ttf") format("truetype");
      font-display: swap;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      min-height: 100vh;
      color: var(--ink);
      background: #b98245 url("/assets/label/background.png") center top / cover repeat-y;
    }
    .page {
      position: relative;
      width: min(100%, 554px);
      min-height: 100vh;
      margin: 0 auto;
      overflow: hidden;
      background: url("/assets/label/background.png") center top / cover repeat-y;
      box-shadow: 0 0 0 8px #050505;
      padding: 0 26px 44px;
    }
    .page::after {
      content: "";
      position: absolute;
      left: -24px;
      right: -24px;
      bottom: -26px;
      height: 156px;
      background:
        radial-gradient(ellipse at 6% 0, transparent 0 34px, rgba(0,0,0,.16) 35px 39px, transparent 40px),
        radial-gradient(ellipse at 94% 0, transparent 0 34px, rgba(0,0,0,.16) 35px 39px, transparent 40px),
        linear-gradient(176deg, transparent 0 16%, var(--cream) 16.4%),
        linear-gradient(4deg, transparent 0 12%, #fff5d4 12.4%);
      z-index: 0;
    }
    .brand {
      position: relative;
      z-index: 1;
      display: block;
      width: calc(100% + 52px);
      max-width: none;
      margin: 0 -26px 18px;
      aspect-ratio: 1080 / 375;
      object-fit: cover;
      object-position: center top;
    }
    .ticket {
      position: relative;
      z-index: 1;
      width: min(320px, 84%);
      margin: 8px auto 18px;
      padding: 0;
      background: transparent;
      box-shadow: 0 16px 24px rgba(0,0,0,.18);
      overflow: hidden;
    }
    .label-stage {
      position: relative;
      width: 100%;
      aspect-ratio: 2136 / 3132;
      background: #fff;
      container-type: inline-size;
    }
    .label-stage-2 { aspect-ratio: 2138 / 3134; }
    .label-template {
      position: relative;
      z-index: 1;
      display: block;
      width: 100%;
      height: 100%;
      object-fit: contain;
      pointer-events: none;
    }
    .label-photo {
      position: absolute;
      z-index: 2;
      display: block;
      object-fit: cover;
      background: #ddd;
    }
    .label-photo-empty {
      display: grid;
      place-items: center;
      font-size: 22px;
      text-transform: uppercase;
      color: #222;
    }
    .label-stage-1 .label-photo {
      left: 2.9%;
      top: 56.5%;
      width: 94.3%;
      height: 36%;
    }
    .label-stage-2 .label-photo {
      left: 5.1%;
      top: 20%;
      width: 89.8%;
      height: 34.5%;
    }
    .label-passenger-name {
      position: absolute;
      z-index: 3;
      font-family: "Battery Park", Impact, "Arial Black", sans-serif;
      color: #17477f;
      font-size: 16px;
      font-size: 4.87cqw;
      line-height: 1;
      letter-spacing: 0;
      text-transform: uppercase;
      white-space: nowrap;
      pointer-events: none;
    }
    .label-stage-1 .label-passenger-name {
      left: 7.82%;
      top: 16.9%;
    }
    .label-stage-2 .label-passenger-name {
      left: 10.29%;
      top: 61.3%;
    }
    .ticket-head {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 42%;
      gap: 8px;
      align-items: end;
      border-bottom: 2px solid #222;
      padding-bottom: 5px;
    }
    .ticket-logo {
      font-size: 20px;
      line-height: .76;
      text-transform: uppercase;
    }
    .ticket-logo span { display: block; }
    .barcode {
      height: 38px;
      background: repeating-linear-gradient(90deg, #111 0 2px, transparent 2px 4px, #111 4px 5px, transparent 5px 8px, #111 8px 11px, transparent 11px 14px);
      border-bottom: 1px solid #222;
    }
    .ticket-grid {
      position: relative;
      display: grid;
      grid-template-columns: 1fr 76px;
      border: 2px solid #222;
      border-top: 0;
      font-family: Arial, Helvetica, sans-serif;
      font-weight: 800;
      font-size: 11px;
    }
    .ticket-cell { min-height: 37px; padding: 5px 7px; border-bottom: 1px solid #222; }
    .ticket-cell b { display: block; font-size: 12px; }
    .ticket-side { grid-row: span 2; border-left: 2px solid #222; }
    .keep-cool { display: grid; place-items: center; min-height: 42px; border-bottom: 1px solid #222; font-family: Impact, "Arial Black", sans-serif; font-size: 19px; line-height: .82; text-align: center; }
    .stamp {
      position: absolute;
      left: 42%;
      top: 8px;
      width: 92px;
      height: 92px;
      display: grid;
      place-items: center;
      border: 3px solid var(--red);
      border-radius: 50%;
      color: var(--red);
      font-family: Impact, "Arial Black", sans-serif;
      font-size: 16px;
      line-height: .86;
      text-align: center;
      text-transform: uppercase;
      transform: rotate(-18deg);
      opacity: .86;
    }
    .ticket-route {
      display: grid;
      grid-template-columns: 1fr 74px;
      gap: 8px;
      padding: 8px 0;
      font-family: Arial, Helvetica, sans-serif;
      font-size: 11px;
      font-weight: 900;
      line-height: 1.18;
      text-transform: uppercase;
    }
    .ticket-route img { width: 74px; aspect-ratio: 1; }
    .ticket-photo {
      display: block;
      width: 100%;
      aspect-ratio: 1.26 / 1;
      object-fit: cover;
      border: 3px solid #222;
      background: #ddd;
    }
    .ticket-photo-empty { display: grid; place-items: center; font-size: 22px; text-transform: uppercase; }
    .ticket-foot {
      display: flex;
      justify-content: space-between;
      gap: 10px;
      margin-top: 5px;
      font-family: Arial, Helvetica, sans-serif;
      font-size: 7px;
      font-weight: 900;
      text-transform: uppercase;
    }
    .download-image-button {
      position: relative;
      z-index: 2;
      display: grid;
      place-items: center;
      width: min(350px, 76vw);
      min-height: 56px;
      margin: 0 auto;
      border-radius: 14px;
      background: var(--yellow);
      color: #080808;
      box-shadow: 0 6px 0 #d2aa00, 0 12px 24px rgba(0,0,0,.24);
      text-decoration: none;
      text-transform: uppercase;
      font-size: clamp(22px, 5.8vw, 28px);
      line-height: 1;
    }
    .section-heading {
      position: relative;
      z-index: 2;
      width: min(350px, 76vw);
      margin: 24px auto 10px;
      font-size: 32px;
      line-height: 1;
      text-transform: uppercase;
      color: #080808;
    }
    .video-panel {
      position: relative;
      z-index: 2;
      width: min(350px, 76vw);
      margin: 0 auto 14px;
      background: #fff5d4;
      border: 3px solid #080808;
      box-shadow: 0 10px 18px rgba(0,0,0,.18);
    }
    .download-placeholder {
      position: relative;
      z-index: 2;
      display: grid;
      place-items: center;
      width: min(350px, 76vw);
      min-height: 56px;
      margin: 0 auto;
      border: 3px solid #080808;
      color: #080808;
      background: rgba(255,255,255,.55);
      text-transform: uppercase;
      font-size: 22px;
      line-height: 1;
      text-align: center;
    }
    .back-home { margin-top: 10px; }
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
    .media-empty { display: grid; place-items: center; min-height: 220px; color: #080808; text-transform: uppercase; font-size: 22px; text-align: center; }
    img.media { height: auto; }
    .frame-fallback { display: none; }
    @media (max-width: 720px) {
      .page { padding: 0 10px 44px; }
      .brand { width: calc(100% + 20px); margin-left: -10px; margin-right: -10px; }
    }
    @media (max-width: 380px) {
      .ticket { width: 86%; }
      .stamp { width: 78px; height: 78px; font-size: 14px; }
    }
  </style>
</head>
<body>
  <main class="page">
    <img class="brand" src="/assets/label/headline.png" alt="The Furryways">
    <h2 class="section-heading">Photo</h2>
    <section class="ticket" aria-label="${displayTitle}">
      <div class="label-stage label-stage-${labelTemplateId}">
        <img class="label-template" src="${labelTemplateUrl}" alt="Furryways frame ${labelTemplateId}">
        ${photoMarkup}
        ${passengerNameMarkup}
      </div>
    </section>
    <a class="download-image-button" id="photo-download-button" href="${imageDownloadUrl}" download>Download Image</a>
    <h2 class="section-heading">Video</h2>
    <section class="video-panel" aria-label="Video">
      ${videoMarkup || `<div class="media media-empty">Video is processing</div>`}
    </section>
    ${videoDownloadMarkup}
  </main>
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

function resolveLabelTemplateId(value) {
  return normalizeImagePreviewId(value) === "image_preview_2" ? "2" : "1";
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
