const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const {
  buildCountdownSlotFrameAssets,
  buildRotatingFrameSets,
  generateMediaOnce,
  kookyWorldStickerFileNames,
  requiredRawCaptureCount,
  requiredRawCaptureTotalForRoute,
  renderKookyWorldDownloadPage,
  resolveLabelTemplateId,
  sortRawCaptureAssets
} = require("../src/server");

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

test("aggregate Unity motion frames split into three capture segments", () => {
  const frames = Array.from({ length: 9 }, (_, index) => ({
    original_file_name: `motion_${String(index).padStart(3, "0")}.png`,
    remote_key: `motion_${String(index).padStart(3, "0")}.png`
  }));
  const groups = buildCountdownSlotFrameAssets(frames);
  assert.deepEqual(groups.slice(0, 3).map((group) => group.map((frame) => frame.original_file_name)), [
    ["motion_000.png", "motion_001.png", "motion_002.png"],
    ["motion_003.png", "motion_004.png", "motion_005.png"],
    ["motion_006.png", "motion_007.png", "motion_008.png"]
  ]);
});

test("concurrent media requests share one generation", async () => {
  const outputPath = path.join("/tmp", `kooky-media-lock-${process.pid}.mp4`);
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
    assert.match(html, /liveview\.mp4\?v=frame-[12]-name-[a-f0-9]+-v5/);

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
