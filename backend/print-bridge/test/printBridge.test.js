const assert = require("node:assert/strict");
const test = require("node:test");
const { buildPrintArgs, handlePrintJob } = require("../src/printBridge");

const config = {
  defaultPrinterName: "XP-420B",
  allowedPrinterNames: new Set(["XP-420B"]),
  overrideRequestedPrinter: true,
  printOptions: ["PageSize=w6h4", "Darkness=13"],
  printCommand: "lp",
  printPlatform: "darwin"
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
  assert.deepEqual(receivedArgs, ["-d", "XP-420B", "-n", "1", "-o", "PageSize=w6h4", "-o", "Darkness=13", "/tmp/final.png"]);
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
  assert.deepEqual(receivedArgs, ["-d", "XP-420B", "-n", "1", "-o", "PageSize=w6h4", "-o", "Darkness=13", "/tmp/final.png"]);
});

test("submits one OS print command per requested copy", async () => {
  let commandCount = 0;
  const result = await handlePrintJob(
    { job_id: "JOB-1", image_path: "/tmp/final.png", printer_name: "XP-420B", copies: 3 },
    config,
    {
      fileExists: () => true,
      execFile: async () => {
        commandCount += 1;
      }
    }
  );

  assert.equal(result.statusCode, 200);
  assert.equal(commandCount, 3);
});

test("dry run returns success without sending print command", async () => {
  let commandCount = 0;
  const result = await handlePrintJob(
    { job_id: "JOB-1", image_path: "/tmp/final.png", printer_name: "XP-420B", copies: 3 },
    { ...config, dryRun: true, openPreview: false },
    {
      fileExists: () => true,
      execFile: async () => {
        commandCount += 1;
      }
    }
  );

  assert.equal(result.statusCode, 200);
  assert.equal(result.body.success, true);
  assert.match(result.body.message, /Dry-run mode/);
  assert.equal(commandCount, 0);
});

test("dry run can open generated image preview", async () => {
  let receivedCommand = null;
  let receivedArgs = null;
  const result = await handlePrintJob(
    { job_id: "JOB-1", image_path: "C:\\photos\\print.jpg", printer_name: "XP-420B" },
    { ...config, dryRun: true, openPreview: true, printPlatform: "win32" },
    {
      fileExists: () => true,
      execFile: async (command, args) => {
        receivedCommand = command;
        receivedArgs = args;
      }
    }
  );

  assert.equal(result.statusCode, 200);
  assert.equal(receivedCommand, "powershell.exe");
  assert.deepEqual(receivedArgs, ["-NoProfile", "-Command", "Start-Process -LiteralPath $args[0]", "C:\\photos\\print.jpg"]);
});

test("buildPrintArgs uses Windows PowerShell image print arguments", () => {
  const args = buildPrintArgs("DS-RX1 4x6 Cut", 1, "C:\\photos\\final.jpg", [], "win32");

  assert.equal(args[0], "-NoProfile");
  assert.equal(args[1], "-ExecutionPolicy");
  assert.equal(args[2], "Bypass");
  assert.equal(args[3], "-File");
  assert.match(args[4], /print-image\.ps1$/);
  assert.deepEqual(args.slice(5), [
    "-ImagePath",
    "C:\\photos\\final.jpg",
    "-PrinterName",
    "DS-RX1 4x6 Cut",
    "-Copies",
    "1",
    "-PaperWidthHundredths",
    "600",
    "-PaperHeightHundredths",
    "400",
    "-PaperName",
    "(6x4)",
    "-Landscape"
  ]);
});

test("buildPrintArgs includes kiosk driver defaults", () => {
  const args = buildPrintArgs("XP-420B", 1, "/tmp/final.png", undefined, "darwin");

  assert.deepEqual(args, [
    "-d",
    "XP-420B",
    "-n",
    "1",
    "-o",
    "PageSize=w6h4",
    "-o",
    "orientation-requested=4",
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
