const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const sharp = require("sharp");

const testTempRoot = path.join(process.cwd(), "tmp-tests");
const projectRoot = path.join(__dirname, "..", "..", "..");

const {
  ensureWorldTourNamedTemplate,
  ensureWorldTourPhotoImage,
  chooseFeaturedAsset,
  chooseFeaturedComposedAsset,
  featuredComposedRecencySeconds,
  featuredLabelProjectId,
  featuredComposedImagePath,
  isFeaturedLabelJobEligible,
  projectIdForAssetRoute,
  resolveWorldTourTemplate
} = require("../src/server");

function uniqueTestPaths(label) {
  const suffix = `${process.pid}-${Date.now()}-${Math.random().toString(16).slice(2)}`;
  const tempRoot = path.join(testTempRoot, `${label}-${suffix}`);
  const sessionFolder = `${label}-session-${suffix}`;
  const generatedRoot = path.join(process.cwd(), "uploads", "jobs", sessionFolder);
  fs.mkdirSync(tempRoot, { recursive: true });
  return { tempRoot, sessionFolder, generatedRoot };
}

function cleanupTestPaths(paths) {
  fs.rmSync(paths.tempRoot, { recursive: true, force: true });
  fs.rmSync(paths.generatedRoot, { recursive: true, force: true });
}

test("featured composed image keeps a stable rendered URL", () => {
  assert.equal(
    featuredComposedImagePath("JOB-20260803-142909-7d280c75"),
    "/v1/assets/composed/rendered/JOB-20260803-142909-7d280c75"
  );
});

test("featured labels select normal main jobs", () => {
  assert.equal(featuredLabelProjectId, "prj_main");
  assert.equal(featuredComposedRecencySeconds, 90);
  assert.equal(isFeaturedLabelJobEligible({ project_id: "prj_main", upload_status: "LINK_READY" }), true);
  assert.equal(isFeaturedLabelJobEligible({ project_id: "prj_world_tour", upload_status: "LINK_READY" }), false);
  assert.equal(isFeaturedLabelJobEligible({ project_id: "prj_main", upload_status: "PENDING" }), false);
});

test("featured composed selection checks the newest job strictly before falling back to random", () => {
  const selectionFolder = `tmp-tests/featured-strict-selection-${process.pid}-${Date.now()}`;
  const selectionRoot = path.join(process.cwd(), "uploads", selectionFolder);
  const latestComposedKey = `${selectionFolder}/latest-composed.jpg`;
  const latestRawKey = `${selectionFolder}/latest-raw.jpg`;
  const randomKey = `${selectionFolder}/random-composed.jpg`;
  fs.mkdirSync(selectionRoot, { recursive: true });
  fs.writeFileSync(path.join(selectionRoot, "latest-composed.jpg"), "latest");
  fs.writeFileSync(path.join(selectionRoot, "latest-raw.jpg"), "raw");
  fs.writeFileSync(path.join(selectionRoot, "random-composed.jpg"), "random");

  try {
    const latestReady = {
      job_id: "JOB-LATEST",
      project_id: "prj_main",
      upload_status: "LINK_READY",
      asset_type: "composed",
      remote_key: latestComposedKey,
      raw_remote_key: latestRawKey
    };
    const latestPending = { ...latestReady, upload_status: "UPLOADED" };
    const randomAsset = { job_id: "JOB-RANDOM", remote_key: randomKey, asset_type: "composed" };

    assert.equal(chooseFeaturedComposedAsset(latestReady, randomAsset).job_id, "JOB-LATEST");
    assert.equal(chooseFeaturedComposedAsset(latestReady, randomAsset).selection_mode, "latest");
    assert.equal(chooseFeaturedComposedAsset(latestPending, randomAsset).job_id, "JOB-RANDOM");
    assert.equal(chooseFeaturedComposedAsset(latestPending, randomAsset).selection_mode, "random");
    assert.equal(chooseFeaturedComposedAsset(latestPending, null), null);
  } finally {
    fs.rmSync(selectionRoot, { recursive: true, force: true });
  }
});

test("featured selection prefers the newest recent photo and otherwise uses the random candidate list", () => {
  const selectionFolder = `tmp-tests/featured-selection-${process.pid}-${Date.now()}`;
  const recentKey = `${selectionFolder}/recent.jpg`;
  const randomKey = `${selectionFolder}/random.jpg`;
  const selectionRoot = path.join(process.cwd(), "uploads", selectionFolder);
  const recentPath = path.join(selectionRoot, "recent.jpg");
  const randomPath = path.join(selectionRoot, "random.jpg");
  fs.mkdirSync(selectionRoot, { recursive: true });
  fs.writeFileSync(recentPath, "recent");
  fs.writeFileSync(randomPath, "random");
  try {
    const recent = { job_id: "JOB-RECENT", remote_key: recentKey };
    const random = { job_id: "JOB-RANDOM", remote_key: randomKey };

    assert.equal(chooseFeaturedAsset([recent], [random]).selection_mode, "latest");
    assert.equal(chooseFeaturedAsset([], [random]).selection_mode, "random");
  } finally {
    fs.rmSync(selectionRoot, { recursive: true, force: true });
  }
});

test("project-specific World Tour featured routes keep their existing project mapping", () => {
  assert.equal(projectIdForAssetRoute("world-tour"), "prj_world_tour");
});

test("World Tour templates use the anti-alias-safe white openings", () => {
  const frame1 = resolveWorldTourTemplate("1");
  const frame2 = resolveWorldTourTemplate("2");

  assert.deepEqual(
    { width: frame1.width, height: frame1.height, slot: frame1.slot },
    { width: 2136, height: 3132, slot: { x: 72, y: 1766, width: 1993, height: 1137 } }
  );
  assert.deepEqual(
    { width: frame2.width, height: frame2.height, slot: frame2.slot },
    { width: 2138, height: 3134, slot: { x: 114, y: 621, width: 1925, height: 1086 } }
  );
});

test("backend label templates and BatteryPark are exact copies of Unity assets", () => {
  const pairs = [
    ["Assets/UI/Label/1.png", "backend/api/public/label/frame-1.png"],
    ["Assets/UI/Label/2.png", "backend/api/public/label/frame-2.png"],
    ["Assets/UI/Font/BatteryPark.ttf", "backend/api/public/label/BatteryPark.ttf"]
  ];

  for (const [unityPath, backendPath] of pairs) {
    assert.deepEqual(
      fs.readFileSync(path.join(projectRoot, unityPath)),
      fs.readFileSync(path.join(projectRoot, backendPath)),
      `${backendPath} must match ${unityPath}`
    );
  }
});

test("BatteryPark passenger name is drawn into both World Tour templates", async () => {
  for (const templateId of ["1", "2"]) {
    const paths = uniqueTestPaths(`featured-name-${templateId}`);
    try {
      const template = resolveWorldTourTemplate(templateId);
      const outputPath = await ensureWorldTourNamedTemplate(
        {
          job_id: `JOB-FEATURED-NAME-${templateId}`,
          session_folder: paths.sessionFolder,
          passenger_name: "NOAH"
        },
        templateId,
        template.path,
        template.fromName
      );
      const region = {
        left: template.fromName.x,
        top: template.fromName.y,
        width: 900,
        height: 150
      };
      const [before, after] = await Promise.all([
        sharp(template.path).extract(region).raw().toBuffer(),
        sharp(outputPath).extract(region).raw().toBuffer()
      ]);
      assert.notDeepEqual(after, before, `frame ${templateId} must contain rendered passenger text`);
    } finally {
      cleanupTestPaths(paths);
    }
  }
});

test("World Tour featured renderer fills each white photo slot without resizing the label", async () => {
  for (const templateId of ["1", "2"]) {
    const paths = uniqueTestPaths(`featured-render-${templateId}`);
    const sourcePath = path.join(paths.tempRoot, "source.png");
    try {
      await sharp({
        create: {
          width: 1600,
          height: 900,
          channels: 3,
          background: { r: 226, g: 72, b: 42 }
        }
      }).png().toFile(sourcePath);

      const outputPath = await ensureWorldTourPhotoImage(
        {
          job_id: `JOB-FEATURED-RENDER-${templateId}`,
          session_folder: paths.sessionFolder,
          image_preview_id: `image_preview_${templateId}`,
          passenger_name: "NOAH"
        },
        sourcePath
      );
      const template = resolveWorldTourTemplate(templateId);
      const metadata = await sharp(outputPath).metadata();
      assert.equal(metadata.width, template.width);
      assert.equal(metadata.height, template.height);

      const centerPixel = await sharp(outputPath)
        .extract({
          left: template.slot.x + Math.floor(template.slot.width / 2),
          top: template.slot.y + Math.floor(template.slot.height / 2),
          width: 1,
          height: 1
        })
        .removeAlpha()
        .raw()
        .toBuffer();
      assert.ok(centerPixel[0] > 190, `frame ${templateId} photo red channel must be visible`);
      assert.ok(centerPixel[1] < 110, `frame ${templateId} photo green channel must be visible`);
      assert.ok(centerPixel[2] < 90, `frame ${templateId} photo blue channel must be visible`);
    } finally {
      cleanupTestPaths(paths);
    }
  }
});
