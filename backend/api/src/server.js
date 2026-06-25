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
  publicBaseUrlConfigured: Boolean((process.env.PUBLIC_BASE_URL || "").trim()),
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
  defaultDownloadRoutePrefix: normalizeDownloadRoutePrefix(process.env.DEFAULT_DOWNLOAD_ROUTE_PREFIX || "world-tour"),
  voucherLinkBaseUrl: (process.env.VOUCHER_LINK_BASE_URL || process.env.PUBLIC_BASE_URL || "http://localhost:8080").replace(/\/$/, ""),
  voucherLinkPath: normalizeRoutePath(process.env.VOUCHER_LINK_PATH || "/voucher-link"),
  voucherScanBaseUrl: (process.env.VOUCHER_SCAN_BASE_URL || process.env.PUBLIC_BASE_URL || "http://localhost:8080").replace(/\/$/, ""),
  voucherScanPath: normalizeRoutePath(process.env.VOUCHER_SCAN_PATH || "/voucher-scan"),
  kioskSessionTtlSeconds: Math.max(60, Number.parseInt(process.env.KIOSK_SESSION_TTL_SECONDS || "900", 10)),
  voucherScanSessionTtlSeconds: Math.max(60, Number.parseInt(process.env.VOUCHER_SCAN_SESSION_TTL_SECONDS || "300", 10)),
  featuredComposedRecencySeconds: Math.max(1, Number.parseInt(process.env.FEATURED_COMPOSED_RECENCY_SECONDS || "600", 10))
};

const pool = new Pool({
  host: process.env.POSTGRES_HOST || "db",
  port: Number.parseInt(process.env.POSTGRES_PORT || "5432", 10),
  database: process.env.POSTGRES_DB || "photo_booth",
  user: process.env.POSTGRES_USER || "photo_booth",
  password: process.env.POSTGRES_PASSWORD || "photo_booth"
});

const generatedMediaPromises = new Map();

fs.mkdirSync(config.uploadsRoot, { recursive: true });

app.disable("x-powered-by");
app.set("trust proxy", true);
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

app.get("/api/docs/openapi.json", (req, res) => {
  return res.json(buildOpenApiSpec(req));
});

app.get("/api/docs", (req, res) => {
  res.setHeader("Content-Security-Policy", "default-src 'self' https://cdn.jsdelivr.net; img-src 'self' data: https://cdn.jsdelivr.net; style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; connect-src 'self'");
  return res.send(renderSwaggerDocsPage());
});

app.get("/tools/voucher-test", (req, res) => {
  res.setHeader("Content-Security-Policy", "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'");
  return res.send(renderVoucherTestPage());
});

app.get(config.voucherScanPath, (req, res) => {
  res.setHeader("Content-Security-Policy", "default-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'");
  return res.send(renderVoucherScanPage());
});

if (config.voucherScanPath !== "/voucher-scan") {
  app.get("/voucher-scan", (req, res) => {
    res.setHeader("Content-Security-Policy", "default-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'");
    return res.send(renderVoucherScanPage());
  });
}

app.get(config.voucherLinkPath, (req, res) => {
  res.setHeader("Content-Security-Policy", "default-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'");
  return res.send(renderVoucherLinkPage());
});

if (config.voucherLinkPath !== "/voucher-link") {
  app.get("/voucher-link", (req, res) => {
    res.setHeader("Content-Security-Policy", "default-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'");
    return res.send(renderVoucherLinkPage());
  });
}

app.post("/api/kiosk/v1/kiosk-sessions", requireDeviceAuth, async (req, res) => {
  const projectId = normalizeProjectId(req.body?.project_id) || await resolveProjectIdForDevice(pool, normalizeOptional(req.body?.device_id) || normalizeOptional(req.get("X-Device-Id")));
  const deviceId = normalizeOptional(req.body?.device_id) || normalizeOptional(req.get("X-Device-Id")) || "booth-local";
  const jobId = normalizeOptional(req.body?.job_id);
  if (!jobId) {
    return res.status(400).json(errorEnvelope("JOB_ID_REQUIRED", "job_id is required."));
  }

  const sessionToken = createKioskSessionToken();
  const scanUrl = buildKioskSessionUrl(req, sessionToken);

  try {
    const client = await pool.connect();
    try {
      await ensureProject(client, projectId);
      const result = await client.query(
        `INSERT INTO kiosk_sessions (
            session_token,
            project_id,
            device_id,
            job_id,
            status,
            qr_payload,
            expires_at
          )
          VALUES ($1, $2, $3, $4, 'PENDING', $5, NOW() + ($6 * INTERVAL '1 second'))
          RETURNING id, session_token, project_id, device_id, job_id, status, mobile_device_id, voucher_link_id, voucher_code, voucher_status_json, qr_payload, expires_at, attached_at, redeemed_at, cancelled_at, created_at, updated_at`,
        [sessionToken, projectId, deviceId, jobId, scanUrl, config.kioskSessionTtlSeconds]
      );
      await recordKioskSessionEvent(client, result.rows[0].id, "KIOSK_SESSION_CREATED", "PENDING", { scan_url: scanUrl });
      return res.status(201).json({
        success: true,
        data: toKioskSessionResponse(result.rows[0], req),
        error: null
      });
    } finally {
      client.release();
    }
  } catch (error) {
    console.error("kiosk_session_create_failed", { projectId, deviceId, jobId, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "KIOSK_SESSION_CREATE_FAILED", error.message));
  }
});

app.get("/api/kiosk/v1/kiosk-sessions/:sessionToken", requireDeviceAuth, async (req, res) => {
  const sessionToken = normalizeVoucherScanSessionToken(req.params.sessionToken);
  const deviceId = normalizeOptional(req.get("X-Device-Id"));
  if (!sessionToken) {
    return res.status(400).json(errorEnvelope("INVALID_SESSION_TOKEN", "sessionToken is invalid."));
  }

  try {
    const projectId = await resolveProjectIdForDevice(pool, deviceId);
    const session = await loadKioskSessionForKiosk(pool, projectId, deviceId || "booth-local", sessionToken);
    if (!session) {
      return res.status(404).json(errorEnvelope("KIOSK_SESSION_NOT_FOUND", "Kiosk session was not found."));
    }

    return res.json({ success: true, data: toKioskSessionResponse(session, req), error: null });
  } catch (error) {
    console.error("kiosk_session_get_failed", { sessionToken, deviceId, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "KIOSK_SESSION_GET_FAILED", error.message));
  }
});

app.post("/api/kiosk/v1/kiosk-sessions/:sessionToken/ack", requireDeviceAuth, async (req, res) => {
  const sessionToken = normalizeVoucherScanSessionToken(req.params.sessionToken);
  const ackStatus = normalizeKioskSessionStatus(req.body?.status);
  const deviceId = normalizeOptional(req.body?.device_id) || normalizeOptional(req.get("X-Device-Id"));
  if (!sessionToken) {
    return res.status(400).json(errorEnvelope("INVALID_SESSION_TOKEN", "sessionToken is invalid."));
  }
  if (!ackStatus || ackStatus === "PENDING") {
    return res.status(400).json(errorEnvelope("INVALID_SESSION_ACK_STATUS", "status must be CONSUMED or CANCELLED."));
  }

  try {
    const projectId = await resolveProjectIdForDevice(pool, deviceId);
    const client = await pool.connect();
    try {
      const session = await loadKioskSessionForKiosk(client, projectId, deviceId || "booth-local", sessionToken);
      if (!session) {
        return res.status(404).json(errorEnvelope("KIOSK_SESSION_NOT_FOUND", "Kiosk session was not found."));
      }

      const nowColumn = ackStatus === "CONSUMED" ? "redeemed_at" : "cancelled_at";
      const result = await client.query(
        `UPDATE kiosk_sessions
         SET status = $2,
             ${nowColumn} = NOW(),
             updated_at = NOW()
         WHERE id = $1
         RETURNING id, session_token, project_id, device_id, job_id, status, mobile_device_id, voucher_link_id, voucher_code, voucher_status_json, qr_payload, expires_at, attached_at, redeemed_at, cancelled_at, created_at, updated_at`,
        [session.id, ackStatus]
      );
      await recordKioskSessionEvent(client, session.id, "KIOSK_SESSION_ACK", ackStatus, { device_id: deviceId || null });
      return res.json({ success: true, data: toKioskSessionResponse(result.rows[0], req), error: null });
    } finally {
      client.release();
    }
  } catch (error) {
    console.error("kiosk_session_ack_failed", { sessionToken, deviceId, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "KIOSK_SESSION_ACK_FAILED", error.message));
  }
});

app.get("/api/web/v1/kiosk-sessions/:sessionToken", async (req, res) => {
  const sessionToken = normalizeVoucherScanSessionToken(req.params.sessionToken);
  if (!sessionToken) {
    return res.status(400).json(errorEnvelope("INVALID_SESSION_TOKEN", "sessionToken is invalid."));
  }

  try {
    const session = await loadKioskSessionForWeb(pool, sessionToken);
    if (!session) {
      return res.status(404).json(errorEnvelope("KIOSK_SESSION_NOT_FOUND", "Kiosk session was not found."));
    }

    return res.json({ success: true, data: toKioskSessionResponse(session, req), error: null });
  } catch (error) {
    console.error("kiosk_session_web_get_failed", { sessionToken, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "KIOSK_SESSION_GET_FAILED", error.message));
  }
});

app.get("/api/web/v1/kiosk-sessions/:sessionToken/qr.png", async (req, res) => {
  const sessionToken = normalizeVoucherScanSessionToken(req.params.sessionToken);
  if (!sessionToken) {
    return res.status(400).json(errorEnvelope("INVALID_SESSION_TOKEN", "sessionToken is invalid."));
  }

  try {
    const session = await loadKioskSessionForWeb(pool, sessionToken);
    if (!session) {
      return res.status(404).json(errorEnvelope("KIOSK_SESSION_NOT_FOUND", "Kiosk session was not found."));
    }

    const png = await QRCode.toBuffer(session.qr_payload || buildKioskSessionUrl(req, sessionToken), {
      type: "png",
      errorCorrectionLevel: "M",
      margin: 1,
      scale: 10,
      color: {
        dark: "#000000",
        light: "#ffffff"
      }
    });
    res.setHeader("Content-Type", "image/png");
    res.setHeader("Cache-Control", "no-store");
    return res.send(png);
  } catch (error) {
    console.error("kiosk_session_qr_failed", { sessionToken, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "KIOSK_SESSION_QR_FAILED", error.message));
  }
});

app.get("/api/web/v1/device/me", async (req, res) => {
  const deviceToken = getDeviceTokenFromRequest(req);
  if (!deviceToken) {
    return res.status(401).json(errorEnvelope("DEVICE_TOKEN_REQUIRED", "device_token is required."));
  }

  try {
    const device = await loadUserDeviceByToken(pool, deviceToken);
    if (!device || device.status !== "ACTIVE") {
      return res.status(404).json(errorEnvelope("DEVICE_NOT_REGISTERED", "Device is not registered."));
    }

    await touchUserDevice(pool, device.id);
    return res.json({ success: true, data: { device_token: deviceToken, device: toUserDeviceResponse(device) }, error: null });
  } catch (error) {
    console.error("device_me_failed", { error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "DEVICE_ME_FAILED", error.message));
  }
});

app.post("/api/web/v1/device/claim-voucher", async (req, res) => {
  const incomingToken = getDeviceTokenFromRequest(req);
  const deviceName = normalizeDeviceName(req.body?.device_name);
  const projectId = normalizeProjectId(req.body?.project_id) || config.defaultProjectId;
  const voucherCodes = Array.isArray(req.body?.voucher_codes)
    ? req.body.voucher_codes
    : normalizeOptional(req.body?.voucher_code)
      ? [req.body.voucher_code]
      : [];

  try {
    const client = await pool.connect();
    try {
      await client.query("BEGIN");
      await ensureProject(client, projectId);
      let deviceToken = incomingToken || createDeviceToken();
      let device = await saveUserDevice(client, { deviceToken, projectId, deviceName, status: "ACTIVE" });

      const claimedVouchers = [];
      for (const voucherCodeInput of voucherCodes) {
        const voucherCode = normalizeAutoVoucherCode(voucherCodeInput) || normalizeVoucherCode(voucherCodeInput);
        if (!voucherCode) {
          throw httpError(400, "INVALID_VOUCHER_CODE", "voucher_code is invalid.");
        }

        const voucher = await loadVoucherStatusByCode(client, projectId, voucherCode);
        const voucherStatus = toVoucherStatusResponse(voucher, projectId);
        if (!voucherStatus.exists) {
          throw httpError(404, "VOUCHER_NOT_FOUND", "Voucher was not found.");
        }
        if (!voucherStatus.usable_now) {
          throw httpError(409, voucherStatus.status === "USED" ? "VOUCHER_USED" : "VOUCHER_UNAVAILABLE", `Voucher is not usable: ${voucherStatus.status}.`);
        }

        const linkResult = await client.query(
          `INSERT INTO voucher_device_links (
              project_id,
              device_id,
              voucher_id,
              voucher_code,
              status,
              claim_source,
              claimed_at,
              last_seen_at
            )
            VALUES ($1, $2, $3, $4, 'ACTIVE', $5, NOW(), NOW())
            ON CONFLICT (project_id, voucher_id)
            DO UPDATE SET
              device_id = EXCLUDED.device_id,
              voucher_code = EXCLUDED.voucher_code,
              status = 'ACTIVE',
              claim_source = COALESCE(EXCLUDED.claim_source, voucher_device_links.claim_source),
              last_seen_at = NOW(),
              updated_at = NOW()
            RETURNING id, project_id, device_id, voucher_id, voucher_code, status, claim_source, claimed_at, last_seen_at, metadata, created_at, updated_at`,
          [projectId, device.id, voucher.voucher_id || voucher.id, voucherCode, normalizeOptional(req.body?.claim_source) || "device_claim"]
        );
        claimedVouchers.push({
          link_id: linkResult.rows[0].id,
          voucher_code: voucherCode,
          voucher_status: voucherStatus
        });
      }

      device = await touchUserDevice(client, device.id) || device;
      await client.query("COMMIT");
      return res.status(incomingToken ? 200 : 201).json({
        success: true,
        data: {
          device_token: deviceToken,
          device: toUserDeviceResponse(device),
          claimed_vouchers: claimedVouchers
        },
        error: null
      });
    } catch (error) {
      try {
        await client.query("ROLLBACK");
      } catch {
      }
      throw error;
    } finally {
      client.release();
    }
  } catch (error) {
    console.error("device_claim_failed", { error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "DEVICE_CLAIM_FAILED", error.message));
  }
});

app.get("/api/web/v1/me/vouchers", async (req, res) => {
  const deviceToken = getDeviceTokenFromRequest(req);
  const projectId = normalizeProjectId(req.query?.project_id) || config.defaultProjectId;
  if (!deviceToken) {
    return res.status(401).json(errorEnvelope("DEVICE_TOKEN_REQUIRED", "device_token is required."));
  }

  try {
    const device = await loadUserDeviceByToken(pool, deviceToken);
    if (!device || device.status !== "ACTIVE") {
      return res.status(404).json(errorEnvelope("DEVICE_NOT_REGISTERED", "Device is not registered."));
    }

    const links = await loadVoucherLinksForDevice(pool, projectId, device.id);
    const vouchers = [];
    for (const link of links) {
      const voucher = await loadVoucherStatusByCode(pool, projectId, link.voucher_code);
      vouchers.push({
        link_id: link.id,
        voucher_code: link.voucher_code,
        voucher_status: toVoucherStatusResponse(voucher, projectId),
        claimed_at: link.claimed_at,
        last_seen_at: link.last_seen_at,
        claim_source: link.claim_source,
        status: link.status
      });
    }

    await touchUserDevice(pool, device.id);
    return res.json({
      success: true,
      data: {
        device_token: deviceToken,
        device: toUserDeviceResponse(device),
        vouchers
      },
      error: null
    });
  } catch (error) {
    console.error("me_vouchers_failed", { projectId, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "ME_VOUCHERS_FAILED", error.message));
  }
});

app.post("/api/web/v1/kiosk-sessions/:sessionToken/attach-voucher", async (req, res) => {
  const sessionToken = normalizeVoucherScanSessionToken(req.params.sessionToken);
  const voucherCode = normalizeAutoVoucherCode(req.body?.voucher_code) || normalizeVoucherCode(req.body?.voucher_code);
  const deviceToken = getDeviceTokenFromRequest(req);
  if (!sessionToken) {
    return res.status(400).json(errorEnvelope("INVALID_SESSION_TOKEN", "sessionToken is invalid."));
  }
  if (!voucherCode) {
    return res.status(400).json(errorEnvelope("INVALID_VOUCHER_CODE", "voucher_code is required."));
  }
  if (!deviceToken) {
    return res.status(401).json(errorEnvelope("DEVICE_TOKEN_REQUIRED", "device_token is required."));
  }

  try {
    const client = await pool.connect();
    try {
      await client.query("BEGIN");
      const device = await loadUserDeviceByToken(client, deviceToken);
      if (!device || device.status !== "ACTIVE") {
        await client.query("ROLLBACK");
        return res.status(404).json(errorEnvelope("DEVICE_NOT_REGISTERED", "Device is not registered."));
      }

      const session = await loadKioskSessionForWeb(client, sessionToken);
      if (!session) {
        await client.query("ROLLBACK");
        return res.status(404).json(errorEnvelope("KIOSK_SESSION_NOT_FOUND", "Kiosk session was not found."));
      }

      if (session.project_id !== device.project_id) {
        await client.query("ROLLBACK");
        return res.status(409).json(errorEnvelope("KIOSK_SESSION_PROJECT_MISMATCH", "Kiosk session project does not match this device."));
      }

      if (session.status === "CONSUMED" || session.status === "REDEEMED") {
        await client.query("ROLLBACK");
        return res.status(409).json(errorEnvelope("KIOSK_SESSION_ALREADY_CLOSED", "Kiosk session has already been closed."));
      }

      const link = await loadVoucherDeviceLinkByVoucherCode(client, session.project_id, voucherCode);
      if (!link || link.device_id !== device.id) {
        await client.query("ROLLBACK");
        return res.status(404).json(errorEnvelope("VOUCHER_NOT_LINKED", "Voucher is not linked to this device."));
      }

      const voucher = await loadVoucherStatusByCode(client, session.project_id, voucherCode);
      const voucherStatus = toVoucherStatusResponse(voucher, session.project_id);
      if (!voucherStatus.exists) {
        await client.query("ROLLBACK");
        return res.status(404).json(errorEnvelope("VOUCHER_NOT_FOUND", "Voucher was not found."));
      }
      if (!voucherStatus.usable_now) {
        await client.query("ROLLBACK");
        return res.status(409).json(errorEnvelope(voucherStatus.status === "USED" ? "VOUCHER_USED" : "VOUCHER_UNAVAILABLE", `Voucher is not usable: ${voucherStatus.status}.`));
      }

      const result = await client.query(
        `UPDATE kiosk_sessions
         SET status = 'ATTACHED',
             mobile_device_id = $2,
             voucher_link_id = $3,
             voucher_code = $4,
             voucher_status_json = $5,
             attached_at = NOW(),
             updated_at = NOW()
         WHERE id = $1
         RETURNING id, session_token, project_id, device_id, job_id, status, mobile_device_id, voucher_link_id, voucher_code, voucher_status_json, qr_payload, expires_at, attached_at, redeemed_at, cancelled_at, created_at, updated_at`,
        [session.id, device.id, link.id, voucherCode, voucherStatus]
      );
      await client.query(
        `UPDATE voucher_device_links
         SET last_seen_at = NOW(),
             updated_at = NOW()
         WHERE id = $1`,
        [link.id]
      );
      await recordKioskSessionEvent(client, session.id, "KIOSK_SESSION_VOUCHER_ATTACHED", "ATTACHED", { device_id: device.id, voucher_code: voucherCode });
      await client.query("COMMIT");
      return res.json({ success: true, data: toKioskSessionResponse(result.rows[0], req), error: null });
    } catch (error) {
      try {
        await client.query("ROLLBACK");
      } catch {
      }
      throw error;
    } finally {
      client.release();
    }
  } catch (error) {
    console.error("kiosk_attach_voucher_failed", { sessionToken, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "KIOSK_ATTACH_FAILED", error.message));
  }
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
        qr_png_url: codeMode === "AUTO" ? buildVoucherQrPngUrl(req, voucherCode) : null,
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

app.post("/api/web/v1/vouchers/status", async (req, res) => {
  const voucherCode = normalizeVoucherCode(req.body?.voucher_code);
  const projectId = normalizeProjectId(req.body?.project_id) || config.defaultProjectId;
  if (!voucherCode) {
    return res.status(400).json(errorEnvelope("INVALID_VOUCHER_STATUS_REQUEST", "voucher_code is required."));
  }

  try {
    const voucher = await loadVoucherStatusByCode(pool, projectId, voucherCode);
    return res.json({
      success: true,
      data: toVoucherStatusResponse(voucher, projectId),
      error: null
    });
  } catch (error) {
    console.error("voucher_status_failed", { projectId, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "VOUCHER_STATUS_FAILED", error.message));
  }
});

app.get("/api/web/v1/vouchers/:voucherCode/qr.png", async (req, res) => {
  const voucherCode = normalizeAutoVoucherCode(req.params.voucherCode);
  if (!voucherCode) {
    return res.status(400).json(errorEnvelope("INVALID_VOUCHER_QR_CODE", "voucherCode must match PB-XXXXXXXX."));
  }

  try {
    const png = await QRCode.toBuffer(voucherCode, {
      type: "png",
      errorCorrectionLevel: "M",
      margin: 1,
      width: 320
    });

    res.setHeader("Content-Type", "image/png");
    res.setHeader("Cache-Control", "public, max-age=300");
    return res.send(png);
  } catch (error) {
    console.error("voucher_qr_generation_failed", { voucherCode, error });
    return res.status(500).json(errorEnvelope("VOUCHER_QR_GENERATION_FAILED", error.message));
  }
});

app.post("/api/kiosk/v1/voucher-scan-sessions", requireDeviceAuth, async (req, res) => {
  const jobId = normalizeJobId(req.body?.job_id);
  const deviceId = normalizeOptional(req.get("X-Device-Id")) || normalizeOptional(req.body?.device_id) || "booth-local";
  if (!jobId) {
    return res.status(400).json(errorEnvelope("INVALID_VOUCHER_SCAN_SESSION_REQUEST", "job_id is required."));
  }

  try {
    const projectId = await resolveProjectIdForDevice(pool, deviceId);
    const sessionToken = createVoucherScanSessionToken();
    const expiresAt = new Date(Date.now() + (config.voucherScanSessionTtlSeconds * 1000));
    const result = await pool.query(
      `INSERT INTO voucher_scan_sessions (
          session_token,
          project_id,
          device_id,
          job_id,
          status,
          expires_at
        )
        VALUES ($1, $2, $3, $4, 'PENDING', $5)
        RETURNING session_token, project_id, device_id, job_id, status, voucher_code, voucher_status_json, expires_at, created_at, updated_at`,
      [sessionToken, projectId, deviceId, jobId, expiresAt]
    );

    return res.status(201).json({
      success: true,
      data: toVoucherScanSessionResponse(result.rows[0], req),
      error: null
    });
  } catch (error) {
    console.error("voucher_scan_session_create_failed", { jobId, deviceId, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "VOUCHER_SCAN_SESSION_CREATE_FAILED", error.message));
  }
});

app.get("/api/kiosk/v1/voucher-scan-sessions/:sessionToken", requireDeviceAuth, async (req, res) => {
  const sessionToken = normalizeVoucherScanSessionToken(req.params.sessionToken);
  const deviceId = normalizeOptional(req.get("X-Device-Id")) || "booth-local";
  if (!sessionToken) {
    return res.status(400).json(errorEnvelope("INVALID_VOUCHER_SCAN_SESSION", "session token is invalid."));
  }

  try {
    const projectId = await resolveProjectIdForDevice(pool, deviceId);
    const session = await loadVoucherScanSessionForKiosk(pool, projectId, deviceId, sessionToken);
    if (!session) {
      return res.status(404).json(errorEnvelope("VOUCHER_SCAN_SESSION_NOT_FOUND", "Voucher scan session was not found."));
    }

    return res.json({
      success: true,
      data: toVoucherScanSessionResponse(session, req),
      error: null
    });
  } catch (error) {
    console.error("voucher_scan_session_poll_failed", { sessionToken, deviceId, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "VOUCHER_SCAN_SESSION_POLL_FAILED", error.message));
  }
});

app.post("/api/kiosk/v1/voucher-scan-sessions/:sessionToken/ack", requireDeviceAuth, async (req, res) => {
  const sessionToken = normalizeVoucherScanSessionToken(req.params.sessionToken);
  const deviceId = normalizeOptional(req.get("X-Device-Id")) || normalizeOptional(req.body?.device_id) || "booth-local";
  const ackStatus = normalizeVoucherScanAckStatus(req.body?.status);
  if (!sessionToken || !ackStatus) {
    return res.status(400).json(errorEnvelope("INVALID_VOUCHER_SCAN_ACK", "session token and status are required."));
  }

  try {
    const projectId = await resolveProjectIdForDevice(pool, deviceId);
    const result = await pool.query(
      `UPDATE voucher_scan_sessions
       SET status = $4,
           voucher_code = CASE WHEN $4 = 'CONSUMED' THEN NULL ELSE voucher_code END,
           updated_at = NOW()
       WHERE session_token = $1
         AND project_id = $2
         AND device_id = $3
       RETURNING session_token, project_id, device_id, job_id, status, voucher_code, voucher_status_json, expires_at, created_at, updated_at`,
      [sessionToken, projectId, deviceId, ackStatus]
    );
    if (result.rowCount === 0) {
      return res.status(404).json(errorEnvelope("VOUCHER_SCAN_SESSION_NOT_FOUND", "Voucher scan session was not found."));
    }

    return res.json({
      success: true,
      data: toVoucherScanSessionResponse(result.rows[0], req),
      error: null
    });
  } catch (error) {
    console.error("voucher_scan_session_ack_failed", { sessionToken, deviceId, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "VOUCHER_SCAN_SESSION_ACK_FAILED", error.message));
  }
});

app.post("/api/web/v1/voucher-scan-sessions/:sessionToken/submit", async (req, res) => {
  const sessionToken = normalizeVoucherScanSessionToken(req.params.sessionToken);
  const voucherCode = normalizeVoucherCode(req.body?.voucher_code);
  if (!sessionToken || !voucherCode) {
    return res.status(400).json(errorEnvelope("INVALID_VOUCHER_SCAN_SUBMIT", "session token and voucher_code are required."));
  }

  try {
    const session = await loadVoucherScanSessionForSubmit(pool, sessionToken);
    if (!session) {
      return res.status(404).json(errorEnvelope("VOUCHER_SCAN_SESSION_NOT_FOUND", "Voucher scan session was not found or has expired."));
    }

    const requestedProjectId = normalizeProjectId(req.body?.project_id);
    if (requestedProjectId && requestedProjectId !== session.project_id) {
      return res.status(400).json(errorEnvelope("VOUCHER_PROJECT_MISMATCH", "Voucher project does not match this kiosk session."));
    }

    const voucher = await loadVoucherStatusByCode(pool, session.project_id, voucherCode);
    const voucherStatus = toVoucherStatusResponse(voucher, session.project_id);
    const nextStatus = voucherStatus.usable_now ? "SUBMITTED_AVAILABLE" : "SUBMITTED_REJECTED";
    const result = await pool.query(
      `UPDATE voucher_scan_sessions
       SET status = $2,
           voucher_code = $3,
           voucher_status_json = $4::jsonb,
           updated_at = NOW()
       WHERE session_token = $1
         AND expires_at > NOW()
         AND status IN ('PENDING', 'SUBMITTED_AVAILABLE', 'SUBMITTED_REJECTED')
       RETURNING session_token, project_id, device_id, job_id, status, voucher_code, voucher_status_json, expires_at, created_at, updated_at`,
      [sessionToken, nextStatus, voucherCode, JSON.stringify(voucherStatus)]
    );
    if (result.rowCount === 0) {
      return res.status(409).json(errorEnvelope("VOUCHER_SCAN_SESSION_CLOSED", "Voucher scan session is no longer accepting submissions."));
    }

    return res.json({
      success: true,
      data: {
        ...toVoucherScanSessionResponse(result.rows[0], req),
        voucher_status: voucherStatus
      },
      error: null
    });
  } catch (error) {
    console.error("voucher_scan_session_submit_failed", { sessionToken, error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "VOUCHER_SCAN_SESSION_SUBMIT_FAILED", error.message));
  }
});

app.get("/api/web/v1/voucher-scan-sessions/:sessionToken/qr.png", async (req, res) => {
  const sessionToken = normalizeVoucherScanSessionToken(req.params.sessionToken);
  if (!sessionToken) {
    return res.status(400).json(errorEnvelope("INVALID_VOUCHER_SCAN_SESSION", "session token is invalid."));
  }

  try {
    const session = await loadVoucherScanSessionByToken(pool, sessionToken);
    if (!session || new Date(session.expires_at).getTime() <= Date.now()) {
      return res.status(404).json(errorEnvelope("VOUCHER_SCAN_SESSION_NOT_FOUND", "Voucher scan session was not found or has expired."));
    }

    const png = await QRCode.toBuffer(buildVoucherScanUrl(req, sessionToken), {
      type: "png",
      errorCorrectionLevel: "M",
      margin: 1,
      width: 420
    });

    res.setHeader("Content-Type", "image/png");
    res.setHeader("Cache-Control", "no-store");
    return res.send(png);
  } catch (error) {
    console.error("voucher_scan_session_qr_failed", { sessionToken, error });
    return res.status(500).json(errorEnvelope("VOUCHER_SCAN_SESSION_QR_FAILED", error.message));
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
  { name: "motion_frame_files", maxCount: 600 }
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
    const routePrefix = await resolveDownloadRoutePrefix(client, jobId);
    const expectedCaptureTotal = requiredRawCaptureTotalForRoute(routePrefix, captureTotal);
    const rawCapturesComplete = rawCaptureCount >= expectedCaptureTotal;
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
      captureTotal: expectedCaptureTotal,
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
        capture_total: expectedCaptureTotal,
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

    const routePrefix = await resolveDownloadRoutePrefix(client, jobId);
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

app.get("/v1/assets/composed/featured", async (req, res) => {
  try {
    const selected = await selectFeaturedComposedAsset();
    if (!selected) {
      return res.status(404).json(errorEnvelope("NO_COMPOSED_ASSETS", "No composed assets were found."));
    }

    const assetUrl = `/files/${encodeURIPath(selected.remote_key)}`;
    if (String(req.query?.format || "").toLowerCase() === "json") {
      return res.json({
        success: true,
        data: {
          asset_type: selected.asset_type,
          selection_mode: selected.selection_mode,
          url: absolutePublicUrl(req, assetUrl),
          created_at: selected.created_at
        },
        error: null
      });
    }

    return res.redirect(302, assetUrl);
  } catch (error) {
    console.error("featured_composed_asset_failed", { error });
    return res.status(error.statusCode || 500).json(errorEnvelope(error.code || "FEATURED_COMPOSED_ASSET_FAILED", error.message));
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

app.get(["/d/:jobId/qr", "/world-tour/:jobId/qr", "/kooky-world/:jobId/qr"], async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).json(errorEnvelope("INVALID_JOB_ID", "Job id is required."));
  }

  try {
    const result = await pool.query(
      `SELECT project_id, upload_status, download_url
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
      if (rawCaptureCount < requiredRawCaptureCount(job.project_id)) {
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

app.get(["/d/:jobId/clip", "/world-tour/:jobId/clip", "/kooky-world/:jobId/clip"], async (req, res) => {
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

app.get(["/d/:jobId/clip.mp4", "/world-tour/:jobId/clip.mp4", "/kooky-world/:jobId/clip.mp4"], async (req, res) => {
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

    const responsePath = usesWorldTourPresentation(routePrefix)
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

app.get(["/d/:jobId/clip-download.mp4", "/world-tour/:jobId/clip-download.mp4", "/kooky-world/:jobId/clip-download.mp4"], async (req, res) => {
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

    const responsePath = usesWorldTourPresentation(routePrefix)
      ? await ensureWorldTourMotionVideo(jobResult.rows[0], absolutePath)
      : absolutePath;
    return sendAttachmentFile(res, responsePath, "video/mp4", `${jobId}-countdown.mp4`);
  } catch (error) {
    console.error("motion_video_attachment_download_failed", { jobId, error });
    return res.status(500).send("Internal server error.");
  }
});

app.get(["/d/:jobId/liveview.mp4", "/world-tour/:jobId/liveview.mp4", "/kooky-world/:jobId/liveview.mp4"], async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const routePrefix = downloadRoutePrefixFromRequest(req);
    const { job, assets } = await loadDownloadJobAssets(jobId);
    const rawCaptures = sortRawCaptureAssets(assets.filter((asset) => asset.asset_type === "raw_capture"));
    if (rawCaptures.length === 0) {
      return res.status(404).send("Raw captures were not uploaded for this job.");
    }

    const outputPath = (routePrefix === "kooky-world")
      ? await ensureKookyWorldLiveviewVideo(job, rawCaptures)
      : await ensureFramedLiveviewVideo(job, rawCaptures);
    res.setHeader("Content-Type", "video/mp4");
    res.setHeader("Cache-Control", "public, max-age=300");
    return res.sendFile(outputPath);
  } catch (error) {
    console.error("framed_liveview_video_failed", { jobId, error });
    return res.status(error.statusCode || 500).send(error.message || "Internal server error.");
  }
});

app.get(["/d/:jobId/liveview-download.mp4", "/world-tour/:jobId/liveview-download.mp4", "/kooky-world/:jobId/liveview-download.mp4"], async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const routePrefix = downloadRoutePrefixFromRequest(req);
    const { job, assets } = await loadDownloadJobAssets(jobId);
    const rawCaptures = sortRawCaptureAssets(assets.filter((asset) => asset.asset_type === "raw_capture"));
    if (rawCaptures.length === 0) {
      return res.status(404).send("Raw captures were not uploaded for this job.");
    }

    const outputPath = (routePrefix === "kooky-world")
      ? await ensureKookyWorldLiveviewVideo(job, rawCaptures)
      : await ensureFramedLiveviewVideo(job, rawCaptures);
    return sendAttachmentFile(res, outputPath, "video/mp4", `${jobId}-liveview.mp4`);
  } catch (error) {
    console.error("framed_liveview_video_download_failed", { jobId, error });
    return res.status(error.statusCode || 500).send(error.message || "Internal server error.");
  }
});

app.get(["/d/:jobId/framed-countdown.mp4", "/world-tour/:jobId/framed-countdown.mp4", "/kooky-world/:jobId/framed-countdown.mp4"], async (req, res) => {
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

    const outputPath = (routePrefix === "kooky-world")
      ? await ensureKookyWorldCountdownVideo(job, motionFrames)
      : usesWorldTourPresentation(routePrefix)
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

app.get(["/d/:jobId/countdown-download.mp4", "/world-tour/:jobId/countdown-download.mp4", "/kooky-world/:jobId/countdown-download.mp4"], async (req, res) => {
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

    const outputPath = (routePrefix === "kooky-world")
      ? await ensureKookyWorldCountdownVideo(job, motionFrames)
      : usesWorldTourPresentation(routePrefix)
        ? await ensureWorldTourCountdownVideo(job, motionFrames)
        : await ensureLegacyFramedCountdownVideo(job, motionFrames);
    return sendAttachmentFile(res, outputPath, "video/mp4", `${jobId}-countdown.mp4`);
  } catch (error) {
    console.error("framed_countdown_video_download_failed", { jobId, error });
    return res.status(error.statusCode || 500).send(error.message || "Internal server error.");
  }
});

app.get(["/d/:jobId/image", "/world-tour/:jobId/image", "/kooky-world/:jobId/image"], async (req, res) => {
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

app.get(["/d/:jobId/image-download", "/world-tour/:jobId/image-download", "/kooky-world/:jobId/image-download"], async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const routePrefix = downloadRoutePrefixFromRequest(req);
    const { job, assets } = await loadDownloadJobAssets(jobId);
    const rawCaptures = sortRawCaptureAssets(assets.filter((asset) => asset.asset_type === "raw_capture"));
    if (usesWorldTourPresentation(routePrefix)) {
      if (routePrefix === "kooky-world") {
        if (rawCaptures.length > 0) {
          const outputPath = await ensureKookyWorldPhotoImage(job, rawCaptures);
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
      }

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

app.get(["/d/:jobId", "/world-tour/:jobId", "/kooky-world/:jobId"], async (req, res) => {
  const jobId = normalizeJobId(req.params.jobId);
  if (!jobId) {
    return res.status(400).send("Invalid job id.");
  }

  try {
    const jobResult = await pool.query(
      `SELECT job_id, project_id, status, upload_status, remote_asset_key, download_url, session_folder, session_started_at_utc, theme_id, image_preview_id, passenger_name, created_at, published_at
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
    if ((job.upload_status !== "LINK_READY" || !job.remote_asset_key) && rawCaptures.length < requiredRawCaptureCount(job.project_id)) {
      return res.status(202).send("Download is still processing.");
    }

    const composed = findLatestAsset(assets, "composed") || { remote_key: job.remote_asset_key };
    const thumbnail = findLatestAsset(assets, "thumbnail") || composed;
    const liveImage = findLatestAsset(assets, "live_image");
    const motionVideo = findLatestAsset(assets, "motion_video");
    const motionFrames = assets.filter((asset) => asset.asset_type === "motion_frame");

    const routePrefix = downloadRoutePrefixFromRequest(req);
    const pageArgs = {
      job,
      composed,
      thumbnail,
      liveImage,
      motionVideo,
      rawCaptures,
      motionFrames
    };
    const page = usesWorldTourPresentation(routePrefix)
      ? renderWorldTourDownloadPage(pageArgs, routePrefix)
      : renderLegacyDownloadPage(pageArgs);

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

if (require.main === module) {
  initialize().then(() => {
    app.listen(config.port, "0.0.0.0", () => {
      console.log(`photo-booth-backend listening on ${config.port}`);
    });
  }).catch((error) => {
    console.error("startup_failed", error);
    process.exit(1);
  });
}

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
       ('prj_world_tour', 'WORLD_TOUR', 'World Tour Photo Booth', 'world-tour', 'ACTIVE'),
       ('prj_kooky_world', 'KOOKY_WORLD', 'Kooky World Photo Booth', 'kooky-world', 'ACTIVE')
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
  await pool.query(
    `INSERT INTO project_devices (project_id, device_id, active)
     VALUES ('prj_kooky_world', 'booth-kooky-01', TRUE)
     ON CONFLICT (device_id)
     DO UPDATE SET project_id = EXCLUDED.project_id, active = TRUE, updated_at = NOW()`
  );
  await pool.query(`
    CREATE TABLE IF NOT EXISTS user_devices (
      id BIGSERIAL PRIMARY KEY,
      project_id TEXT NOT NULL REFERENCES projects(id),
      device_token_hash TEXT NOT NULL UNIQUE,
      device_name TEXT NOT NULL,
      status TEXT NOT NULL DEFAULT 'ACTIVE',
      first_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      last_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
    )`);
  await pool.query("CREATE INDEX IF NOT EXISTS idx_user_devices_project_id ON user_devices(project_id)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_user_devices_status ON user_devices(status)");
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
    CREATE TABLE IF NOT EXISTS voucher_device_links (
      id BIGSERIAL PRIMARY KEY,
      project_id TEXT NOT NULL REFERENCES projects(id),
      device_id BIGINT NOT NULL REFERENCES user_devices(id) ON DELETE CASCADE,
      voucher_id BIGINT NOT NULL,
      voucher_code TEXT NOT NULL,
      status TEXT NOT NULL DEFAULT 'ACTIVE',
      claim_source TEXT,
      claimed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      last_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      FOREIGN KEY (project_id, voucher_id) REFERENCES vouchers(project_id, id)
    )`);
  await pool.query("CREATE UNIQUE INDEX IF NOT EXISTS idx_voucher_device_links_project_voucher ON voucher_device_links(project_id, voucher_id)");
  await pool.query("CREATE UNIQUE INDEX IF NOT EXISTS idx_voucher_device_links_project_code ON voucher_device_links(project_id, voucher_code)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_voucher_device_links_device_id ON voucher_device_links(device_id)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_voucher_device_links_status ON voucher_device_links(status)");
  await pool.query(`
    CREATE TABLE IF NOT EXISTS kiosk_sessions (
      id BIGSERIAL PRIMARY KEY,
      session_token TEXT NOT NULL UNIQUE,
      project_id TEXT NOT NULL REFERENCES projects(id),
      device_id TEXT NOT NULL,
      job_id TEXT NOT NULL,
      status TEXT NOT NULL DEFAULT 'PENDING',
      mobile_device_id BIGINT REFERENCES user_devices(id) ON DELETE SET NULL,
      voucher_link_id BIGINT REFERENCES voucher_device_links(id) ON DELETE SET NULL,
      voucher_code TEXT,
      voucher_status_json JSONB,
      qr_payload TEXT,
      expires_at TIMESTAMPTZ NOT NULL,
      attached_at TIMESTAMPTZ,
      redeemed_at TIMESTAMPTZ,
      cancelled_at TIMESTAMPTZ,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
    )`);
  await pool.query("CREATE INDEX IF NOT EXISTS idx_kiosk_sessions_token ON kiosk_sessions(session_token)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_kiosk_sessions_project_device ON kiosk_sessions(project_id, device_id)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_kiosk_sessions_status ON kiosk_sessions(status)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_kiosk_sessions_expires_at ON kiosk_sessions(expires_at)");
  await pool.query(`
    CREATE TABLE IF NOT EXISTS kiosk_session_events (
      id BIGSERIAL PRIMARY KEY,
      kiosk_session_id BIGINT NOT NULL REFERENCES kiosk_sessions(id) ON DELETE CASCADE,
      event_name TEXT NOT NULL,
      event_status TEXT,
      metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
    )`);
  await pool.query("CREATE INDEX IF NOT EXISTS idx_kiosk_session_events_session_id ON kiosk_session_events(kiosk_session_id)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_kiosk_session_events_event_name ON kiosk_session_events(event_name)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_kiosk_session_events_created_at ON kiosk_session_events(created_at DESC)");
  await pool.query(`
    CREATE TABLE IF NOT EXISTS voucher_scan_sessions (
      id BIGSERIAL PRIMARY KEY,
      session_token TEXT NOT NULL UNIQUE,
      project_id TEXT NOT NULL REFERENCES projects(id),
      device_id TEXT NOT NULL,
      job_id TEXT NOT NULL,
      status TEXT NOT NULL DEFAULT 'PENDING',
      voucher_code TEXT,
      voucher_status_json JSONB,
      expires_at TIMESTAMPTZ NOT NULL,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
    )`);
  await pool.query("CREATE INDEX IF NOT EXISTS idx_voucher_scan_sessions_token ON voucher_scan_sessions(session_token)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_voucher_scan_sessions_project_device ON voucher_scan_sessions(project_id, device_id)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_voucher_scan_sessions_status ON voucher_scan_sessions(status)");
  await pool.query("CREATE INDEX IF NOT EXISTS idx_voucher_scan_sessions_expires_at ON voucher_scan_sessions(expires_at)");
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
    `SELECT job_id, project_id, status, upload_status, remote_asset_key, download_url, session_folder, session_started_at_utc, theme_id, image_preview_id, passenger_name, created_at, published_at
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
  if ((job.upload_status !== "LINK_READY" || !job.remote_asset_key) && rawCaptureCount < requiredRawCaptureCount(job.project_id)) {
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

async function selectFeaturedComposedAsset() {
  const recentResult = await pool.query(
    `SELECT asset_type, remote_key, content_type, original_file_name, created_at
     FROM booth_assets
     WHERE asset_type = 'composed'
       AND created_at >= NOW() - ($1::int * INTERVAL '1 second')
     ORDER BY created_at DESC`,
    [config.featuredComposedRecencySeconds]
  );
  const recentAsset = findFirstExistingAsset(recentResult.rows);
  if (recentAsset) {
    return { ...recentAsset, selection_mode: "latest" };
  }

  const randomResult = await pool.query(
    `SELECT asset_type, remote_key, content_type, original_file_name, created_at
     FROM booth_assets
     WHERE asset_type = 'composed'
     ORDER BY RANDOM()`
  );
  const randomAsset = findFirstExistingAsset(randomResult.rows);
  if (randomAsset) {
    return { ...randomAsset, selection_mode: "random" };
  }

  return null;
}

function findFirstExistingAsset(assets) {
  return assets.find((asset) => uploadFileExists(asset?.remote_key)) || null;
}

function uploadFileExists(remoteKey) {
  const absolutePath = path.resolve(config.uploadsRoot, remoteKey || "");
  return absolutePath.startsWith(`${config.uploadsRoot}${path.sep}`) && fs.existsSync(absolutePath);
}

function absolutePublicUrl(req, routePath) {
  if (/^https?:\/\//i.test(routePath)) {
    return routePath;
  }

  const origin = `${req.protocol}://${req.get("host")}`;
  return `${origin}${routePath.startsWith("/") ? routePath : `/${routePath}`}`;
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

async function ensureKookyWorldNamedTemplate(job, labelTemplateId, templatePath) {
  const passengerName = normalizePassengerName(job.passenger_name);
  if (!passengerName) {
    return templatePath;
  }

  const fontPath = kookyWorldLabelFontPath();
  if (!fs.existsSync(fontPath)) {
    throw httpError(500, "LABEL_FONT_NOT_FOUND", "Label font was not found.");
  }

  const nameKey = passengerNameCacheKey(passengerName);
  const outputPath = path.join(generatedDirectory(job), `label_kooky-world_frame-${labelTemplateId}_name-${nameKey}_franie-v2.png`);
  if (fs.existsSync(outputPath)) {
    return outputPath;
  }

  const fontBuffer = fs.readFileSync(fontPath);
  const font = opentype.parse(fontBuffer.buffer.slice(fontBuffer.byteOffset, fontBuffer.byteOffset + fontBuffer.byteLength));

  let width, height, x, y, fontSize;
  if (labelTemplateId === "2") {
    width = 12640;
    height = 8399;
    x = Math.round(width * 0.330);
    y = Math.round(height * 0.240);
    fontSize = Math.round(width * 0.008);
  } else {
    width = 12657;
    height = 8445;
    x = Math.round(width * 0.455);
    y = Math.round(height * 0.206);
    fontSize = Math.round(width * 0.008);
  }

  const textPath = font.getPath(passengerName, 0, 0, fontSize);
  const bounds = textPath.getBoundingBox();
  const padding = 4;
  const textWidth = Math.max(1, Math.ceil(bounds.x2 - bounds.x1) + padding * 2);
  const textHeight = Math.max(1, Math.ceil(bounds.y2 - bounds.y1) + padding * 2);
  const pathData = textPath.toPathData(2);
  const svg = `<?xml version="1.0" encoding="UTF-8"?>
<svg width="${textWidth}" height="${textHeight}" viewBox="${bounds.x1 - padding} ${bounds.y1 - padding} ${textWidth} ${textHeight}" xmlns="http://www.w3.org/2000/svg">
  <path d="${pathData}" fill="${labelTemplateId === "2" ? "#FFFFFF" : "#231F20"}"/>
</svg>`;
  const textOverlay = await sharp(Buffer.from(svg), { density: 72 }).png().toBuffer();

  await sharp(templatePath)
    .composite([{ input: textOverlay, top: Math.max(0, y - padding), left: Math.max(0, x - padding) }])
    .png()
    .toFile(outputPath);
  return outputPath;
}

async function ensureKookyWorldPhotoImage(job, rawCaptures) {
  const labelTemplateId = resolveLabelTemplateId(job.image_preview_id || job.theme_id);
  const nameKey = passengerNameCacheKey(job.passenger_name);
  const outputPath = path.join(generatedDirectory(job), `photo_kooky-world_frame-${labelTemplateId}_name-${nameKey}_v7_3072_q86.jpg`);
  if (fs.existsSync(outputPath)) {
    return outputPath;
  }

  const templatePath = path.join(config.publicRoot, "kooky-world", `ticket_${labelTemplateId}.png`);
  if (!fs.existsSync(templatePath)) {
    throw httpError(500, "FRAME_TEMPLATE_NOT_FOUND", "Kooky World template was not found.");
  }

  const namedTemplatePath = await ensureKookyWorldNamedTemplate(job, labelTemplateId, templatePath);
  const sourcePath = path.join(generatedDirectory(job), `photo_kooky-world_frame-${labelTemplateId}_name-${nameKey}_v7_source.png`);
  if (!fs.existsSync(sourcePath)) {
    await renderKookyWorldFramedPng(rawCaptures, sourcePath, namedTemplatePath, labelTemplateId);
  }

  await compressStillImage(sourcePath, outputPath, "format=yuvj420p");
  return outputPath;
}

async function renderKookyWorldFramedPng(rawCaptures, outputPath, templatePath, labelTemplateId) {
  if (!fs.existsSync(templatePath)) {
    throw httpError(500, "FRAME_TEMPLATE_NOT_FOUND", "Frame template was not found.");
  }

  const inputs = ["-y", "-i", templatePath];
  const imagePaths = rawCaptures.map((asset) => {
    if (typeof asset === "string") {
      return asset;
    }
    return absoluteUploadPath(asset?.remote_key);
  }).filter(Boolean).slice(0, 3);

  for (const imagePath of imagePaths) {
    inputs.push("-i", imagePath);
  }

  const stickerPaths = kookyWorldStickerFileNames(labelTemplateId)
    .map((fileName) => path.join(config.publicRoot, "kooky-world", fileName));
  const activeStickerPaths = stickerPaths.slice(0, imagePaths.length);
  for (const stickerPath of activeStickerPaths) {
    inputs.push("-i", stickerPath);
  }

  // Use the actual template dimensions so video generation can work from a
  // memory-efficient derivative while still sharing the exact slot geometry.
  const templateMetadata = await sharp(templatePath).metadata();
  const width = templateMetadata.width;
  const height = templateMetadata.height;
  let slots;
  if (labelTemplateId === "2") {
    slots = [
      { x: 0.07476, y: 0.50411, w: 0.26400, h: 0.37516 },
      { x: 0.36780, y: 0.50411, w: 0.26400, h: 0.37516 },
      { x: 0.66092, y: 0.50411, w: 0.26400, h: 0.37516 }
    ];
  } else {
    slots = [
      { x: 0.07277, y: 0.50409, w: 0.26452, h: 0.37430 },
      { x: 0.36636, y: 0.50409, w: 0.26444, h: 0.37430 },
      { x: 0.65995, y: 0.50409, w: 0.26444, h: 0.37430 }
    ];
  }

  const slotDimensions = slots.map(s => ({
    x: Math.round(width * s.x),
    y: Math.round(height * s.y),
    width: Math.round(width * s.w),
    height: Math.round(height * s.h)
  }));

  const filters = [];
  let baseLabel = "[0:v]";
  imagePaths.forEach((_, index) => {
    const slot = slotDimensions[index];
    const rawLabel = `raw${index}`;
    const stickLabel = `stick${index}`;
    const overlayPhotoLabel = `photo${index}`;
    const isLastImage = index === imagePaths.length - 1;
    const outputLabel = isLastImage ? "out" : `tmp${index}`;

    filters.push(`[${index + 1}:v]scale=${slot.width}:${slot.height}:force_original_aspect_ratio=increase,crop=${slot.width}:${slot.height}[${rawLabel}]`);

    const stickerInputIndex = imagePaths.length + 1 + index;
    filters.push(`[${stickerInputIndex}:v]scale=${slot.width}:${slot.height}[${stickLabel}]`);

    filters.push(`${baseLabel}[${rawLabel}]overlay=${slot.x}:${slot.y}[${overlayPhotoLabel}]`);
    filters.push(`[${overlayPhotoLabel}][${stickLabel}]overlay=${slot.x}:${slot.y}[${outputLabel}]`);

    baseLabel = `[${outputLabel}]`;
  });

  if (imagePaths.length === 0) {
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

async function ensureKookyWorldCountdownVideo(job, motionFrames) {
  const labelTemplateId = resolveLabelTemplateId(job.image_preview_id || job.theme_id);
  const nameKey = passengerNameCacheKey(job.passenger_name);
  const frameDurationSeconds = resolveKookyWorldCountdownFrameDurationSeconds(motionFrames);
  const outputPath = path.join(generatedDirectory(job), `framed-countdown_kooky-world_frame-${labelTemplateId}_name-${nameKey}_fps-${frameDurationCacheKey(frameDurationSeconds)}_v8.mp4`);
  if (isNonEmptyFile(outputPath)) {
    return outputPath;
  }
  return generateMediaOnce(outputPath, async () => {
    const slotFrames = buildCountdownSlotFrameAssets(motionFrames)
      .slice(0, 3)
      .map((frames) => frames.map((asset) => absoluteUploadPath(asset.remote_key)));
    const maxFrameCount = Math.max(...slotFrames.map((frames) => frames.length));
    if (maxFrameCount <= 0) {
      throw httpError(404, "MOTION_FRAMES_NOT_FOUND", "Motion frame files were not found.");
    }

    const frameSets = Array.from({ length: maxFrameCount }, (_, frameIndex) =>
      slotFrames.map((frames) => frames.length > 0 ? frames[frameIndex % frames.length] : ""));

    await renderKookyWorldFramedVideo(
      job,
      frameSets,
      frameDurationSeconds,
      outputPath,
      path.join(generatedDirectory(job), `countdown_kooky-world_frame-${labelTemplateId}_name-${nameKey}_fps-${frameDurationCacheKey(frameDurationSeconds)}_v8_frames`)
    );
  });
}

function resolveKookyWorldCountdownFrameDurationSeconds(motionFrames) {
  const slotFrameCounts = buildCountdownSlotFrameAssets(motionFrames)
    .slice(0, 3)
    .map((frames) => frames.length)
    .filter((count) => count > 0);
  const maxFramesPerCapture = Math.max(0, ...slotFrameCounts);
  if (maxFramesPerCapture <= 0) {
    return 0.25;
  }

  // Kooky records a five-second pre-capture clip for each of the three photos.
  // Infer the actual sampled FPS from uploaded frame count so 30fps clips play
  // at real speed while old 4fps/15fps jobs keep the correct timing.
  const inferredFps = Math.min(30, Math.max(1, maxFramesPerCapture / 5));
  return 1 / inferredFps;
}

function frameDurationCacheKey(frameDurationSeconds) {
  return Math.round(frameDurationSeconds * 10000).toString();
}

async function ensureKookyWorldLiveviewVideo(job, rawCaptures) {
  const labelTemplateId = resolveLabelTemplateId(job.image_preview_id || job.theme_id);
  const nameKey = passengerNameCacheKey(job.passenger_name);
  const outputPath = path.join(generatedDirectory(job), `liveview_kooky-world_frame-${labelTemplateId}_name-${nameKey}_v7.mp4`);
  if (isNonEmptyFile(outputPath)) {
    return outputPath;
  }
  return generateMediaOnce(outputPath, async () => {
    const rawPaths = rawCaptures.slice(0, 3).map((asset) => absoluteUploadPath(asset.remote_key));
    const frameSets = buildRotatingFrameSets(rawPaths, 2);

    await renderKookyWorldFramedVideo(
      job,
      frameSets,
      0.25,
      outputPath,
      path.join(generatedDirectory(job), `liveview_kooky-world_frame-${labelTemplateId}_name-${nameKey}_v7_frames`)
    );
  });
}

function isNonEmptyFile(filePath) {
  return fs.existsSync(filePath) && fs.statSync(filePath).size > 0;
}

async function generateMediaOnce(outputPath, generator) {
  if (isNonEmptyFile(outputPath)) {
    return outputPath;
  }
  if (generatedMediaPromises.has(outputPath)) {
    return generatedMediaPromises.get(outputPath);
  }

  const generation = (async () => {
    if (fs.existsSync(outputPath)) {
      fs.unlinkSync(outputPath);
    }
    await generator();
    if (!isNonEmptyFile(outputPath)) {
      throw httpError(500, "MEDIA_GENERATION_EMPTY", "Generated media file was empty.");
    }
    return outputPath;
  })().finally(() => generatedMediaPromises.delete(outputPath));

  generatedMediaPromises.set(outputPath, generation);
  return generation;
}

function buildRotatingFrameSets(imagePaths, rounds = 2) {
  const paths = Array.isArray(imagePaths) ? imagePaths.filter(Boolean).slice(0, 3) : [];
  if (paths.length === 0) {
    return [];
  }

  const cycle = paths.map((_, rotation) =>
    paths.map((__, slotIndex) => paths[(slotIndex + rotation) % paths.length]));
  return Array.from({ length: Math.max(1, rounds) }, () => cycle)
    .flat()
    .map((frameSet) => [...frameSet]);
}

async function renderKookyWorldFramedVideo(job, frameSets, frameDurationSeconds, outputPath, frameDirectory) {
  const labelTemplateId = resolveLabelTemplateId(job.image_preview_id || job.theme_id);
  const templatePath = path.join(config.publicRoot, "kooky-world", `ticket_${labelTemplateId}.png`);
  if (!fs.existsSync(templatePath)) {
    throw httpError(500, "FRAME_TEMPLATE_NOT_FOUND", "Kooky World template was not found.");
  }

  const namedTemplatePath = await ensureKookyWorldNamedTemplate(job, labelTemplateId, templatePath);
  const videoTemplatePath = path.join(
    generatedDirectory(job),
    `label_kooky-world_frame-${labelTemplateId}_name-${passengerNameCacheKey(job.passenger_name)}_franie-v2_video-1800.png`
  );
  if (!fs.existsSync(videoTemplatePath)) {
    await sharp(namedTemplatePath)
      .resize({ width: 1800, withoutEnlargement: true })
      .png()
      .toFile(videoTemplatePath);
  }
  fs.mkdirSync(frameDirectory, { recursive: true });
  const framePaths = [];

  for (let index = 0; index < frameSets.length; index += 1) {
    const framePath = path.join(frameDirectory, `frame_${String(index).padStart(3, "0")}.png`);
    await renderKookyWorldFramedPng(frameSets[index], framePath, videoTemplatePath, labelTemplateId);
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

function kookyWorldLabelFontPath() {
  return path.join(config.publicRoot, "kooky-world", "Franie-SBold.otf");
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

async function loadVoucherStatusByCode(queryable, projectId, voucherCode) {
  const result = await queryable.query(
    `SELECT v.id AS voucher_id,
            v.project_id,
            p.code AS project_code,
            v.code_masked,
            v.benefit_type,
            v.benefit_value_minor,
            v.benefit_percent,
            v.max_uses,
            v.used_count,
            v.status,
            v.valid_from,
            v.valid_until
     FROM vouchers v
     JOIN projects p ON p.id = v.project_id
     WHERE v.project_id = $1
       AND v.code_hash = $2
     LIMIT 1`,
    [projectId, hashVoucherCode(projectId, voucherCode)]
  );

  return result.rowCount > 0 ? result.rows[0] : null;
}

function toVoucherStatusResponse(voucher, projectId) {
  if (!voucher) {
    return {
      exists: false,
      project_id: projectId,
      status: "NOT_FOUND",
      usable_now: false,
      has_been_used: false,
      quota_exhausted: false,
      remaining_uses: 0
    };
  }

  const now = Date.now();
  const usedCount = Number(voucher.used_count || 0);
  const maxUses = Number(voucher.max_uses || 0);
  const remainingUses = Math.max(0, maxUses - usedCount);
  const validFromMs = voucher.valid_from ? new Date(voucher.valid_from).getTime() : null;
  const validUntilMs = voucher.valid_until ? new Date(voucher.valid_until).getTime() : null;
  const notStarted = validFromMs !== null && validFromMs > now;
  const expired = validUntilMs !== null && validUntilMs < now;
  const inactive = voucher.status !== "ACTIVE";
  const quotaExhausted = remainingUses < 1;
  let status = "AVAILABLE";

  if (inactive) {
    status = "INACTIVE";
  } else if (notStarted) {
    status = "NOT_STARTED";
  } else if (expired) {
    status = "EXPIRED";
  } else if (quotaExhausted) {
    status = "USED";
  }

  return {
    exists: true,
    project_id: voucher.project_id,
    project_code: voucher.project_code,
    code_masked: voucher.code_masked,
    status,
    usable_now: status === "AVAILABLE",
    has_been_used: usedCount > 0,
    quota_exhausted: quotaExhausted,
    used_count: usedCount,
    max_uses: maxUses,
    remaining_uses: remainingUses,
    benefit_type: voucher.benefit_type,
    benefit_value_minor: Number(voucher.benefit_value_minor || 0),
    benefit_percent: Number(voucher.benefit_percent || 0),
    valid_from: voucher.valid_from,
    valid_until: voucher.valid_until
  };
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

function normalizeAutoVoucherCode(value) {
  const normalized = normalizeVoucherCode(value);
  return /^PB-[0-9A-F]{8}$/.test(normalized || "") ? normalized : null;
}

function normalizeVoucherScanSessionToken(value) {
  const normalized = normalizeOptional(value);
  if (!normalized) {
    return null;
  }

  const safe = normalized.replace(/[^A-Za-z0-9_-]/g, "");
  return safe.length >= 16 && safe.length <= 96 ? safe : null;
}

function normalizeVoucherScanAckStatus(value) {
  const normalized = normalizeOptional(value)?.toUpperCase();
  return normalized === "CONSUMED" || normalized === "CANCELLED" ? normalized : null;
}

function normalizeRoutePath(value) {
  const normalized = normalizeOptional(value) || "/voucher-scan";
  return normalized.startsWith("/") ? normalized : `/${normalized}`;
}

function createVoucherScanSessionToken() {
  return crypto.randomBytes(24).toString("base64url");
}

function createKioskSessionToken() {
  return crypto.randomBytes(24).toString("base64url");
}

function buildVoucherScanUrl(req, sessionToken) {
  const pathPrefix = config.voucherScanPath.startsWith("/") ? config.voucherScanPath : `/${config.voucherScanPath}`;
  return `${config.voucherScanBaseUrl}${pathPrefix}?session=${encodeURIComponent(sessionToken)}`;
}

function buildVoucherScanSessionQrPngUrl(req, sessionToken) {
  return `${resolveOpenApiServerUrl(req)}/api/web/v1/voucher-scan-sessions/${encodeURIComponent(sessionToken)}/qr.png`;
}

function buildKioskSessionUrl(req, sessionToken) {
  const pathPrefix = config.voucherLinkPath.startsWith("/") ? config.voucherLinkPath : `/${config.voucherLinkPath}`;
  return `${config.voucherLinkBaseUrl}${pathPrefix}?session=${encodeURIComponent(sessionToken)}`;
}

function buildKioskSessionQrPngUrl(req, sessionToken) {
  return `${resolveOpenApiServerUrl(req)}/api/web/v1/kiosk-sessions/${encodeURIComponent(sessionToken)}/qr.png`;
}

function buildVoucherQrPngUrl(req, voucherCode) {
  const normalized = normalizeAutoVoucherCode(voucherCode);
  if (!normalized) {
    return null;
  }

  return `${resolveOpenApiServerUrl(req)}/api/web/v1/vouchers/${encodeURIComponent(normalized)}/qr.png`;
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

function normalizeDeviceToken(value) {
  const normalized = normalizeOptional(value);
  if (!normalized) {
    return null;
  }

  const safe = normalized.replace(/[^A-Za-z0-9_-]/g, "");
  return safe.length >= 16 && safe.length <= 128 ? safe : null;
}

function extractBearerToken(value) {
  const normalized = normalizeOptional(value);
  if (!normalized) {
    return null;
  }

  const match = normalized.match(/^Bearer\s+(.+)$/i);
  return match ? normalizeDeviceToken(match[1]) : null;
}

function getDeviceTokenFromRequest(req) {
  return extractBearerToken(req.get("Authorization")) || normalizeDeviceToken(req.body?.device_token) || normalizeDeviceToken(req.query?.device_token);
}

function hashDeviceToken(deviceToken) {
  const normalized = normalizeDeviceToken(deviceToken);
  if (!normalized) {
    return null;
  }

  return crypto.createHash("sha256").update(normalized).digest("hex");
}

function createDeviceToken() {
  return crypto.randomBytes(24).toString("base64url");
}

function normalizeDeviceName(value) {
  const normalized = normalizeOptional(value) || "PHONE";
  return normalized.slice(0, 80);
}

function normalizeKioskSessionStatus(value) {
  const normalized = normalizeOptional(value)?.toUpperCase();
  const allowed = new Set(["PENDING", "ATTACHED", "REDEEMED", "EXPIRED", "CANCELLED", "CONSUMED"]);
  return allowed.has(normalized) ? normalized : null;
}

function normalizeUserDeviceStatus(value) {
  const normalized = normalizeOptional(value)?.toUpperCase();
  return normalized === "ACTIVE" || normalized === "DISABLED" ? normalized : null;
}

function normalizeVoucherDeviceLinkStatus(value) {
  const normalized = normalizeOptional(value)?.toUpperCase();
  return normalized === "ACTIVE" || normalized === "REVOKED" ? normalized : null;
}

async function loadVoucherScanSessionByToken(queryable, sessionToken) {
  const result = await queryable.query(
    `SELECT session_token, project_id, device_id, job_id, status, voucher_code, voucher_status_json, expires_at, created_at, updated_at
     FROM voucher_scan_sessions
     WHERE session_token = $1
     LIMIT 1`,
    [sessionToken]
  );
  return result.rows[0] || null;
}

async function loadVoucherScanSessionForSubmit(queryable, sessionToken) {
  await expireVoucherScanSession(queryable, sessionToken);
  const result = await queryable.query(
    `SELECT session_token, project_id, device_id, job_id, status, voucher_code, voucher_status_json, expires_at, created_at, updated_at
     FROM voucher_scan_sessions
     WHERE session_token = $1
       AND expires_at > NOW()
       AND status IN ('PENDING', 'SUBMITTED_AVAILABLE', 'SUBMITTED_REJECTED')
     LIMIT 1`,
    [sessionToken]
  );
  return result.rows[0] || null;
}

async function loadVoucherScanSessionForKiosk(queryable, projectId, deviceId, sessionToken) {
  await expireVoucherScanSession(queryable, sessionToken);
  const result = await queryable.query(
    `SELECT session_token, project_id, device_id, job_id, status, voucher_code, voucher_status_json, expires_at, created_at, updated_at
     FROM voucher_scan_sessions
     WHERE session_token = $1
       AND project_id = $2
       AND device_id = $3
     LIMIT 1`,
    [sessionToken, projectId, deviceId]
  );
  return result.rows[0] || null;
}

async function expireVoucherScanSession(queryable, sessionToken) {
  await queryable.query(
    `UPDATE voucher_scan_sessions
     SET status = 'EXPIRED',
         updated_at = NOW()
     WHERE session_token = $1
       AND expires_at <= NOW()
      AND status IN ('PENDING', 'SUBMITTED_AVAILABLE', 'SUBMITTED_REJECTED')`,
    [sessionToken]
  );
}

async function loadUserDeviceByToken(queryable, deviceToken) {
  const tokenHash = hashDeviceToken(deviceToken);
  if (!tokenHash) {
    return null;
  }

  const result = await queryable.query(
    `SELECT id, project_id, device_token_hash, device_name, status, first_seen_at, last_seen_at, metadata, created_at, updated_at
     FROM user_devices
     WHERE device_token_hash = $1
     LIMIT 1`,
    [tokenHash]
  );
  return result.rows[0] || null;
}

async function loadUserDeviceById(queryable, deviceId) {
  const normalizedDeviceId = Number.parseInt(deviceId, 10);
  if (!Number.isFinite(normalizedDeviceId) || normalizedDeviceId < 1) {
    return null;
  }

  const result = await queryable.query(
    `SELECT id, project_id, device_token_hash, device_name, status, first_seen_at, last_seen_at, metadata, created_at, updated_at
     FROM user_devices
     WHERE id = $1
     LIMIT 1`,
    [normalizedDeviceId]
  );
  return result.rows[0] || null;
}

async function saveUserDevice(queryable, input) {
  const deviceToken = normalizeDeviceToken(input.deviceToken);
  const tokenHash = hashDeviceToken(deviceToken);
  if (!deviceToken || !tokenHash) {
    throw httpError(400, "INVALID_DEVICE_TOKEN", "device token is invalid.");
  }

  const projectId = normalizeProjectId(input.projectId) || config.defaultProjectId;
  const deviceName = normalizeDeviceName(input.deviceName);
  const status = normalizeUserDeviceStatus(input.status) || "ACTIVE";
  const result = await queryable.query(
    `INSERT INTO user_devices (
        project_id,
        device_token_hash,
        device_name,
        status,
        metadata,
        first_seen_at,
        last_seen_at
      )
      VALUES ($1, $2, $3, $4, COALESCE($5, '{}'::jsonb), NOW(), NOW())
      ON CONFLICT (device_token_hash)
      DO UPDATE SET
        project_id = EXCLUDED.project_id,
        device_name = EXCLUDED.device_name,
        status = COALESCE(EXCLUDED.status, user_devices.status),
        metadata = COALESCE(EXCLUDED.metadata, user_devices.metadata),
        last_seen_at = NOW(),
        updated_at = NOW()
      RETURNING id, project_id, device_token_hash, device_name, status, first_seen_at, last_seen_at, metadata, created_at, updated_at`,
    [
      projectId,
      tokenHash,
      deviceName,
      status,
      input.metadata ? input.metadata : null
    ]
  );
  return result.rows[0] || null;
}

async function touchUserDevice(queryable, deviceId) {
  const normalizedDeviceId = Number.parseInt(deviceId, 10);
  if (!Number.isFinite(normalizedDeviceId) || normalizedDeviceId < 1) {
    return null;
  }

  const result = await queryable.query(
    `UPDATE user_devices
     SET last_seen_at = NOW(),
         updated_at = NOW()
     WHERE id = $1
     RETURNING id, project_id, device_token_hash, device_name, status, first_seen_at, last_seen_at, metadata, created_at, updated_at`,
    [normalizedDeviceId]
  );
  return result.rows[0] || null;
}

async function loadVoucherDeviceLinkByVoucherCode(queryable, projectId, voucherCode) {
  const normalizedVoucherCode = normalizeVoucherCode(voucherCode);
  if (!normalizedVoucherCode) {
    return null;
  }

  const result = await queryable.query(
    `SELECT l.id, l.project_id, l.device_id, l.voucher_id, l.voucher_code, l.status, l.claim_source, l.claimed_at, l.last_seen_at, l.metadata, l.created_at, l.updated_at,
            u.device_name, u.device_token_hash
     FROM voucher_device_links l
     JOIN user_devices u ON u.id = l.device_id
     WHERE l.project_id = $1
       AND l.voucher_code = $2
     LIMIT 1`,
    [projectId, normalizedVoucherCode]
  );
  return result.rows[0] || null;
}

async function loadVoucherLinksForDevice(queryable, projectId, deviceId) {
  const result = await queryable.query(
    `SELECT l.id, l.project_id, l.device_id, l.voucher_id, l.voucher_code, l.status, l.claim_source, l.claimed_at, l.last_seen_at, l.metadata, l.created_at, l.updated_at,
            u.device_name
     FROM voucher_device_links l
     JOIN user_devices u ON u.id = l.device_id
     WHERE l.project_id = $1
       AND l.device_id = $2
       AND l.status = 'ACTIVE'
     ORDER BY l.last_seen_at DESC, l.created_at DESC, l.id DESC`,
    [projectId, deviceId]
  );
  return result.rows;
}

async function loadKioskSessionByToken(queryable, sessionToken) {
  const result = await queryable.query(
    `SELECT ks.id, ks.session_token, ks.project_id, ks.device_id, ks.job_id, ks.status, ks.mobile_device_id,
            ks.voucher_link_id, ks.voucher_code, ks.voucher_status_json, ks.qr_payload, ks.expires_at,
            ks.attached_at, ks.redeemed_at, ks.cancelled_at, ks.created_at, ks.updated_at,
            ud.device_name AS mobile_device_name
     FROM kiosk_sessions ks
     LEFT JOIN user_devices ud ON ud.id = ks.mobile_device_id
     WHERE ks.session_token = $1
     LIMIT 1`,
    [sessionToken]
  );
  return result.rows[0] || null;
}

async function loadKioskSessionForKiosk(queryable, projectId, deviceId, sessionToken) {
  await expireKioskSession(queryable, sessionToken);
  const result = await queryable.query(
    `SELECT ks.id, ks.session_token, ks.project_id, ks.device_id, ks.job_id, ks.status, ks.mobile_device_id,
            ks.voucher_link_id, ks.voucher_code, ks.voucher_status_json, ks.qr_payload, ks.expires_at,
            ks.attached_at, ks.redeemed_at, ks.cancelled_at, ks.created_at, ks.updated_at,
            ud.device_name AS mobile_device_name
     FROM kiosk_sessions ks
     LEFT JOIN user_devices ud ON ud.id = ks.mobile_device_id
     WHERE ks.session_token = $1
       AND ks.project_id = $2
       AND ks.device_id = $3
     LIMIT 1`,
    [sessionToken, projectId, deviceId]
  );
  return result.rows[0] || null;
}

async function loadKioskSessionForWeb(queryable, sessionToken) {
  await expireKioskSession(queryable, sessionToken);
  return loadKioskSessionByToken(queryable, sessionToken);
}

async function expireKioskSession(queryable, sessionToken) {
  await queryable.query(
    `UPDATE kiosk_sessions
     SET status = 'EXPIRED',
         updated_at = NOW()
     WHERE session_token = $1
       AND expires_at <= NOW()
       AND status IN ('PENDING', 'ATTACHED')`,
    [sessionToken]
  );
}

async function recordKioskSessionEvent(queryable, kioskSessionId, eventName, eventStatus, metadata = {}) {
  if (!kioskSessionId) {
    return;
  }

  await queryable.query(
    `INSERT INTO kiosk_session_events (
        kiosk_session_id,
        event_name,
        event_status,
        metadata
      )
      VALUES ($1, $2, $3, COALESCE($4, '{}'::jsonb))`,
    [kioskSessionId, eventName, eventStatus || null, metadata && Object.keys(metadata).length ? metadata : null]
  );
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

function toVoucherScanSessionResponse(row, req) {
  if (!row) {
    return null;
  }

  return {
    session_token: row.session_token,
    project_id: row.project_id,
    device_id: row.device_id,
    job_id: row.job_id,
    status: row.status,
    voucher_code: row.voucher_code || null,
    voucher_status: row.voucher_status_json || null,
    scan_url: buildVoucherScanUrl(req, row.session_token),
    qr_png_url: buildVoucherScanSessionQrPngUrl(req, row.session_token),
    expires_at: row.expires_at,
    created_at: row.created_at,
    updated_at: row.updated_at
  };
}

function toUserDeviceResponse(row) {
  if (!row) {
    return null;
  }

  return {
    device_id: row.id,
    project_id: row.project_id,
    device_name: row.device_name,
    status: row.status,
    first_seen_at: row.first_seen_at,
    last_seen_at: row.last_seen_at,
    created_at: row.created_at,
    updated_at: row.updated_at
  };
}

function toKioskSessionResponse(row, req) {
  if (!row) {
    return null;
  }

  const status = row.status || "PENDING";
  const voucherStatus = row.voucher_status_json || null;
  return {
    session_token: row.session_token,
    project_id: row.project_id,
    device_id: row.device_id,
    job_id: row.job_id,
    status,
    voucher_attached: Boolean(row.voucher_code) || status === "ATTACHED" || status === "REDEEMED",
    mobile_device_id: row.mobile_device_id || null,
    mobile_device_name: row.mobile_device_name || null,
    voucher_link_id: row.voucher_link_id || null,
    voucher_code: row.voucher_code || null,
    voucher_status: voucherStatus,
    scan_url: buildKioskSessionUrl(req, row.session_token),
    qr_png_url: buildKioskSessionQrPngUrl(req, row.session_token),
    expires_at: row.expires_at,
    attached_at: row.attached_at,
    redeemed_at: row.redeemed_at,
    cancelled_at: row.cancelled_at,
    created_at: row.created_at,
    updated_at: row.updated_at
  };
}

function buildOpenApiSpec(req) {
  const serverUrl = resolveOpenApiServerUrl(req);
  return {
    openapi: "3.0.3",
    info: {
      title: "Photo Booth Backend API",
      version: "0.3.0",
      description: "Admin voucher generation and kiosk voucher checkout APIs."
    },
    servers: [
      {
        url: serverUrl,
        description: "Current backend"
      }
    ],
    tags: [
      { name: "Admin Vouchers" },
      { name: "Admin Projects" },
      { name: "Web Vouchers" },
      { name: "Kiosk Voucher Scan" },
      { name: "Kiosk Checkout" }
    ],
    components: {
      securitySchemes: {
        AdminBearerAuth: {
          type: "http",
          scheme: "bearer",
          bearerFormat: "admin token",
          description: "Use ADMIN_BEARER_TOKEN."
        },
        DeviceBearerAuth: {
          type: "http",
          scheme: "bearer",
          bearerFormat: "device token",
          description: "Use DEVICE_BEARER_TOKEN for kiosk/device endpoints when configured."
        }
      },
      schemas: {
        ErrorEnvelope: {
          type: "object",
          properties: {
            success: { type: "boolean", example: false },
            data: { nullable: true, example: null },
            error: {
              type: "object",
              properties: {
                code: { type: "string", example: "VOUCHER_CODE_EXISTS" },
                message: { type: "string", example: "duplicate key value violates unique constraint" }
              }
            }
          }
        },
        VoucherGenerateRequest: {
          type: "object",
          required: ["project_id", "benefit_type"],
          properties: {
            project_id: {
              type: "string",
              example: "prj_world_tour",
              description: "Project this voucher belongs to. Voucher cannot be redeemed across projects."
            },
            campaign_name: {
              type: "string",
              example: "World Tour VIP June"
            },
            purpose: {
              type: "string",
              example: "Sponsor guest"
            },
            code_mode: {
              type: "string",
              enum: ["AUTO", "MANUAL"],
              default: "AUTO",
              example: "AUTO"
            },
            code_name: {
              type: "string",
              example: "WORLDTOUR-VIP-001",
              description: "Required only when code_mode is MANUAL. MANUAL supports quantity = 1."
            },
            benefit_type: {
              type: "string",
              enum: ["FREE_SESSION", "FIXED_DISCOUNT", "PERCENT_DISCOUNT", "FREE_ADDON", "STAFF_TEST"],
              example: "FREE_SESSION"
            },
            benefit_value_minor: {
              type: "integer",
              minimum: 0,
              default: 0,
              example: 10000,
              description: "Used by FIXED_DISCOUNT. Minor units, e.g. satang/cents."
            },
            benefit_percent: {
              type: "integer",
              minimum: 0,
              maximum: 100,
              default: 0,
              example: 20,
              description: "Used by PERCENT_DISCOUNT."
            },
            quantity: {
              type: "integer",
              minimum: 1,
              maximum: 500,
              default: 1,
              example: 10,
              description: "How many voucher codes to create."
            },
            max_uses_per_code: {
              type: "integer",
              minimum: 1,
              default: 1,
              example: 3,
              description: "How many times each generated code can be used."
            },
            valid_from: {
              type: "string",
              format: "date-time",
              nullable: true,
              example: "2026-06-09T00:00:00Z"
            },
            valid_until: {
              type: "string",
              format: "date-time",
              nullable: true,
              example: "2026-06-30T16:59:59Z",
              description: "Expiry time. Use UTC ISO-8601."
            }
          }
        },
        VoucherGenerateResponse: {
          type: "object",
          properties: {
            success: { type: "boolean", example: true },
            data: {
              type: "object",
              properties: {
                project_id: { type: "string", example: "prj_world_tour" },
                campaign_id: { type: "string", example: "12" },
                quantity: { type: "integer", example: 1 },
                vouchers: {
                  type: "array",
                  items: {
                    type: "object",
                    properties: {
                      voucher_id: { type: "string", example: "34" },
                      code: {
                        type: "string",
                        example: "PB-ADF2633C",
                        description: "Plain code is returned once at generation time. Backend stores only the hash."
                      },
                      qr_png_url: {
                        type: "string",
                        format: "uri",
                        nullable: true,
                        example: "https://api.wajanapir.com/api/web/v1/vouchers/PB-ADF2633C/qr.png",
                        description: "PNG QR image URL for AUTO voucher codes matching PB-XXXXXXXX. Null for non-auto/manual formats."
                      },
                      project_id: { type: "string", example: "prj_world_tour" },
                      code_masked: { type: "string", example: "PB-A...633C" },
                      benefit_type: { type: "string", example: "FREE_SESSION" },
                      benefit_value_minor: { type: "integer", example: 0 },
                      benefit_percent: { type: "integer", example: 0 },
                      max_uses: { type: "integer", example: 3 },
                      used_count: { type: "integer", example: 0 },
                      status: { type: "string", example: "ACTIVE" },
                      valid_from: { type: "string", format: "date-time" },
                      valid_until: { type: "string", format: "date-time", nullable: true },
                      created_at: { type: "string", format: "date-time" }
                    }
                  }
                }
              }
            },
            error: { nullable: true, example: null }
          }
        },
        VoucherValidateRequest: {
          type: "object",
          required: ["job_id", "voucher_code"],
          properties: {
            job_id: { type: "string", example: "JOB-20260609-001" },
            voucher_code: { type: "string", example: "PB-ADF2633C" },
            device_id: { type: "string", example: "booth-world-tour-01" }
          }
        },
        VoucherStatusRequest: {
          type: "object",
          required: ["voucher_code"],
          properties: {
            voucher_code: { type: "string", example: "PB-ADF2633C" },
            project_id: {
              type: "string",
              example: "prj_world_tour",
              description: "Optional. Defaults to DEFAULT_PROJECT_ID."
            }
          }
        },
        VoucherStatusResponse: {
          type: "object",
          properties: {
            success: { type: "boolean", example: true },
            data: {
              type: "object",
              properties: {
                exists: { type: "boolean", example: true },
                project_id: { type: "string", example: "prj_world_tour" },
                project_code: { type: "string", example: "world-tour" },
                code_masked: { type: "string", example: "PB-A...633C" },
                status: {
                  type: "string",
                  enum: ["AVAILABLE", "USED", "EXPIRED", "NOT_STARTED", "INACTIVE", "NOT_FOUND"],
                  example: "USED"
                },
                usable_now: { type: "boolean", example: false },
                has_been_used: { type: "boolean", example: true },
                quota_exhausted: { type: "boolean", example: true },
                used_count: { type: "integer", example: 1 },
                max_uses: { type: "integer", example: 1 },
                remaining_uses: { type: "integer", example: 0 },
                benefit_type: { type: "string", example: "FREE_SESSION" },
                benefit_value_minor: { type: "integer", example: 0 },
                benefit_percent: { type: "integer", example: 0 },
                valid_from: { type: "string", format: "date-time" },
                valid_until: { type: "string", format: "date-time", nullable: true }
              }
            },
            error: { nullable: true, example: null }
          }
        },
        VoucherReserveRequest: {
          type: "object",
          required: ["job_id", "checkout_token", "idempotency_key"],
          properties: {
            job_id: { type: "string", example: "JOB-20260609-001" },
            checkout_token: { type: "string", example: "token-from-validate" },
            idempotency_key: { type: "string", example: "booth-world-tour-01-JOB-20260609-001-1" },
            gross_amount_minor: { type: "integer", example: 20000 },
            currency: { type: "string", example: "THB" },
            device_id: { type: "string", example: "booth-world-tour-01" }
          }
        },
        VoucherRedemptionActionRequest: {
          type: "object",
          properties: {
            device_id: {
              type: "string",
              example: "booth-world-tour-01",
              description: "Optional when X-Device-Id header is sent."
            }
          }
        },
        VoucherApplyResponse: {
          type: "object",
          properties: {
            success: { type: "boolean", example: true },
            data: {
              type: "object",
              properties: {
                redemption_id: { type: "integer", example: 12 },
                status: { type: "string", example: "APPLIED" }
              }
            },
            error: { nullable: true, example: null }
          }
        },
        VoucherReleaseResponse: {
          type: "object",
          properties: {
            success: { type: "boolean", example: true },
            data: {
              type: "object",
              properties: {
                redemption_id: { type: "integer", example: 12 },
                status: { type: "string", example: "RELEASED" }
              }
            },
            error: { nullable: true, example: null }
          }
        }
      }
    },
    paths: {
      "/v1/assets/composed/featured": {
        get: {
          tags: ["Assets"],
          summary: "Get featured composed image",
          description: "Redirects to the newest composed asset from the recent window, or a random composed asset when no recent image exists. Add ?format=json for debugging metadata.",
          parameters: [
            {
              name: "format",
              in: "query",
              required: false,
              schema: { type: "string", enum: ["json"] },
              description: "Return JSON metadata instead of redirecting to the image."
            }
          ],
          responses: {
            302: { description: "Redirects to the selected image under /files/." },
            200: {
              description: "Selected image metadata when format=json.",
              content: {
                "application/json": {
                  schema: {
                    allOf: [
                      { $ref: "#/components/schemas/SuccessEnvelope" },
                      {
                        type: "object",
                        properties: {
                          data: {
                            type: "object",
                            properties: {
                              asset_type: { type: "string", example: "composed" },
                              selection_mode: { type: "string", enum: ["latest", "random"] },
                              url: { type: "string", format: "uri" },
                              created_at: { type: "string", format: "date-time" }
                            }
                          }
                        }
                      }
                    ]
                  }
                }
              }
            },
            404: { description: "No composed assets were found.", content: { "application/json": { schema: { $ref: "#/components/schemas/ErrorEnvelope" } } } }
          }
        }
      },
      "/api/admin/v1/vouchers/generate": {
        post: {
          tags: ["Admin Vouchers"],
          summary: "Generate voucher codes",
          description: "Creates one or more voucher codes. Plain codes are returned only once in this response.",
          security: [{ AdminBearerAuth: [] }],
          requestBody: {
            required: true,
            content: {
              "application/json": {
                schema: { $ref: "#/components/schemas/VoucherGenerateRequest" },
                examples: {
                  autoFreeSession: {
                    summary: "Auto-generate 10 free-session vouchers",
                    value: {
                      project_id: "prj_world_tour",
                      campaign_name: "World Tour VIP June",
                      purpose: "Sponsor guest",
                      code_mode: "AUTO",
                      benefit_type: "FREE_SESSION",
                      quantity: 10,
                      max_uses_per_code: 3,
                      valid_until: "2026-06-30T16:59:59Z"
                    }
                  },
                  manualCode: {
                    summary: "Create one manual code",
                    value: {
                      project_id: "prj_world_tour",
                      campaign_name: "VIP Manual",
                      purpose: "VIP guest",
                      code_mode: "MANUAL",
                      code_name: "WORLDTOUR-VIP-001",
                      benefit_type: "FREE_SESSION",
                      quantity: 1,
                      max_uses_per_code: 5,
                      valid_until: "2026-06-30T16:59:59Z"
                    }
                  },
                  fixedDiscount: {
                    summary: "Fixed discount",
                    value: {
                      project_id: "prj_world_tour",
                      campaign_name: "Discount 100 THB",
                      code_mode: "AUTO",
                      benefit_type: "FIXED_DISCOUNT",
                      benefit_value_minor: 10000,
                      quantity: 20,
                      max_uses_per_code: 1,
                      valid_until: "2026-06-30T16:59:59Z"
                    }
                  }
                }
              }
            }
          },
          responses: {
            201: {
              description: "Voucher codes created.",
              content: {
                "application/json": {
                  schema: { $ref: "#/components/schemas/VoucherGenerateResponse" }
                }
              }
            },
            400: { description: "Invalid request.", content: { "application/json": { schema: { $ref: "#/components/schemas/ErrorEnvelope" } } } },
            401: { description: "Invalid admin token.", content: { "application/json": { schema: { $ref: "#/components/schemas/ErrorEnvelope" } } } },
            409: { description: "Manual code already exists.", content: { "application/json": { schema: { $ref: "#/components/schemas/ErrorEnvelope" } } } }
          }
        }
      },
      "/api/admin/v1/projects": {
        get: {
          tags: ["Admin Projects"],
          summary: "List projects",
          security: [{ AdminBearerAuth: [] }],
          responses: { 200: { description: "Project list." } }
        }
      },
      "/api/admin/v1/vouchers": {
        get: {
          tags: ["Admin Vouchers"],
          summary: "List vouchers",
          security: [{ AdminBearerAuth: [] }],
          parameters: [
            { name: "project_id", in: "query", required: false, schema: { type: "string" }, example: "prj_world_tour" }
          ],
          responses: { 200: { description: "Voucher list." } }
        }
      },
      "/api/web/v1/vouchers/status": {
        post: {
          tags: ["Web Vouchers"],
          summary: "Check voucher usage status",
          description: "Frontend-friendly voucher status check. It only requires voucher_code and does not reserve or consume usage.",
          requestBody: {
            required: true,
            content: {
              "application/json": {
                schema: { $ref: "#/components/schemas/VoucherStatusRequest" },
                examples: {
                  checkCode: {
                    summary: "Check one voucher code",
                    value: {
                      voucher_code: "PB-ADF2633C"
                    }
                  }
                }
              }
            }
          },
          responses: {
            200: {
              description: "Voucher usage status. NOT_FOUND is returned as success data for easy frontend handling.",
              content: {
                "application/json": {
                  schema: { $ref: "#/components/schemas/VoucherStatusResponse" }
                }
              }
            },
            400: { description: "voucher_code is missing.", content: { "application/json": { schema: { $ref: "#/components/schemas/ErrorEnvelope" } } } }
          }
        }
      },
      "/api/web/v1/vouchers/{voucherCode}/qr.png": {
        get: {
          tags: ["Web Vouchers"],
          summary: "Render voucher code QR PNG",
          description: "Returns a PNG QR code whose payload is the voucher code text. Only PB-XXXXXXXX voucher codes are accepted.",
          parameters: [
            {
              name: "voucherCode",
              in: "path",
              required: true,
              schema: {
                type: "string",
                pattern: "^PB-[0-9A-F]{8}$"
              },
              example: "PB-ADF2633C"
            }
          ],
          responses: {
            200: {
              description: "Voucher QR PNG image.",
              content: {
                "image/png": {
                  schema: {
                    type: "string",
                    format: "binary"
                  }
                }
              }
            },
            400: { description: "voucherCode does not match PB-XXXXXXXX.", content: { "application/json": { schema: { $ref: "#/components/schemas/ErrorEnvelope" } } } }
          }
        }
      },
      "/api/kiosk/v1/voucher-scan-sessions": {
        post: {
          tags: ["Kiosk Voucher Scan"],
          summary: "Create a phone voucher scan session",
          description: "Kiosk creates a short-lived session and receives scan_url plus qr_png_url for users to scan with their phone.",
          security: [{ DeviceBearerAuth: [] }],
          requestBody: {
            required: true,
            content: {
              "application/json": {
                examples: {
                  create: {
                    value: {
                      job_id: "JOB-20260609-001",
                      device_id: "booth-world-tour-01"
                    }
                  }
                }
              }
            }
          },
          responses: { 201: { description: "Scan session created." } }
        }
      },
      "/api/kiosk/v1/voucher-scan-sessions/{sessionToken}": {
        get: {
          tags: ["Kiosk Voucher Scan"],
          summary: "Poll a phone voucher scan session",
          description: "Returns PENDING, SUBMITTED_AVAILABLE, SUBMITTED_REJECTED, EXPIRED, CONSUMED, or CANCELLED.",
          security: [{ DeviceBearerAuth: [] }],
          parameters: [
            { name: "sessionToken", in: "path", required: true, schema: { type: "string" } }
          ],
          responses: { 200: { description: "Current scan session state." } }
        }
      },
      "/api/kiosk/v1/voucher-scan-sessions/{sessionToken}/ack": {
        post: {
          tags: ["Kiosk Voucher Scan"],
          summary: "Acknowledge a phone voucher scan session",
          description: "Kiosk marks a session as CONSUMED or CANCELLED after handling the submitted voucher.",
          security: [{ DeviceBearerAuth: [] }],
          parameters: [
            { name: "sessionToken", in: "path", required: true, schema: { type: "string" } }
          ],
          requestBody: {
            required: true,
            content: {
              "application/json": {
                examples: {
                  consumed: { value: { status: "CONSUMED", device_id: "booth-world-tour-01" } },
                  cancelled: { value: { status: "CANCELLED", device_id: "booth-world-tour-01" } }
                }
              }
            }
          },
          responses: { 200: { description: "Scan session acknowledged." } }
        }
      },
      "/api/web/v1/voucher-scan-sessions/{sessionToken}/submit": {
        post: {
          tags: ["Web Vouchers"],
          summary: "Submit a voucher code from the phone scan page",
          description: "Frontend submits the user's voucher code to the kiosk scan session. This checks status only; Unity still redeems the voucher.",
          parameters: [
            { name: "sessionToken", in: "path", required: true, schema: { type: "string" } }
          ],
          requestBody: {
            required: true,
            content: {
              "application/json": {
                examples: {
                  submit: { value: { voucher_code: "PB-ADF2633C" } }
                }
              }
            }
          },
          responses: { 200: { description: "Voucher status stored on the scan session." } }
        }
      },
      "/api/web/v1/voucher-scan-sessions/{sessionToken}/qr.png": {
        get: {
          tags: ["Web Vouchers"],
          summary: "Render phone scan session QR PNG",
          description: "Returns a PNG QR whose payload is the frontend scan URL.",
          parameters: [
            { name: "sessionToken", in: "path", required: true, schema: { type: "string" } }
          ],
          responses: { 200: { description: "Scan session QR PNG image." } }
        }
      },
      "/api/kiosk/v1/kiosk-sessions": {
        post: {
          tags: ["Kiosk Voucher Link"],
          summary: "Create a kiosk session for mobile voucher linking",
          security: [{ DeviceBearerAuth: [] }],
          responses: { 201: { description: "Kiosk session created." } }
        }
      },
      "/api/kiosk/v1/kiosk-sessions/{sessionToken}": {
        get: {
          tags: ["Kiosk Voucher Link"],
          summary: "Poll a kiosk session",
          security: [{ DeviceBearerAuth: [] }],
          responses: { 200: { description: "Kiosk session state." } }
        }
      },
      "/api/kiosk/v1/kiosk-sessions/{sessionToken}/ack": {
        post: {
          tags: ["Kiosk Voucher Link"],
          summary: "Acknowledge a kiosk session",
          security: [{ DeviceBearerAuth: [] }],
          responses: { 200: { description: "Kiosk session acknowledged." } }
        }
      },
      "/api/web/v1/kiosk-sessions/{sessionToken}": {
        get: {
          tags: ["Web Kiosk Link"],
          summary: "Read a kiosk session from mobile",
          responses: { 200: { description: "Kiosk session state." } }
        }
      },
      "/api/web/v1/kiosk-sessions/{sessionToken}/qr.png": {
        get: {
          tags: ["Web Kiosk Link"],
          summary: "Render kiosk session QR PNG",
          responses: { 200: { description: "Kiosk session QR image." } }
        }
      },
      "/api/web/v1/device/me": {
        get: {
          tags: ["Web Device"],
          summary: "Read the current mobile device",
          responses: { 200: { description: "Current device." } }
        }
      },
      "/api/web/v1/device/claim-voucher": {
        post: {
          tags: ["Web Device"],
          summary: "Claim or pair the current mobile device",
          responses: { 200: { description: "Device claimed." } }
        }
      },
      "/api/web/v1/me/vouchers": {
        get: {
          tags: ["Web Device"],
          summary: "List vouchers linked to the current mobile device",
          responses: { 200: { description: "Voucher list." } }
        }
      },
      "/api/web/v1/kiosk-sessions/{sessionToken}/attach-voucher": {
        post: {
          tags: ["Web Kiosk Link"],
          summary: "Attach a voucher to a kiosk session",
          responses: { 200: { description: "Voucher attached." } }
        }
      },
      "/api/kiosk/v1/checkout/voucher/validate": {
        post: {
          tags: ["Kiosk Checkout"],
          summary: "Validate voucher before reserve",
          security: [{ DeviceBearerAuth: [] }],
          requestBody: {
            required: true,
            content: { "application/json": { schema: { $ref: "#/components/schemas/VoucherValidateRequest" } } }
          },
          responses: { 200: { description: "Voucher is valid and checkout_token is returned." } }
        }
      },
      "/api/kiosk/v1/checkout/voucher/reserve": {
        post: {
          tags: ["Kiosk Checkout"],
          summary: "Reserve voucher for a job",
          security: [{ DeviceBearerAuth: [] }],
          requestBody: {
            required: true,
            content: { "application/json": { schema: { $ref: "#/components/schemas/VoucherReserveRequest" } } }
          },
          responses: { 201: { description: "Voucher reserved or applied if net amount is zero." } }
        }
      },
      "/api/kiosk/v1/checkout/voucher/{redemptionId}/apply": {
        post: {
          tags: ["Kiosk Checkout"],
          summary: "Apply reserved voucher redemption",
          description: "Marks a RESERVED redemption as APPLIED after the remaining payment is confirmed. Calling this on an already APPLIED redemption is idempotent.",
          security: [{ DeviceBearerAuth: [] }],
          parameters: [
            {
              name: "redemptionId",
              in: "path",
              required: true,
              schema: { type: "integer", minimum: 1 },
              example: 12
            }
          ],
          requestBody: {
            required: false,
            content: {
              "application/json": {
                schema: { $ref: "#/components/schemas/VoucherRedemptionActionRequest" },
                examples: {
                  apply: {
                    summary: "Apply a redemption",
                    value: {
                      device_id: "booth-world-tour-01"
                    }
                  }
                }
              }
            }
          },
          responses: {
            200: {
              description: "Redemption is applied.",
              content: { "application/json": { schema: { $ref: "#/components/schemas/VoucherApplyResponse" } } }
            },
            400: { description: "Invalid redemption id.", content: { "application/json": { schema: { $ref: "#/components/schemas/ErrorEnvelope" } } } },
            401: { description: "Invalid device token.", content: { "application/json": { schema: { $ref: "#/components/schemas/ErrorEnvelope" } } } },
            404: { description: "Redemption was not found.", content: { "application/json": { schema: { $ref: "#/components/schemas/ErrorEnvelope" } } } },
            409: { description: "Redemption was already released.", content: { "application/json": { schema: { $ref: "#/components/schemas/ErrorEnvelope" } } } }
          }
        }
      },
      "/api/kiosk/v1/checkout/voucher/{redemptionId}/release": {
        post: {
          tags: ["Kiosk Checkout"],
          summary: "Release reserved voucher redemption",
          description: "Cancels a RESERVED redemption and returns one usage to the voucher. This does not release APPLIED redemptions.",
          security: [{ DeviceBearerAuth: [] }],
          parameters: [
            {
              name: "redemptionId",
              in: "path",
              required: true,
              schema: { type: "integer", minimum: 1 },
              example: 12
            }
          ],
          requestBody: {
            required: false,
            content: {
              "application/json": {
                schema: { $ref: "#/components/schemas/VoucherRedemptionActionRequest" },
                examples: {
                  release: {
                    summary: "Release a redemption",
                    value: {
                      device_id: "booth-world-tour-01"
                    }
                  }
                }
              }
            }
          },
          responses: {
            200: {
              description: "Reserved redemption is released.",
              content: { "application/json": { schema: { $ref: "#/components/schemas/VoucherReleaseResponse" } } }
            },
            400: { description: "Invalid redemption id.", content: { "application/json": { schema: { $ref: "#/components/schemas/ErrorEnvelope" } } } },
            401: { description: "Invalid device token.", content: { "application/json": { schema: { $ref: "#/components/schemas/ErrorEnvelope" } } } }
          }
        }
      }
    }
  };
}

function resolveOpenApiServerUrl(req) {
  if (config.publicBaseUrlConfigured) {
    return config.publicBaseUrl;
  }

  const forwardedProto = normalizeOptional(req.get("x-forwarded-proto"))?.split(",")[0]?.trim();
  const forwardedHost = normalizeOptional(req.get("x-forwarded-host"))?.split(",")[0]?.trim();
  const protocol = forwardedProto || req.protocol || "http";
  const host = forwardedHost || req.get("host") || `localhost:${config.port}`;
  return `${protocol}://${host}`.replace(/\/$/, "");
}

function renderSwaggerDocsPage() {
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Photo Booth API Docs</title>
  <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/swagger-ui-dist@5/swagger-ui.css">
  <style>
    body { margin: 0; background: #f7f7f4; }
    .topbar { padding: 12px 18px; background: #10212a; color: white; font-family: Inter, system-ui, sans-serif; }
    .topbar b { margin-right: 12px; }
    .topbar a { color: #b9e6ef; }
  </style>
</head>
<body>
  <div class="topbar"><b>Photo Booth API Docs</b><a href="/api/docs/openapi.json">OpenAPI JSON</a></div>
  <div id="swagger-ui"></div>
  <script src="https://cdn.jsdelivr.net/npm/swagger-ui-dist@5/swagger-ui-bundle.js"></script>
  <script>
    window.ui = SwaggerUIBundle({
      url: "/api/docs/openapi.json",
      dom_id: "#swagger-ui",
      deepLinking: true,
      persistAuthorization: true
    });
  </script>
</body>
</html>`;
}

function renderVoucherScanPage() {
  return `<!doctype html>
<html lang="th">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
  <title>Scan Voucher</title>
  <style>
    :root { --bg:#111914; --panel:#f7f0d8; --ink:#172018; --muted:#667061; --green:#bdf042; --red:#cc3a32; --yellow:#f5d33f; }
    * { box-sizing:border-box; }
    body { margin:0; min-height:100vh; background:radial-gradient(circle at 50% -10%, #2c4932 0, #111914 48%, #080b09 100%); color:var(--panel); font-family:system-ui,-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif; }
    main { width:min(560px, 100%); margin:0 auto; padding:24px 18px 40px; }
    .hero { text-align:center; margin:10px 0 18px; }
    h1 { margin:0; font-size:30px; line-height:1; letter-spacing:.02em; text-transform:uppercase; }
    .sub { color:#c8d4c2; margin:10px auto 0; max-width:360px; line-height:1.45; font-size:14px; }
    .card { background:var(--panel); color:var(--ink); border-radius:24px; padding:16px; box-shadow:0 20px 60px rgba(0,0,0,.35); }
    .video-wrap { position:relative; overflow:hidden; border-radius:18px; background:#050605; min-height:310px; display:grid; place-items:center; }
    video { width:100%; min-height:310px; object-fit:cover; display:block; background:#050605; }
    .reticle { position:absolute; width:58%; aspect-ratio:1; border:3px solid rgba(189,240,66,.95); border-radius:18px; box-shadow:0 0 0 999px rgba(0,0,0,.28); pointer-events:none; }
    .reticle:before, .reticle:after { content:""; position:absolute; inset:18px; border:1px dashed rgba(255,255,255,.5); border-radius:12px; }
    .unsupported { display:none; text-align:center; padding:38px 22px; color:#d9dfd5; }
    .status { min-height:24px; margin:14px 2px 4px; color:var(--muted); font-size:14px; line-height:1.45; text-align:center; }
    .status.ok { color:#1d6b37; font-weight:800; }
    .status.error { color:var(--red); font-weight:800; }
    .manual { margin-top:14px; display:grid; gap:10px; }
    label { font-size:12px; color:var(--muted); font-weight:700; text-transform:uppercase; letter-spacing:.08em; }
    input { width:100%; border:2px solid #ddd2aa; border-radius:14px; padding:15px 14px; font:inherit; font-size:18px; font-weight:800; text-transform:uppercase; letter-spacing:.06em; color:var(--ink); background:#fffaf0; }
    button { border:0; border-radius:14px; padding:15px 16px; font:inherit; font-weight:900; letter-spacing:.02em; cursor:pointer; background:var(--yellow); color:#15180f; box-shadow:0 8px 0 rgba(0,0,0,.1); }
    button.secondary { background:#202820; color:#fff; box-shadow:none; }
    button:disabled { opacity:.55; cursor:not-allowed; }
    .actions { display:grid; grid-template-columns:1fr 1fr; gap:10px; }
    .hint { margin-top:14px; color:#7c8579; font-size:12px; line-height:1.45; text-align:center; }
    .missing { min-height:60vh; display:grid; place-items:center; text-align:center; }
    code { background:rgba(0,0,0,.08); padding:2px 5px; border-radius:6px; }
  </style>
</head>
<body>
  <main>
    <section class="hero">
      <h1>Scan Voucher</h1>
      <p class="sub">สแกน QR voucher หรือกรอกรหัส voucher เพื่อส่งกลับไปยังตู้ถ่ายรูป</p>
    </section>
    <section id="missing" class="missing" hidden>
      <div>
        <h1>Session Missing</h1>
        <p class="sub">ไม่พบ session สำหรับตู้ กรุณาสแกน QR จากหน้าตู้ใหม่อีกครั้ง</p>
      </div>
    </section>
    <section id="scanner" class="card">
      <div class="video-wrap">
        <video id="video" playsinline muted></video>
        <div class="reticle"></div>
        <div id="unsupported" class="unsupported">เบราว์เซอร์นี้ไม่รองรับ QR scan อัตโนมัติ<br>กรุณากรอกรหัสด้านล่างแทน</div>
      </div>
      <div id="status" class="status">กำลังเปิดกล้อง...</div>
      <div class="manual">
        <label for="voucherCode">Voucher Code</label>
        <input id="voucherCode" autocomplete="one-time-code" inputmode="text" placeholder="PB-ADF2633C">
        <div class="actions">
          <button id="submitBtn">SUBMIT</button>
          <button id="restartBtn" class="secondary" type="button">SCAN AGAIN</button>
        </div>
      </div>
      <div class="hint">หน้านี้เช็คสถานะก่อนเท่านั้น การตัดสิทธิ์จริงจะทำที่ตู้หลังจากส่งรหัสสำเร็จ</div>
    </section>
  </main>
  <script>
    const params = new URLSearchParams(location.search);
    const session = params.get("session") || "";
    const video = document.getElementById("video");
    const statusEl = document.getElementById("status");
    const input = document.getElementById("voucherCode");
    const submitBtn = document.getElementById("submitBtn");
    const restartBtn = document.getElementById("restartBtn");
    const unsupported = document.getElementById("unsupported");
    const missing = document.getElementById("missing");
    const scanner = document.getElementById("scanner");
    let detector = null;
    let stream = null;
    let scanning = false;
    let submitting = false;

    function setStatus(message, kind) {
      statusEl.textContent = message || "";
      statusEl.className = "status" + (kind ? " " + kind : "");
    }

    function extractVoucherCode(payload) {
      const text = String(payload || "").trim().toUpperCase();
      const match = text.match(/PB-[A-Z0-9._-]{4,80}/);
      if (match) return match[0];
      const suffix = text.replace(/[^A-Z0-9]/g, "");
      if (/^(?=.*[A-Z])(?=.*[0-9])[A-Z0-9]{6,21}$/.test(suffix)) return "PB-" + suffix;
      return "";
    }

    async function submitVoucher(rawValue) {
      const voucherCode = extractVoucherCode(rawValue);
      if (!voucherCode) {
        setStatus("QR ไม่ใช่ Voucher", "error");
        return;
      }

      if (submitting) return;
      submitting = true;
      submitBtn.disabled = true;
      input.value = voucherCode;
      setStatus("กำลังเช็ค Voucher...", "");

      try {
        const response = await fetch("/api/web/v1/voucher-scan-sessions/" + encodeURIComponent(session) + "/submit", {
          method: "POST",
          headers: { "Content-Type": "application/json", "Accept": "application/json" },
          body: JSON.stringify({ voucher_code: voucherCode })
        });
        const body = await response.json().catch(() => ({ success:false, error:{ message:response.statusText } }));
        if (!response.ok || body.success === false) throw new Error((body.error && body.error.message) || response.statusText);

        const voucherStatus = body.data && body.data.voucher_status ? body.data.voucher_status : {};
        if (voucherStatus.usable_now) {
          setStatus("ส่ง Voucher กลับไปที่ตู้แล้ว", "ok");
          stopCamera();
          return;
        }

        if (voucherStatus.status === "NOT_FOUND" || voucherStatus.exists === false) {
          setStatus("ไม่พบ Voucher", "error");
        } else if (voucherStatus.status === "USED" || voucherStatus.quota_exhausted || voucherStatus.has_been_used) {
          setStatus("Voucher นี้ถูกใช้ไปแล้ว", "error");
        } else {
          setStatus("Voucher ไม่พร้อมใช้งาน: " + (voucherStatus.status || "UNKNOWN"), "error");
        }
      } catch (error) {
        setStatus(error.message || "ส่ง Voucher ไม่สำเร็จ", "error");
      } finally {
        submitting = false;
        submitBtn.disabled = false;
      }
    }

    async function scanLoop() {
      if (!scanning || !detector || video.readyState < 2) {
        if (scanning) requestAnimationFrame(scanLoop);
        return;
      }

      try {
        const codes = await detector.detect(video);
        if (codes && codes.length > 0) {
          await submitVoucher(codes[0].rawValue || "");
        }
      } catch (error) {
        console.warn(error);
      }

      if (scanning) requestAnimationFrame(scanLoop);
    }

    async function startCamera() {
      if (!session) {
        scanner.hidden = true;
        missing.hidden = false;
        return;
      }

      stopCamera();
      if (!("BarcodeDetector" in window)) {
        unsupported.style.display = "block";
        video.style.display = "none";
        setStatus("กรอกรหัส Voucher ด้วยตัวเอง", "");
        return;
      }

      try {
        detector = new BarcodeDetector({ formats: ["qr_code"] });
        stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: "environment" }, audio: false });
        video.srcObject = stream;
        await video.play();
        unsupported.style.display = "none";
        video.style.display = "block";
        scanning = true;
        setStatus("สแกน QR voucher ได้เลย", "");
        requestAnimationFrame(scanLoop);
      } catch (error) {
        unsupported.style.display = "block";
        video.style.display = "none";
        setStatus("เปิดกล้องไม่ได้ กรุณากรอกรหัสแทน", "error");
      }
    }

    function stopCamera() {
      scanning = false;
      if (stream) {
        for (const track of stream.getTracks()) track.stop();
      }
      stream = null;
      video.srcObject = null;
    }

    submitBtn.addEventListener("click", () => submitVoucher(input.value));
    input.addEventListener("keydown", (event) => {
      if (event.key === "Enter") submitVoucher(input.value);
    });
    restartBtn.addEventListener("click", startCamera);
    window.addEventListener("pagehide", stopCamera);
    startCamera();
  </script>
</body>
</html>`;
}

function renderVoucherLinkPage() {
  return `<!doctype html>
<html lang="th">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
  <title>Voucher Link</title>
  <style>
    :root { --bg:#0f1a14; --panel:#f5f0dc; --ink:#162117; --muted:#66715f; --green:#97d43a; --yellow:#f1d348; --red:#d04a3c; --line:#d9cfb1; }
    * { box-sizing:border-box; }
    body { margin:0; min-height:100vh; background:radial-gradient(circle at 50% -10%, #2e5630 0, #0f1a14 48%, #070b08 100%); color:var(--panel); font-family:system-ui,-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif; }
    main { width:min(860px, 100%); margin:0 auto; padding:20px 16px 42px; display:grid; gap:14px; }
    .hero { text-align:center; padding:12px 8px 2px; }
    h1 { margin:0; font-size:clamp(30px, 7vw, 52px); line-height:1; letter-spacing:.03em; text-transform:uppercase; }
    .sub { margin:10px auto 0; max-width:560px; color:#cfd8c8; line-height:1.45; font-size:15px; }
    .card { background:var(--panel); color:var(--ink); border-radius:24px; padding:16px; box-shadow:0 18px 52px rgba(0,0,0,.32); }
    .grid { display:grid; grid-template-columns:1fr 1fr; gap:14px; }
    .stack { display:grid; gap:12px; }
    .title { margin:0 0 10px; font-size:15px; letter-spacing:.04em; text-transform:uppercase; }
    .status { min-height:22px; color:var(--muted); font-size:14px; line-height:1.45; }
    .status.ok { color:#136a33; font-weight:800; }
    .status.error { color:var(--red); font-weight:800; }
    .pill { display:inline-flex; align-items:center; gap:8px; padding:8px 12px; border-radius:999px; background:#1b2a21; color:#d7ebd2; font-weight:800; font-size:12px; letter-spacing:.05em; text-transform:uppercase; }
    .voucher-list { display:grid; gap:10px; margin-top:12px; }
    .voucher { border:1px solid var(--line); border-radius:18px; padding:12px 14px; background:#fffdf5; display:grid; gap:8px; }
    .voucher.selected { border-color:#88c73a; box-shadow:0 0 0 2px rgba(136,199,58,.16) inset; }
    .voucher h4 { margin:0; font-size:16px; }
    .voucher small { color:var(--muted); }
    .voucher .row { display:flex; flex-wrap:wrap; justify-content:space-between; gap:8px; align-items:center; }
    .voucher button, .primary, .secondary { border:0; border-radius:14px; padding:13px 16px; font:inherit; font-weight:900; cursor:pointer; }
    .primary { background:var(--yellow); color:#16180d; box-shadow:0 8px 0 rgba(0,0,0,.1); }
    .secondary { background:#1f2b1f; color:#fff; }
    .ghost { background:#ece7d6; color:var(--ink); }
    .actions { display:flex; flex-wrap:wrap; gap:10px; margin-top:12px; }
    label { display:block; margin:0 0 6px; color:var(--muted); font-size:12px; text-transform:uppercase; letter-spacing:.08em; font-weight:800; }
    input { width:100%; border:2px solid #d9cfb1; border-radius:14px; padding:13px 14px; font:inherit; font-size:16px; font-weight:800; color:var(--ink); background:#fffaf0; }
    .missing { min-height:48vh; display:grid; place-items:center; text-align:center; }
    .compact { font-size:13px; color:var(--muted); line-height:1.45; }
    .divider { height:1px; background:#ddd4b9; margin:8px 0; }
    .empty { padding:14px; border:1px dashed #c8bfa3; border-radius:16px; color:var(--muted); background:rgba(255,255,255,.7); }
    .meta { display:flex; flex-wrap:wrap; gap:8px; align-items:center; }
    .meta .pill { background:#f0ead7; color:#24311f; }
    @media (max-width:760px) { .grid { grid-template-columns:1fr; } main { padding-bottom:28px; } }
  </style>
</head>
<body>
  <main>
    <section class="hero">
      <h1>Voucher Link</h1>
      <p class="sub">สแกน QR บนตู้เพื่อเปิดหน้านี้ แล้วเลือก voucher จากมือถือเพื่อส่งกลับไปยัง session ของตู้</p>
    </section>

    <section id="missingSession" class="card missing" hidden>
      <div>
        <h1>SESSION MISSING</h1>
        <p class="sub">ไม่พบ session สำหรับตู้ กรุณาสแกน QR จากหน้าตู้ใหม่อีกครั้ง</p>
      </div>
    </section>

    <section id="content" class="grid">
      <div class="stack">
        <div class="card">
          <div class="meta">
            <span id="sessionBadge" class="pill">Session: ...</span>
            <span id="deviceBadge" class="pill">Device: ...</span>
          </div>
          <h3 class="title">Session Status</h3>
          <div id="sessionStatus" class="status">กำลังโหลด session...</div>
          <div id="sessionDetails" class="compact"></div>
          <div class="actions">
            <button id="refreshSessionBtn" class="ghost" type="button">Refresh Session</button>
            <button id="signOutBtn" class="secondary" type="button">Forget Device</button>
          </div>
        </div>

        <div class="card">
          <h3 class="title">Device Claim</h3>
          <div id="deviceStatus" class="status">กำลังตรวจสอบ device...</div>
          <div class="stack">
            <div>
              <label for="deviceName">Device name</label>
              <input id="deviceName" placeholder="PHONE-01">
            </div>
            <div>
              <label for="claimVoucherCode">Optional voucher code to link</label>
              <input id="claimVoucherCode" placeholder="PB-ADF2633C">
            </div>
          </div>
          <div class="actions">
            <button id="claimBtn" class="primary" type="button">Claim Device</button>
            <button id="reloadVouchersBtn" class="ghost" type="button">Reload Vouchers</button>
          </div>
          <div class="compact">ถ้า device นี้ถูกจำไว้แล้ว ระบบจะโหลด token จากเบราว์เซอร์และแสดง voucher ที่ผูกไว้ให้ทันที</div>
        </div>
      </div>

      <div class="stack">
        <div class="card">
          <h3 class="title">Available Vouchers</h3>
          <div id="voucherStatus" class="status">รอโหลด voucher list...</div>
          <div id="voucherList" class="voucher-list"></div>
        </div>
      </div>
    </section>
  </main>
  <script>
    const params = new URLSearchParams(location.search);
    const sessionToken = params.get("session") || "";
    const deviceTokenStorageKey = "noah_kiosk_device_token";
    const deviceNameStorageKey = "noah_kiosk_device_name";
    const deviceTokenHeader = () => localStorage.getItem(deviceTokenStorageKey) || "";

    const sessionBadge = document.getElementById("sessionBadge");
    const deviceBadge = document.getElementById("deviceBadge");
    const sessionStatus = document.getElementById("sessionStatus");
    const sessionDetails = document.getElementById("sessionDetails");
    const deviceStatus = document.getElementById("deviceStatus");
    const voucherStatus = document.getElementById("voucherStatus");
    const voucherList = document.getElementById("voucherList");
    const missingSession = document.getElementById("missingSession");
    const content = document.getElementById("content");
    const deviceNameInput = document.getElementById("deviceName");
    const claimVoucherInput = document.getElementById("claimVoucherCode");
    const claimBtn = document.getElementById("claimBtn");
    const refreshSessionBtn = document.getElementById("refreshSessionBtn");
    const reloadVouchersBtn = document.getElementById("reloadVouchersBtn");
    const signOutBtn = document.getElementById("signOutBtn");

    let currentSession = null;
    let currentDevice = null;
    let currentVouchers = [];
    let sessionTimer = null;
    let voucherTimer = null;
    let attaching = false;

    function setStatus(el, message, kind) {
      el.textContent = message || "";
      el.className = "status" + (kind ? " " + kind : "");
    }

    function authHeaders(extra) {
      const headers = Object.assign({ "Accept": "application/json" }, extra || {});
      const token = deviceTokenHeader();
      if (token) {
        headers.Authorization = "Bearer " + token;
      }
      return headers;
    }

    async function fetchJson(url, options) {
      const response = await fetch(url, Object.assign({ headers: authHeaders((options && options.headers) || {}) }, options || {}));
      const payload = await response.json().catch(() => ({ success:false, error:{ message: response.statusText } }));
      if (!response.ok || payload.success === false) {
        const error = new Error((payload.error && payload.error.message) || response.statusText || "Request failed");
        error.code = payload.error && payload.error.code;
        error.status = response.status;
        throw error;
      }
      return payload.data;
    }

    function formatRemaining(voucher) {
      const remaining = voucher && voucher.voucher_status ? Number(voucher.voucher_status.remaining_uses || 0) : 0;
      return String(Math.max(0, remaining));
    }

    function isUsable(voucher) {
      return Boolean(voucher && voucher.voucher_status && voucher.voucher_status.usable_now);
    }

    function renderVouchers() {
      voucherList.innerHTML = "";
      const usable = currentVouchers.filter(isUsable);
      if (!currentVouchers.length) {
        voucherList.innerHTML = '<div class="empty">ยังไม่มี voucher ที่ผูกไว้กับ device นี้</div>';
        return;
      }

      for (const voucher of currentVouchers) {
        const el = document.createElement("div");
        el.className = "voucher" + (isUsable(voucher) ? " selected" : "");
        const status = voucher.voucher_status || {};
        const canAttach = Boolean(deviceTokenHeader() && currentSession && currentSession.status === "PENDING" && isUsable(voucher) && !attaching);
        el.innerHTML = [
          '<div class="row">',
          '<h4>' + voucher.voucher_code + '</h4>',
          '<small>' + (status.status || 'UNKNOWN') + '</small>',
          '</div>',
          '<small>Remaining: ' + formatRemaining(voucher) + '</small>',
          '<small>Claimed: ' + (voucher.claimed_at || '-') + '</small>',
          '<div class="actions">',
          '<button class="primary" type="button"' + (canAttach ? '' : ' disabled') + '>Attach to session</button>',
          '</div>'
        ].join('');
        const button = el.querySelector("button");
        button.addEventListener("click", function () {
          attachVoucher(voucher.voucher_code);
        });
        voucherList.appendChild(el);
      }

      if (usable.length === 1 && deviceTokenHeader() && !attaching && currentSession && currentSession.status === "PENDING") {
        setTimeout(function () {
          attachVoucher(usable[0].voucher_code);
        }, 250);
      }
    }

    async function loadSession() {
      if (!sessionToken) {
        content.hidden = true;
        missingSession.hidden = false;
        return;
      }

      try {
        const data = await fetchJson("/api/web/v1/kiosk-sessions/" + encodeURIComponent(sessionToken));
        currentSession = data;
        sessionBadge.textContent = "Session: " + sessionToken.slice(0, 8);
        deviceBadge.textContent = "Device: " + (data.mobile_device_name || (currentDevice && currentDevice.device_name) || "pending");
        if (data.status === "PENDING") {
          setStatus(sessionStatus, "Waiting for voucher attachment...", "");
        } else if (data.status === "ATTACHED") {
          setStatus(sessionStatus, "Voucher attached. Waiting for Unity to redeem.", "ok");
        } else if (data.status === "CONSUMED" || data.status === "REDEEMED") {
          setStatus(sessionStatus, "Session completed.", "ok");
        } else if (data.status === "CANCELLED" || data.status === "EXPIRED") {
          setStatus(sessionStatus, "Session closed: " + data.status, "error");
        } else {
          setStatus(sessionStatus, "Session status: " + data.status, "");
        }
        sessionDetails.textContent = "Project: " + (data.project_id || "-") + " | " + (data.voucher_code ? "Voucher: " + data.voucher_code + " | " : "") + "Expires: " + (data.expires_at || "-");
      } catch (error) {
        setStatus(sessionStatus, error.message || "Failed to load session", "error");
      }
    }

    async function loadDevice() {
      const token = deviceTokenHeader();
      if (!token) {
        currentDevice = null;
        deviceBadge.textContent = "Device: unknown";
        setStatus(deviceStatus, "ยังไม่พบ device token ใน browser. กด Claim Device เพื่อเริ่ม pairing.", "");
        return;
      }

      try {
        const data = await fetchJson("/api/web/v1/device/me");
        currentDevice = data.device;
        deviceBadge.textContent = "Device: " + (currentDevice.device_name || "unknown");
        deviceNameInput.value = currentDevice.device_name || localStorage.getItem(deviceNameStorageKey) || "PHONE-01";
        setStatus(deviceStatus, "Device recognized.", "ok");
      } catch (error) {
        currentDevice = null;
        if (error.status === 404) {
          setStatus(deviceStatus, "Device token ไม่ถูกจำใน backend แล้ว กด Claim Device เพื่อ pair ใหม่", "error");
          localStorage.removeItem(deviceTokenStorageKey);
        } else {
          setStatus(deviceStatus, error.message || "Failed to load device", "error");
        }
      }
    }

    async function loadVouchers() {
      const token = deviceTokenHeader();
      if (!token) {
        currentVouchers = [];
        voucherStatus.textContent = "ยังไม่ได้ claim device";
        renderVouchers();
        return;
      }

      try {
        const data = await fetchJson("/api/web/v1/me/vouchers" + (currentSession && currentSession.project_id ? "?project_id=" + encodeURIComponent(currentSession.project_id) : ""));
        currentVouchers = data.vouchers || [];
        voucherStatus.textContent = currentVouchers.length ? "Loaded " + currentVouchers.length + " vouchers" : "No vouchers linked";
        renderVouchers();
      } catch (error) {
        currentVouchers = [];
        voucherStatus.textContent = error.message || "Failed to load vouchers";
        renderVouchers();
      }
    }

    async function claimDevice() {
      const deviceName = ((deviceNameInput.value || localStorage.getItem(deviceNameStorageKey) || "PHONE-01").trim() || "PHONE-01");
      const voucherCode = (claimVoucherInput.value || "").trim();
      claimBtn.disabled = true;
      setStatus(deviceStatus, "Claiming device...", "");
      try {
        const data = await fetchJson("/api/web/v1/device/claim-voucher", {
          method: "POST",
          headers: authHeaders({ "Content-Type": "application/json" }),
          body: JSON.stringify({
            device_name: deviceName,
            project_id: currentSession && currentSession.project_id ? currentSession.project_id : undefined,
            voucher_code: voucherCode || undefined,
            claim_source: "voucher-link-page"
          })
        });
        if (data.device_token) {
          localStorage.setItem(deviceTokenStorageKey, data.device_token);
          localStorage.setItem(deviceNameStorageKey, deviceName);
        }
        setStatus(deviceStatus, "Device claimed and saved on this browser.", "ok");
        claimVoucherInput.value = "";
        await loadDevice();
        await loadVouchers();
      } catch (error) {
        setStatus(deviceStatus, error.message || "Claim failed", "error");
      } finally {
        claimBtn.disabled = false;
      }
    }

    async function attachVoucher(voucherCode) {
      if (attaching) {
        return;
      }
      if (!sessionToken || !currentSession || currentSession.status !== "PENDING") {
        return;
      }
      attaching = true;
      voucherStatus.textContent = "Attaching " + voucherCode + " ...";
      try {
        const data = await fetchJson("/api/web/v1/kiosk-sessions/" + encodeURIComponent(sessionToken) + "/attach-voucher", {
          method: "POST",
          headers: authHeaders({ "Content-Type": "application/json" }),
          body: JSON.stringify({ voucher_code: voucherCode })
        });
        currentSession = data;
        setStatus(sessionStatus, "Voucher attached to session.", "ok");
        await loadSession();
        await loadVouchers();
      } catch (error) {
        setStatus(sessionStatus, error.message || "Attach failed", "error");
        await loadSession();
      } finally {
        attaching = false;
        renderVouchers();
      }
    }

    function stopTimers() {
      if (sessionTimer) {
        clearInterval(sessionTimer);
        sessionTimer = null;
      }
      if (voucherTimer) {
        clearInterval(voucherTimer);
        voucherTimer = null;
      }
    }

    async function bootstrap() {
      if (!sessionToken) {
        content.hidden = true;
        missingSession.hidden = false;
        return;
      }

      missingSession.hidden = true;
      content.hidden = false;
      deviceNameInput.value = localStorage.getItem(deviceNameStorageKey) || "PHONE-01";
      sessionBadge.textContent = "Session: " + sessionToken.slice(0, 8);
      await loadSession();
      await loadDevice();
      await loadVouchers();
      stopTimers();
      sessionTimer = setInterval(loadSession, 1500);
      voucherTimer = setInterval(loadVouchers, 5000);
    }

    claimBtn.addEventListener("click", claimDevice);
    refreshSessionBtn.addEventListener("click", loadSession);
    reloadVouchersBtn.addEventListener("click", loadVouchers);
    signOutBtn.addEventListener("click", function () {
      localStorage.removeItem(deviceTokenStorageKey);
      localStorage.removeItem(deviceNameStorageKey);
      currentDevice = null;
      currentVouchers = [];
      deviceBadge.textContent = "Device: unknown";
      setStatus(deviceStatus, "Device removed from this browser.", "");
      voucherStatus.textContent = "Login again or claim device";
      renderVouchers();
      loadDevice();
    });

    window.addEventListener("pagehide", stopTimers);
    bootstrap();
  </script>
</body>
</html>`;
}

function renderVoucherTestPage() {
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Voucher Test Tool</title>
  <style>
    :root { --bg:#f4f4f1; --panel:#fff; --ink:#17232c; --muted:#65717a; --line:#deddd8; --teal:#14627a; --green:#16704d; --red:#b8323d; --orange:#c95722; --mono:ui-monospace,SFMono-Regular,Menlo,Monaco,Consolas,monospace; }
    * { box-sizing:border-box; }
    body { margin:0; background:var(--bg); color:var(--ink); font-family:Inter,system-ui,-apple-system,BlinkMacSystemFont,"Segoe UI",Arial,sans-serif; }
    header { background:#10212a; color:white; padding:18px 24px; display:flex; justify-content:space-between; gap:16px; align-items:flex-start; }
    h1 { margin:0 0 4px; font-size:22px; }
    header p { margin:0; color:#b7cbd0; font-size:13px; }
    main { padding:22px; max-width:1280px; margin:0 auto; }
    .grid { display:grid; grid-template-columns:1fr 1fr; gap:16px; align-items:start; }
    .panel { background:var(--panel); border:1px solid var(--line); border-radius:14px; padding:16px; }
    .panel h2 { margin:0 0 13px; font-size:16px; }
    .form-grid { display:grid; grid-template-columns:1fr 1fr; gap:10px; }
    .full { grid-column:1/-1; }
    label { display:block; color:var(--muted); font-size:11px; margin-bottom:5px; }
    input, select, textarea { width:100%; border:1px solid #d8d7d2; border-radius:9px; padding:10px; font:inherit; font-size:13px; background:white; color:var(--ink); }
    textarea { min-height:168px; resize:vertical; font-family:var(--mono); }
    button { border:0; border-radius:10px; padding:11px 13px; font:inherit; font-size:13px; font-weight:700; cursor:pointer; background:#e9e9e4; color:var(--ink); }
    button.primary { background:var(--teal); color:white; }
    button.green { background:#e7f5ee; color:var(--green); }
    button.orange { background:#fff0e8; color:var(--orange); }
    .actions { display:flex; flex-wrap:wrap; gap:8px; margin-top:13px; }
    .status { margin-top:10px; min-height:18px; font-size:12px; color:var(--muted); }
    .status.ok { color:var(--green); }
    .status.error { color:var(--red); }
    pre { margin:0; white-space:pre-wrap; word-break:break-word; background:#10212a; color:#dff3f6; padding:14px; border-radius:12px; min-height:180px; font:12px/1.5 var(--mono); }
    code { font-family:var(--mono); color:var(--teal); }
    .hint { color:var(--muted); font-size:12px; line-height:1.5; margin-top:8px; }
    .steps { display:grid; gap:8px; margin-bottom:16px; }
    .step { background:#fff; border:1px solid var(--line); border-left:4px solid var(--teal); border-radius:10px; padding:10px 12px; font-size:13px; color:var(--muted); }
    .step b { color:var(--ink); }
    @media (max-width:900px) { header { display:block; } main { padding:14px; } .grid { grid-template-columns:1fr; } .form-grid { grid-template-columns:1fr; } }
  </style>
</head>
<body>
  <header>
    <div>
      <h1>Voucher Test Tool</h1>
      <p>Generate a voucher and check web-facing voucher status against this backend.</p>
    </div>
    <p>Docs: <a style="color:#b9e6ef" href="/api/docs">/api/docs</a></p>
  </header>
  <main>
    <div class="steps">
      <div class="step"><b>1. Generate</b> creates a code. Save the returned plain code immediately.</div>
      <div class="step"><b>2. Check Status</b> calls <code>/api/web/v1/vouchers/status</code>. It does not require a device token and does not consume quota.</div>
    </div>
    <div class="grid">
      <section class="panel">
        <h2>Auth</h2>
        <div class="form-grid">
          <div class="full">
            <label for="adminToken">Admin bearer token</label>
            <input id="adminToken" type="password" autocomplete="off" placeholder="ADMIN_BEARER_TOKEN">
          </div>
          <div class="full">
            <label for="projectId">Project ID</label>
            <input id="projectId" value="prj_world_tour">
          </div>
        </div>
        <div class="actions">
          <button id="saveAuth">Save auth in this browser</button>
          <button id="clearAuth">Clear</button>
        </div>
        <div class="hint">This page sends requests to the same origin, so use <code>https://api.wajanapir.com/tools/voucher-test</code> on production.</div>
      </section>

      <section class="panel">
        <h2>Latest Result</h2>
        <pre id="output">No request sent yet.</pre>
      </section>

      <section class="panel">
        <h2>1. Generate Voucher</h2>
        <div class="form-grid">
          <div>
            <label for="codeMode">Code mode</label>
            <select id="codeMode"><option>AUTO</option><option>MANUAL</option></select>
          </div>
          <div>
            <label for="codeName">Manual code</label>
            <input id="codeName" value="WORLDTOUR-VIP-001">
          </div>
          <div>
            <label for="benefitType">Benefit type</label>
            <select id="benefitType"><option>FREE_SESSION</option><option>FIXED_DISCOUNT</option><option>PERCENT_DISCOUNT</option><option>FREE_ADDON</option><option>STAFF_TEST</option></select>
          </div>
          <div>
            <label for="quantity">Quantity</label>
            <input id="quantity" type="number" min="1" max="500" value="1">
          </div>
          <div>
            <label for="maxUses">Max uses per code</label>
            <input id="maxUses" type="number" min="1" value="1">
          </div>
          <div>
            <label for="validUntil">Valid until</label>
            <input id="validUntil" type="datetime-local">
          </div>
          <div class="full">
            <label for="campaignName">Campaign name</label>
            <input id="campaignName" value="Voucher Test">
          </div>
          <div class="full">
            <label for="purpose">Purpose</label>
            <input id="purpose" value="Manual QA test">
          </div>
        </div>
        <div class="actions">
          <button class="primary" id="generateBtn">Generate</button>
          <button class="green" id="copyGeneratedBtn">Copy generated code to status check</button>
        </div>
        <div id="generateStatus" class="status"></div>
      </section>

      <section class="panel">
        <h2>2. Check Voucher Status</h2>
        <div class="form-grid">
          <div class="full">
            <label for="voucherCode">Voucher code</label>
            <input id="voucherCode" placeholder="PB-XXXXXXXX">
          </div>
        </div>
        <div class="actions">
          <button class="primary" id="statusBtn">Check Status</button>
        </div>
        <div id="statusResult" class="status"></div>
        <div class="hint">This status check is for frontend websites. Use <code>usable_now</code>, <code>has_been_used</code>, and <code>quota_exhausted</code> from the response.</div>
      </section>
    </div>
  </main>
  <script>
    const storageKey = "photoBoothVoucherTest";
    const fields = ["adminToken", "projectId"];
    const state = { generatedCode: "" };

    function el(id) { return document.getElementById(id); }
    function pretty(value) { return JSON.stringify(value, null, 2); }
    function show(value) { el("output").textContent = typeof value === "string" ? value : pretty(value); }
    function status(id, message, kind) { el(id).textContent = message || ""; el(id).className = "status" + (kind ? " " + kind : ""); }
    function authHeaders(kind) {
      const headers = { "Content-Type": "application/json" };
      const token = kind === "admin" ? el("adminToken").value.trim() : "";
      if (token) headers.Authorization = "Bearer " + token;
      return headers;
    }
    async function post(path, body, kind) {
      const response = await fetch(path, { method: "POST", headers: authHeaders(kind), body: JSON.stringify(body) });
      const json = await response.json().catch(() => ({ success: false, error: { message: response.statusText } }));
      show(json);
      if (!response.ok || json.success === false) throw new Error((json.error && json.error.message) || response.statusText);
      return json.data;
    }
    function loadSaved() {
      try {
        const saved = JSON.parse(localStorage.getItem(storageKey) || "{}");
        fields.forEach((id) => { if (saved[id]) el(id).value = saved[id]; });
      } catch {}
      if (!el("adminToken").value && (location.hostname === "localhost" || location.hostname === "127.0.0.1")) {
        el("adminToken").value = "dev-admin-token";
      }
    }
    function saveAuth() {
      const saved = {};
      fields.forEach((id) => { saved[id] = el(id).value; });
      localStorage.setItem(storageKey, JSON.stringify(saved));
      show("Saved auth fields in this browser.");
    }
    function validUntilIso() {
      const value = el("validUntil").value;
      return value ? new Date(value).toISOString() : null;
    }

    el("saveAuth").addEventListener("click", saveAuth);
    el("clearAuth").addEventListener("click", () => { localStorage.removeItem(storageKey); show("Cleared saved auth fields."); });
    el("generateBtn").addEventListener("click", async () => {
      try {
        status("generateStatus", "Generating...", "");
        const body = {
          project_id: el("projectId").value.trim(),
          campaign_name: el("campaignName").value.trim(),
          purpose: el("purpose").value.trim(),
          code_mode: el("codeMode").value,
          benefit_type: el("benefitType").value,
          quantity: Number(el("quantity").value || 1),
          max_uses_per_code: Number(el("maxUses").value || 1)
        };
        const until = validUntilIso();
        if (until) body.valid_until = until;
        if (body.code_mode === "MANUAL") body.code_name = el("codeName").value.trim();
        const data = await post("/api/admin/v1/vouchers/generate", body, "admin");
        state.generatedCode = data.vouchers && data.vouchers[0] ? data.vouchers[0].code : "";
        if (state.generatedCode) el("voucherCode").value = state.generatedCode;
        status("generateStatus", "Generated. Code copied to status check field.", "ok");
      } catch (error) {
        status("generateStatus", error.message, "error");
      }
    });
    el("copyGeneratedBtn").addEventListener("click", () => {
      if (state.generatedCode) el("voucherCode").value = state.generatedCode;
    });
    el("statusBtn").addEventListener("click", async () => {
      try {
        status("statusResult", "Checking status...", "");
        const data = await post("/api/web/v1/vouchers/status", {
          voucher_code: el("voucherCode").value.trim(),
          project_id: el("projectId").value.trim()
        }, "web");
        status("statusResult", "Status: " + data.status + ". Usable now: " + String(data.usable_now) + ".", data.usable_now ? "ok" : "error");
      } catch (error) {
        status("statusResult", error.message, "error");
      }
    });
    loadSaved();
  </script>
</body>
</html>`;
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

async function resolveDownloadRoutePrefix(queryable, jobId) {
  const result = await queryable.query(
    `SELECT p.result_route_prefix
     FROM booth_jobs j
     JOIN projects p ON p.id = j.project_id
     WHERE j.job_id = $1
     LIMIT 1`,
    [jobId]
  );
  return normalizeDownloadRoutePrefix(result.rows[0]?.result_route_prefix || config.defaultDownloadRoutePrefix);
}

function normalizeDownloadRoutePrefix(value) {
  const normalized = String(value || "").trim().toLowerCase();
  if (normalized === "d" || normalized === "kooky-world") {
    return normalized;
  }
  return "world-tour";
}

function downloadRoutePrefixFromRequest(req) {
  if (req.path.startsWith("/d/")) {
    return "d";
  }
  return req.path.startsWith("/kooky-world/") ? "kooky-world" : "world-tour";
}

function usesWorldTourPresentation(routePrefix) {
  return routePrefix !== "d";
}

function requiredRawCaptureCount(projectId) {
  return projectId === "prj_kooky_world" ? 3 : 4;
}

function requiredRawCaptureTotalForRoute(routePrefix, requestedTotal) {
  const normalizedRequestedTotal = Math.max(1, normalizeInteger(requestedTotal, 1));
  return normalizeDownloadRoutePrefix(routePrefix) === "kooky-world"
    ? Math.max(3, normalizedRequestedTotal)
    : normalizedRequestedTotal;
}

function kookyWorldStickerFileNames(labelTemplateId) {
  const frameId = labelTemplateId === "2" ? "02" : "01";
  return [1, 2, 3].map((slotNumber) => `frame${frameId}_${String(slotNumber).padStart(2, "0")}.png`);
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

  // Unity's aggregate fallback clip uses motion_000.png ... motion_NNN.png.
  // Those names describe one continuous sequence, so divide it into the three
  // capture segments instead of mistaking the frame number for a capture id.
  const hasExplicitCaptureNumbers = sortedFrames.some((frame) => {
    const frameName = frame.original_file_name || frame.remote_key || "";
    return /motion[_-]?\d+[_-]\d+/i.test(frameName);
  });
  if (!hasExplicitCaptureNumbers && sortedFrames.length >= 3) {
    const framesPerCapture = Math.ceil(sortedFrames.length / 3);
    for (let captureIndex = 0; captureIndex < 3; captureIndex += 1) {
      slotFrameUrls[captureIndex] = sortedFrames.slice(
        captureIndex * framesPerCapture,
        Math.min((captureIndex + 1) * framesPerCapture, sortedFrames.length)
      );
    }
    return slotFrameUrls;
  }

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

function renderKookyWorldDownloadPage({ job, composed, thumbnail, liveImage, motionVideo, rawCaptures, motionFrames }) {
  const jobId = job.job_id;
  const safeJobId = escapeHtml(jobId);
  const displayTitle = "MRKREME Kooky World Session";
  const takenAt = escapeHtml(formatDisplayDate(job.session_started_at_utc || job.created_at)).toUpperCase();
  const imageUrl = assetUrl(composed);
  const thumbnailUrl = assetUrl(thumbnail);
  const heroPreviewUrl = imageUrl || thumbnailUrl;
  const liveImageUrl = assetUrl(liveImage);
  const labelTemplateId = resolveLabelTemplateId(job.image_preview_id || job.theme_id);
  const passengerName = normalizePassengerName(job.passenger_name);
  const passengerNameVersion = passengerNameCacheKey(passengerName);
  const labelTemplateUrl = `/assets/kooky-world/ticket_${labelTemplateId}.png`;
  const imageDownloadUrl = `/kooky-world/${encodeURIComponent(jobId)}/image-download`;
  const framedCountdownVideoDownloadUrl = `/kooky-world/${encodeURIComponent(jobId)}/countdown-download.mp4`;
  const motionVideoUrl = motionVideo ? `/kooky-world/${encodeURIComponent(jobId)}/clip.mp4?v=frame-${labelTemplateId}-name-${passengerNameVersion}-v4` : "";
  const motionVideoDownloadUrl = motionVideo ? `/kooky-world/${encodeURIComponent(jobId)}/clip-download.mp4?v=frame-${labelTemplateId}-name-${passengerNameVersion}-v4` : "";
  const rawCaptureUrls = rawCaptures.map(assetUrl).filter(Boolean);
  const hasCountdownPreview = motionFrames.length > 0;
  const framedCountdownVideoUrl = hasCountdownPreview ? `/kooky-world/${encodeURIComponent(jobId)}/framed-countdown.mp4?v=frame-${labelTemplateId}-name-${passengerNameVersion}-v8` : "";
  const rawMotionVideoUrl = assetUrl(motionVideo);

  const passengerNameMarkup = passengerName
    ? `<div class="label-passenger-name">${escapeHtml(passengerName)}</div>`
    : "";
  const photoUrl = rawCaptureUrls[0] || heroPreviewUrl || liveImageUrl;
  const stickerFileNames = kookyWorldStickerFileNames(labelTemplateId);
  const photoMarkup = stickerFileNames.map((stickerFileName, index) => {
    const captureUrl = rawCaptureUrls[index];
    if (!captureUrl) {
      return "";
    }
    const slotNumber = index + 1;
    return `
      <div class="kooky-slot kooky-slot-${slotNumber}">
        <img src="${captureUrl}" alt="Raw capture ${slotNumber}" loading="eager">
        <img class="kooky-sticker" src="/assets/kooky-world/${stickerFileName}" alt="Frame ${labelTemplateId} overlay ${slotNumber}">
      </div>`;
  }).join("");

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

  const hasLiveview = rawCaptureUrls.length > 0;
  const liveviewVideoUrl = `/kooky-world/${encodeURIComponent(jobId)}/liveview.mp4?v=frame-${labelTemplateId}-name-${passengerNameVersion}-v7`;
  const liveviewVideoDownloadUrl = `/kooky-world/${encodeURIComponent(jobId)}/liveview-download.mp4?v=frame-${labelTemplateId}-name-${passengerNameVersion}-v7`;

  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>${displayTitle}</title>
  <style>
    @font-face {
      font-family: "Franie";
      src: url("/assets/kooky-world/Franie-XBold.otf") format("opentype");
      font-weight: 800;
      font-style: normal;
      font-display: swap;
    }
    @font-face {
      font-family: "Franie";
      src: url("/assets/kooky-world/Franie-SBold.otf") format("opentype");
      font-weight: 600;
      font-style: normal;
      font-display: swap;
    }
    @font-face {
      font-family: "Battery Park";
      src: url("/assets/label/BatteryPark.ttf") format("truetype");
      font-display: swap;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      padding: 0;
      min-height: 100vh;
      background-color: #0b0a08;
    }
    .page {
      position: relative;
      width: min(100%, 500px);
      min-height: 100vh;
      margin: 0 auto;
      background: #0b0a08 url("/assets/kooky-world/website_background.png") center top / 100% auto repeat-y;
      padding: 30px 20px 50px;
      display: flex;
      flex-direction: column;
      align-items: center;
      box-shadow: 0 0 40px rgba(0,0,0,0.8);
    }
    .brand-top-logo {
      display: block;
      width: calc(100% + 40px);
      margin: -30px -20px 20px;
      height: auto;
      z-index: 1;
    }
    .hero-section {
      display: flex;
      flex-direction: column;
      align-items: center;
      position: relative;
      width: 100%;
      margin-bottom: 15px;
    }
    .top-character {
      width: 200px;
      height: auto;
      z-index: 2;
    }
    .signature-tape {
      width: 230px;
      height: auto;
      margin-top: -20px;
      z-index: 1;
    }
    .date-time {
      font-family: 'Franie', sans-serif;
      font-weight: 800;
      font-size: 18px;
      color: #fff;
      text-align: left;
      margin: 15px 0 25px;
      letter-spacing: 0.05em;
    }
    .card {
      width: 100%;
      max-width: 360px;
      margin-bottom: 35px;
      display: flex;
      flex-direction: column;
    }
    .card-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      width: 100%;
      margin-bottom: 12px;
      padding: 0 4px;
    }
    .card-title {
      display: flex;
      align-items: center;
    }
    .card-icon {
      height: 24px;
      width: auto;
      display: block;
      object-fit: contain;
    }
    .card-download {
      display: block;
      height: 32px;
      width: 135px;
      transition: transform 0.15s ease-in-out;
    }
    .card-download img {
      width: 100%;
      height: 100%;
      object-fit: contain;
      display: block;
    }
    .card-download:hover {
      transform: scale(1.04);
    }
    .card-body {
      width: 100%;
    }
    .ticket-frame {
      width: 100%;
      box-shadow: 0 16px 24px rgba(0,0,0,0.3);
      overflow: hidden;
      background: transparent;
    }
    .label-stage {
      position: relative;
      width: 100%;
      background: #fff;
      container-type: inline-size;
    }
    .label-stage-1 { aspect-ratio: 12657 / 8445; }
    .label-stage-2 { aspect-ratio: 12640 / 8399; }
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
      width: 100%;
      height: 100%;
      font-size: 14px;
      font-size: 3cqw;
      text-transform: uppercase;
      color: #222;
      background: #eee;
    }
    .kooky-slot {
      position: absolute;
      z-index: 2;
      display: block;
      background: #000;
      overflow: hidden;
    }
    .kooky-slot img {
      width: 100%;
      height: 100%;
      object-fit: cover;
      display: block;
    }
    .kooky-slot-bg {
      background-color: #21201b;
    }
    .kooky-sticker {
      position: absolute;
      top: 0;
      left: 0;
      width: 100%;
      height: 100%;
      object-fit: fill;
      z-index: 3;
      pointer-events: none;
    }

    /* Template 1 Slots */
    .label-stage-1 .kooky-slot-1 {
      left: 7.277%;
      top: 50.409%;
      width: 26.452%;
      height: 37.430%;
    }
    .label-stage-1 .kooky-slot-2 {
      left: 36.636%;
      top: 50.409%;
      width: 26.444%;
      height: 37.430%;
    }
    .label-stage-1 .kooky-slot-3 {
      left: 65.995%;
      top: 50.409%;
      width: 26.444%;
      height: 37.430%;
    }

    /* Template 2 Slots */
    .label-stage-2 .kooky-slot-1 {
      left: 7.476%;
      top: 50.411%;
      width: 26.400%;
      height: 37.516%;
    }
    .label-stage-2 .kooky-slot-2 {
      left: 36.780%;
      top: 50.411%;
      width: 26.400%;
      height: 37.516%;
    }
    .label-stage-2 .kooky-slot-3 {
      left: 66.092%;
      top: 50.411%;
      width: 26.400%;
      height: 37.516%;
    }

    .label-passenger-name {
      position: absolute;
      z-index: 3;
      font-family: "Franie", Impact, "Arial Black", sans-serif;
      font-weight: 600;
      line-height: 1;
      letter-spacing: 0;
      text-transform: uppercase;
      white-space: nowrap;
      pointer-events: none;
    }
    .label-stage-1 .label-passenger-name {
      left: 45.5%;
      top: 20.6%;
      font-size: 0.8cqw;
      color: #231F20;
    }
    .label-stage-2 .label-passenger-name {
      left: 33.0%;
      top: 24.0%;
      font-size: 0.8cqw;
      color: #FFFFFF;
    }
    .video-label-stage {
      position: relative;
      width: 100%;
    }
    .media {
      display: block;
      width: 100%;
      height: auto;
      background: #000;
    }
    .media-empty {
      display: grid;
      place-items: center;
      min-height: 220px;
      color: #080808;
      text-transform: uppercase;
      font-size: 22px;
      text-align: center;
      background: #fff5d4;
      border: 3px solid #080808;
    }
    .train-section {
      width: 100%;
      max-width: 360px;
      display: flex;
      justify-content: center;
      margin: 25px 0 10px;
    }
    .train-image {
      width: 100%;
      height: auto;
    }
    .footer {
      width: 100%;
      max-width: 360px;
      display: flex;
      justify-content: center;
      margin-top: 15px;
    }
    .footer-image {
      width: 100%;
      height: auto;
    }
  </style>
</head>
<body>
  <main class="page">
    <img class="brand-top-logo" src="/assets/kooky-world/Top.png" alt="THE FURRYWAYS">
    <img class="signature-tape" src="/assets/kooky-world/under_the_top.png" alt="Kreme Signature">

    <div class="hero-section">
      <img class="top-character" src="/assets/kooky-world/top_character.png" alt="Kooky Bat">
    </div>

    <div class="date-time">${takenAt}</div>

    <!-- IMAGE CARD -->
    <div class="card">
      <div class="card-header">
        <div class="card-title">
          <img class="card-icon" src="/assets/kooky-world/image_icon.png" alt="IMAGE">
        </div>
        <a href="${imageDownloadUrl}" download class="card-download">
          <img src="/assets/kooky-world/download_button.png" alt="Download">
        </a>
      </div>
      <div class="card-body">
        <div class="ticket-frame">
          <div class="label-stage label-stage-${labelTemplateId}">
            <img class="label-template" src="${labelTemplateUrl}" alt="Furryways frame ${labelTemplateId}">
            ${photoMarkup}
            ${passengerNameMarkup}
          </div>
        </div>
      </div>
    </div>

    <!-- VDO CARD -->
    <div class="card">
      <div class="card-header">
        <div class="card-title">
          <img class="card-icon" src="/assets/kooky-world/vdo_icon.png" alt="VDO">
        </div>
        ${videoDownloadUrl ? `
        <a href="${videoDownloadUrl}" download class="card-download">
          <img src="/assets/kooky-world/download_button.png" alt="Download">
        </a>` : ''}
      </div>
      <div class="card-body">
        <div class="ticket-frame">
          ${videoMarkup || `<div class="media media-empty">Video is processing</div>`}
        </div>
      </div>
    </div>

    <!-- LIVEVIEW CARD -->
    <div class="card">
      <div class="card-header">
        <div class="card-title">
          <img class="card-icon" src="/assets/kooky-world/liveview_icon.png" alt="LIVEVIEW">
        </div>
        ${hasLiveview ? `
        <a href="${liveviewVideoDownloadUrl}" download class="card-download">
          <img src="/assets/kooky-world/download_button.png" alt="Download">
        </a>` : ''}
      </div>
      <div class="card-body">
        <div class="ticket-frame">
          ${hasLiveview ? `
          <video class="media" controls autoplay playsinline loop muted poster="${photoUrl || thumbnailUrl}">
            <source src="${liveviewVideoUrl}" type="video/mp4">
          </video>` : `<div class="media media-empty">Liveview is processing</div>`}
        </div>
      </div>
    </div>

    <div class="train-section">
      <img class="train-image" src="/assets/kooky-world/train_travel.png" alt="Train Travel Line">
    </div>

    <footer class="footer">
      <img class="footer-image" src="/assets/kooky-world/footer.png" alt="Footer Logo and Copyright">
    </footer>
  </main>
</body>
</html>`;
}

function renderWorldTourDownloadPage({ job, composed, thumbnail, liveImage, motionVideo, rawCaptures, motionFrames }, routePrefix = "world-tour") {
  const prefix = normalizeDownloadRoutePrefix(routePrefix);
  if (prefix === "kooky-world") {
    return renderKookyWorldDownloadPage({ job, composed, thumbnail, liveImage, motionVideo, rawCaptures, motionFrames });
  }
  const jobId = job.job_id;
  const safeJobId = escapeHtml(jobId);
  const displayTitle = prefix === "kooky-world" ? "MRKREME Kooky World Session" : "MRKREME World Tour Session";
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

module.exports = {
  buildRotatingFrameSets,
  buildCountdownSlotFrameAssets,
  resolveKookyWorldCountdownFrameDurationSeconds,
  generateMediaOnce,
  kookyWorldStickerFileNames,
  requiredRawCaptureCount,
  requiredRawCaptureTotalForRoute,
  renderKookyWorldFramedPng,
  renderKookyWorldFramedVideo,
  renderKookyWorldDownloadPage,
  resolveLabelTemplateId,
  sortRawCaptureAssets
};
