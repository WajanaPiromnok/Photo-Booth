const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const sharp = require("sharp");

const testTempRoot = path.join(process.cwd(), "tmp-tests");

const {
  buildPreparedDownloadResponse,
  buildCountdownSlotFrameAssets,
  cleanupLocalUploadCacheWithPolicy,
  ensureKookyWorldOutputTemplate,
  buildRotatingFrameSets,
  generateMediaOnce,
  kookyWorldStickerFileNames,
  resolveKookyWorldCountdownFrameDurationSeconds,
  requiredRawCaptureCount,
  requiredRawCaptureTotalForRoute,
  renderKookyWorldDownloadPage,
  renderLegacyDownloadPage,
  renderPrintQuotaAdminPage,
  resolveLabelTemplateId,
  sortRawCaptureAssets,
  uploadAssetBaseKey,
  isGeneratedCacheFile
} = require("../src/server");

const gib = 1024 * 1024 * 1024;

function writeFileWithAge(root, relativePath, ageHours, nowMs) {
  const filePath = path.join(root, relativePath);
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, relativePath);
  const mtime = new Date(nowMs - ageHours * 60 * 60 * 1000);
  fs.utimesSync(filePath, mtime, mtime);
  return filePath;
}

test("Kooky World output template is generated at 1800x1200", async () => {
  const tempRoot = path.join(testTempRoot, `kooky-output-template-${process.pid}-${Date.now()}`);
  const sessionFolder = `session-folder-${process.pid}-${Date.now()}`;
  const generatedRoot = path.join(process.cwd(), "uploads", "jobs", sessionFolder);
  const sourcePath = path.join(tempRoot, "ticket_2.png");
  fs.mkdirSync(tempRoot, { recursive: true });

  try {
    await sharp({
      create: {
        width: 1800,
        height: 1200,
        channels: 4,
        background: "#1b1f18"
      }
    }).png().toFile(sourcePath);

    const outputPath = await ensureKookyWorldOutputTemplate(
      { job_id: "JOB-TEMPLATE-001", session_folder: sessionFolder, passenger_name: "NOAH" },
      "2",
      sourcePath,
      "test"
    );
    const metadata = await sharp(outputPath).metadata();
    assert.equal(metadata.width, 1800);
    assert.equal(metadata.height, 1200);
  } finally {
    fs.rmSync(tempRoot, { recursive: true, force: true });
    fs.rmSync(generatedRoot, { recursive: true, force: true });
  }
});

test("Kooky World output template regenerates unreadable cache files", async () => {
  const tempRoot = path.join(testTempRoot, `kooky-output-template-invalid-${process.pid}-${Date.now()}`);
  const sessionFolder = `session-folder-invalid-${process.pid}-${Date.now()}`;
  const generatedRoot = path.join(process.cwd(), "uploads", "jobs", sessionFolder);
  const sourcePath = path.join(tempRoot, "ticket_2.png");
  fs.mkdirSync(tempRoot, { recursive: true });

  try {
    await sharp({
      create: {
        width: 1800,
        height: 1200,
        channels: 4,
        background: "#1b1f18"
      }
    }).png().toFile(sourcePath);

    const job = { job_id: "JOB-TEMPLATE-INVALID", session_folder: sessionFolder, passenger_name: "NOAH" };
    const outputPath = await ensureKookyWorldOutputTemplate(job, "2", sourcePath, "test-invalid");
    fs.writeFileSync(outputPath, "not a png");

    const regeneratedPath = await ensureKookyWorldOutputTemplate(job, "2", sourcePath, "test-invalid");
    const metadata = await sharp(regeneratedPath).metadata();
    assert.equal(regeneratedPath, outputPath);
    assert.equal(metadata.width, 1800);
    assert.equal(metadata.height, 1200);
  } finally {
    fs.rmSync(tempRoot, { recursive: true, force: true });
    fs.rmSync(generatedRoot, { recursive: true, force: true });
  }
});

test("livephoto rotates 123, 231, 312 for two rounds", () => {
  assert.deepEqual(buildRotatingFrameSets(["1", "2", "3"], 2), [
    ["1", "2", "3"],
    ["2", "3", "1"],
    ["3", "1", "2"],
    ["1", "2", "3"],
    ["2", "3", "1"],
    ["3", "1", "2"]
  ]);
});

test("new uploads are grouped under project route prefix", () => {
  assert.equal(uploadAssetBaseKey("20260629_JOB-001", "kooky-world"), "kooky-world/jobs/20260629_JOB-001");
  assert.equal(uploadAssetBaseKey("20260629_JOB-002", "world-tour"), "world-tour/jobs/20260629_JOB-002");
  assert.equal(uploadAssetBaseKey("20260629_JOB-003", "d"), "jobs/20260629_JOB-003");
});

test("local cache cleanup skips when disk free space is above threshold", () => {
  const tempRoot = path.join(testTempRoot, `cache-skip-${process.pid}-${Date.now()}`);
  const nowMs = Date.now();
  const oldRaw = writeFileWithAge(tempRoot, "kooky-world/jobs/job/raw/capture_01.jpg", 48, nowMs);

  try {
    const result = cleanupLocalUploadCacheWithPolicy({
      uploadsRoot: tempRoot,
      getFreeBytes: () => 20 * gib,
      minFreeBytes: 15 * gib,
      aggressiveFreeBytes: 10 * gib,
      criticalFreeBytes: 5 * gib,
      ttlHours: 24,
      aggressiveTtlHours: 6,
      nowMs
    });

    assert.equal(result.skipped, true);
    assert.equal(fs.existsSync(oldRaw), true);
  } finally {
    fs.rmSync(tempRoot, { recursive: true, force: true });
  }
});

test("local cache cleanup removes old uploads below threshold and only generated cache in aggressive mode", () => {
  const tempRoot = path.join(testTempRoot, `cache-aggressive-${process.pid}-${Date.now()}`);
  const nowMs = Date.now();
  const oldRaw = writeFileWithAge(tempRoot, "kooky-world/jobs/job/raw/capture_01.jpg", 48, nowMs);
  const recentRaw = writeFileWithAge(tempRoot, "kooky-world/jobs/job/raw/capture_02.jpg", 7, nowMs);
  const generatedFrame = writeFileWithAge(tempRoot, "jobs/job/generated/frame_001.png", 7, nowMs);
  const generatedVideo = writeFileWithAge(tempRoot, "jobs/job/generated/liveview_kooky-world_v15.mp4", 7, nowMs);
  const freeReadings = [9 * gib, 9 * gib, 12 * gib];

  try {
    const result = cleanupLocalUploadCacheWithPolicy({
      uploadsRoot: tempRoot,
      getFreeBytes: () => freeReadings.shift(),
      minFreeBytes: 15 * gib,
      aggressiveFreeBytes: 10 * gib,
      criticalFreeBytes: 5 * gib,
      ttlHours: 24,
      aggressiveTtlHours: 6,
      nowMs
    });

    assert.equal(result.skipped, false);
    assert.equal(result.critical, false);
    assert.equal(fs.existsSync(oldRaw), false);
    assert.equal(fs.existsSync(recentRaw), true);
    assert.equal(fs.existsSync(generatedFrame), false);
    assert.equal(fs.existsSync(generatedVideo), false);
    assert.equal(result.standard.deletedFiles, 1);
    assert.equal(result.aggressive.deletedFiles, 2);
  } finally {
    fs.rmSync(tempRoot, { recursive: true, force: true });
  }
});

test("generated cache detection stays narrow for aggressive cleanup", () => {
  assert.equal(isGeneratedCacheFile(path.join("jobs", "abc", "generated", "anything.tmp")), true);
  assert.equal(isGeneratedCacheFile(path.join("jobs", "abc", "generated", "frame_001.png")), true);
  assert.equal(isGeneratedCacheFile(path.join("kooky-world", "jobs", "abc", "raw", "capture_01.jpg")), false);
});

test("aggregate Unity motion frames split into three capture segments", () => {
  const frames = Array.from({ length: 9 }, (_, index) => ({
    original_file_name: `motion_${String(index).padStart(3, "0")}.jpg`,
    remote_key: `motion_${String(index).padStart(3, "0")}.jpg`
  }));
  const groups = buildCountdownSlotFrameAssets(frames);
  assert.deepEqual(groups.slice(0, 3).map((group) => group.map((frame) => frame.original_file_name)), [
    ["motion_000.jpg", "motion_001.jpg", "motion_002.jpg"],
    ["motion_003.jpg", "motion_004.jpg", "motion_005.jpg"],
    ["motion_006.jpg", "motion_007.jpg", "motion_008.jpg"]
  ]);
});

test("Kooky countdown video infers frame duration from uploaded motion frame count", () => {
  const frames = Array.from({ length: 360 }, (_, index) => ({
    original_file_name: `motion_${String(index).padStart(3, "0")}.png`,
    remote_key: `motion_${String(index).padStart(3, "0")}.png`
  }));

  assert.equal(resolveKookyWorldCountdownFrameDurationSeconds(frames), 1 / 24);
});

test("Kooky countdown video keeps old 4fps motion uploads at real speed", () => {
  const frames = Array.from({ length: 60 }, (_, index) => ({
    original_file_name: `motion_${String(index).padStart(3, "0")}.png`,
    remote_key: `motion_${String(index).padStart(3, "0")}.png`
  }));

  assert.equal(resolveKookyWorldCountdownFrameDurationSeconds(frames), 0.25);
});

test("concurrent media requests share one generation", async () => {
  fs.mkdirSync(testTempRoot, { recursive: true });
  const outputPath = path.join(testTempRoot, `kooky-media-lock-${process.pid}.mp4`);
  if (fs.existsSync(outputPath)) {
    fs.unlinkSync(outputPath);
  }
  let generationCount = 0;
  const generator = async () => {
    generationCount += 1;
    await new Promise((resolve) => setTimeout(resolve, 10));
    fs.writeFileSync(outputPath, "media");
  };

  const results = await Promise.all([
    generateMediaOnce(outputPath, generator),
    generateMediaOnce(outputPath, generator)
  ]);

  assert.equal(generationCount, 1);
  assert.deepEqual(results, [outputPath, outputPath]);
  fs.unlinkSync(outputPath);
});

test("Kooky World waits for three raw captures", () => {
  assert.equal(requiredRawCaptureCount("prj_kooky_world"), 3);
  assert.equal(requiredRawCaptureCount("prj_world_tour"), 4);
  assert.equal(requiredRawCaptureTotalForRoute("kooky-world", 2), 3);
  assert.equal(requiredRawCaptureTotalForRoute("kooky-world", 3), 3);
  assert.equal(requiredRawCaptureTotalForRoute("world-tour", 2), 2);
});

test("prepared download response returns scan-ready download and QR URLs", () => {
  const response = buildPreparedDownloadResponse(null, "JOB-READY-001", "kooky-world", "session-folder");
  assert.equal(response.job_id, "JOB-READY-001");
  assert.equal(response.route_prefix, "kooky-world");
  assert.equal(response.session_folder, "session-folder");
  assert.ok(response.download_url.endsWith("/kooky-world/JOB-READY-001"));
  assert.ok(response.qr_png_url.endsWith("/kooky-world/JOB-READY-001/qr"));
});

test("print quota admin page includes status and reset actions", () => {
  const html = renderPrintQuotaAdminPage();
  assert.match(html, /Print Quota/);
  assert.ok(html.includes("/api/admin/v1/print-quota/status"));
  assert.ok(html.includes("/api/admin/v1/print-quota/reset"));
  assert.match(html, /Reset to 0/);
});

test("download pages show processing placeholders before assets are uploaded", () => {
  const job = {
    job_id: "JOB-PROCESSING-001",
    image_preview_id: "image_preview_1",
    passenger_name: "QA",
    created_at: "2026-06-24T00:00:00.000Z"
  };
  const pageArgs = {
    job,
    composed: null,
    thumbnail: null,
    liveImage: null,
    motionVideo: null,
    rawCaptures: [],
    motionFrames: []
  };

  assert.match(renderLegacyDownloadPage(pageArgs), /Processing please wait/);
  const kookyHtml = renderKookyWorldDownloadPage(pageArgs);
  assert.match(kookyHtml, /Processing please wait/);
  assert.match(kookyHtml, /data-kooky-vdo-processing="true"/);
  assert.match(kookyHtml, /setInterval\(checkVdoReady, 3500\)/);
});

test("Kooky World page polls countdown status before generated mp4 is ready", () => {
  const sessionFolder = `kooky-countdown-pending-${process.pid}-${Date.now()}`;
  const generatedRoot = path.join(process.cwd(), "uploads", "jobs", sessionFolder);
  const job = {
    job_id: "JOB-COUNTDOWN-PENDING",
    session_folder: sessionFolder,
    image_preview_id: "image_preview_1",
    passenger_name: "QA",
    created_at: "2026-06-24T00:00:00.000Z"
  };

  try {
    const html = renderKookyWorldDownloadPage({
      job,
      composed: null,
      thumbnail: null,
      liveImage: null,
      motionVideo: null,
      rawCaptures: [
        { remote_key: "raw/capture_01.jpg", original_file_name: "capture_01.jpg" },
        { remote_key: "raw/capture_02.jpg", original_file_name: "capture_02.jpg" },
        { remote_key: "raw/capture_03.jpg", original_file_name: "capture_03.jpg" }
      ],
      motionFrames: [
        { remote_key: "motion/motion_000.jpg", original_file_name: "motion_000.jpg" }
      ]
    });

    assert.match(html, /data-kooky-vdo-processing="true"/);
    assert.match(html, /framed-countdown-status/);
    assert.doesNotMatch(html, /<source src="\/kooky-world\/JOB-COUNTDOWN-PENDING\/framed-countdown\.mp4/);
  } finally {
    fs.rmSync(generatedRoot, { recursive: true, force: true });
  }
});

test("raw captures are sorted by capture number", () => {
  const assets = [
    { original_file_name: "capture_03.png", remote_key: "raw/capture_03.png" },
    { original_file_name: "capture_01.png", remote_key: "raw/capture_01.png" },
    { original_file_name: "capture_02.png", remote_key: "raw/capture_02.png" }
  ];

  assert.deepEqual(
    sortRawCaptureAssets(assets).map((asset) => asset.original_file_name),
    ["capture_01.png", "capture_02.png", "capture_03.png"]
  );
});

test("frame 1 maps left, middle, and right overlays in order", () => {
  assert.deepEqual(kookyWorldStickerFileNames("1"), [
    "frame01_01.png",
    "frame01_02.png",
    "frame01_03.png"
  ]);
});

test("frame 2 maps left, middle, and right overlays in order", () => {
  assert.deepEqual(kookyWorldStickerFileNames("2"), [
    "frame02_01.png",
    "frame02_02.png",
    "frame02_03.png"
  ]);
  assert.equal(resolveLabelTemplateId("image_preview_2"), "2");
  assert.equal(resolveLabelTemplateId("image_preview_1"), "1");
});

test("all mapped overlays exist and the download page pairs them with captures in order", () => {
  for (const frameId of ["1", "2"]) {
    const overlayNames = kookyWorldStickerFileNames(frameId);
    for (const overlayName of overlayNames) {
      assert.equal(
        fs.existsSync(path.join(__dirname, "..", "public", "kooky-world", overlayName)),
        true,
        `${overlayName} must exist`
      );
    }

    const html = renderKookyWorldDownloadPage({
      job: {
        job_id: `JOB-FRAME-${frameId}`,
        image_preview_id: `image_preview_${frameId}`,
        passenger_name: "QA",
        created_at: "2026-06-24T00:00:00.000Z"
      },
      composed: null,
      thumbnail: null,
      liveImage: null,
      motionVideo: null,
      rawCaptures: [1, 2, 3].map((captureNumber) => ({
        remote_key: `raw/capture_${String(captureNumber).padStart(2, "0")}.png`
      })),
      motionFrames: []
    });
    assert.match(html, /liveview\.mp4\?v=frame-[12]-name-[a-f0-9]+-v\d+/);
    assert.match(html, /font-family: "Franie", Impact/);
    assert.match(html, /font-weight: 600;/);
    assert.match(html, /\.label-stage-1 \.label-passenger-name \{[\s\S]*?left: 45\.5%;[\s\S]*?top: 19\.6%;[\s\S]*?font-size: 0\.8cqw;[\s\S]*?color: #231F20;/);
    assert.match(html, /\.label-stage-2 \.label-passenger-name \{[\s\S]*?left: 33\.0%;[\s\S]*?top: 25\.25%;[\s\S]*?font-size: 0\.8cqw;[\s\S]*?color: #FFFFFF;/);

    let previousCaptureOffset = -1;
    for (let index = 0; index < 3; index += 1) {
      const slotNumber = index + 1;
      const captureOffset = html.indexOf(`raw/capture_${String(slotNumber).padStart(2, "0")}.png`);
      const overlayOffset = html.indexOf(overlayNames[index]);
      assert.ok(captureOffset > previousCaptureOffset, `capture ${slotNumber} must keep its order`);
      assert.ok(overlayOffset > captureOffset, `${overlayNames[index]} must overlay capture ${slotNumber}`);
      previousCaptureOffset = captureOffset;
    }
  }
});

test("backend Kooky fonts are exact copies of the Unity source fonts", () => {
  const projectRoot = path.join(__dirname, "..", "..", "..");
  for (const fontName of ["Franie-SBold.otf", "Franie-XBold.otf"]) {
    const unityFont = fs.readFileSync(path.join(projectRoot, "Assets", "UI", "Kooky", "Fonts", fontName));
    const backendFont = fs.readFileSync(path.join(projectRoot, "backend", "api", "public", "kooky-world", fontName));
    assert.deepEqual(backendFont, unityFont, `${fontName} must match the Unity source font`);
  }
});
