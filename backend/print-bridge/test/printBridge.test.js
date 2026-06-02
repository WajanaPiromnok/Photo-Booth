const assert = require("node:assert/strict");
const test = require("node:test");
const { buildPrintArgs, handlePrintJob } = require("../src/printBridge");

const config = {
  defaultPrinterName: "XP-420B",
  allowedPrinterNames: new Set(["XP-420B"]),
  overrideRequestedPrinter: true,
  printOptions: ["PageSize=w4h6", "Darkness=13"],
  printCommand: "lp"
};

test("rejects missing image path", async () => {
  const result = await handlePrintJob({ job_id: "JOB-1", printer_name: "XP-420B" }, config);

  assert.equal(result.statusCode, 400);
  assert.equal(result.body.success, false);
  assert.equal(result.body.retryable, false);
});

test("rejects printer outside allowlist", async () => {
  const configWithoutOverride = {
    ...config,
    overrideRequestedPrinter: false
  };
  const result = await handlePrintJob(
    { job_id: "JOB-1", image_path: "/tmp/final.png", printer_name: "Other Printer" },
    configWithoutOverride,
    { fileExists: () => true }
  );

  assert.equal(result.statusCode, 403);
  assert.equal(result.body.success, false);
  assert.equal(result.body.retryable, false);
});

test("overrides requested printer with local default printer", async () => {
  let receivedArgs = null;
  const result = await handlePrintJob(
    { job_id: "JOB-1", image_path: "/tmp/final.png", printer_name: "Other Printer", copies: 1 },
    config,
    {
      fileExists: () => true,
      execFile: async (_command, args) => {
        receivedArgs = args;
      }
    }
  );

  assert.equal(result.statusCode, 200);
  assert.equal(result.body.printer_name, "XP-420B");
  assert.deepEqual(receivedArgs, ["-d", "XP-420B", "-n", "1", "-o", "PageSize=w4h6", "-o", "Darkness=13", "/tmp/final.png"]);
});

test("returns retryable failure when OS print command fails", async () => {
  const result = await handlePrintJob(
    { job_id: "JOB-1", image_path: "/tmp/final.png", printer_name: "XP-420B", copies: 2 },
    config,
    {
      fileExists: () => true,
      execFile: async () => {
        throw new Error("printer offline");
      }
    }
  );

  assert.equal(result.statusCode, 502);
  assert.equal(result.body.success, false);
  assert.equal(result.body.retryable, true);
  assert.equal(result.body.message, "printer offline");
});

test("returns success when OS print command succeeds", async () => {
  let receivedCommand = null;
  let receivedArgs = null;
  const result = await handlePrintJob(
    { job_id: "JOB-1", image_path: "/tmp/final.png", printer_name: "XP-420B", copies: 2 },
    config,
    {
      fileExists: () => true,
      execFile: async (command, args) => {
        receivedCommand = command;
        receivedArgs = args;
      }
    }
  );

  assert.equal(result.statusCode, 200);
  assert.equal(result.body.success, true);
  assert.equal(result.body.retryable, false);
  assert.equal(result.body.printer_name, "XP-420B");
  assert.match(result.body.operation_id, /^[0-9a-f-]{36}$/);
  assert.equal(receivedCommand, "lp");
  assert.deepEqual(receivedArgs, ["-d", "XP-420B", "-n", "2", "-o", "PageSize=w4h6", "-o", "Darkness=13", "/tmp/final.png"]);
});

test("buildPrintArgs includes kiosk driver defaults", () => {
  const args = buildPrintArgs("XP-420B", 1, "/tmp/final.png");

  assert.deepEqual(args, [
    "-d",
    "XP-420B",
    "-n",
    "1",
    "-o",
    "PageSize=w4h6",
    "-o",
    "orientation-requested=3",
    "-o",
    "fit-to-page",
    "-o",
    "MediaMethod=Normal",
    "-o",
    "PaperType=LabelGaps",
    "-o",
    "GapsHeight=3",
    "-o",
    "PostAction=TearOff",
    "-o",
    "Occurrence=Every",
    "-o",
    "Brightness=0",
    "-o",
    "HalftoneType=Stucki",
    "-o",
    "Origin=Default",
    "-o",
    "MirrorImage=False",
    "-o",
    "NegativeImage=False",
    "-o",
    "PrintSpeed=2",
    "-o",
    "Darkness=13",
    "/tmp/final.png"
  ]);
});
