const crypto = require("node:crypto");
const fs = require("node:fs");
const { execFile } = require("node:child_process");

function buildConfig(env = process.env) {
  const defaultPrinterName = normalizeOptional(env.DEFAULT_PRINTER_NAME);
  const allowedPrinterNames = parseAllowedPrinters(env.ALLOWED_PRINTER_NAMES, defaultPrinterName);
  return {
    port: Number.parseInt(env.PORT || "18080", 10),
    defaultPrinterName,
    allowedPrinterNames,
    overrideRequestedPrinter: parseBoolean(env.OVERRIDE_REQUESTED_PRINTER, true),
    printOptions: parsePrintOptions(env.PRINT_OPTIONS),
    printCommand: normalizeOptional(env.PRINT_COMMAND) || "lp"
  };
}

async function handlePrintJob(body, config = buildConfig(), deps = {}) {
  const fileExists = deps.fileExists || fs.existsSync;
  const runPrintCommand = deps.execFile || execFilePromise;

  const jobId = normalizeOptional(body?.job_id);
  const imagePath = normalizeOptional(body?.image_path);
  const requestedPrinterName = normalizeOptional(body?.printer_name);
  const printerName = config.overrideRequestedPrinter ? config.defaultPrinterName : requestedPrinterName || config.defaultPrinterName;
  const copies = normalizeCopies(body?.copies);

  if (!jobId) {
    return errorResponse(400, false, "job_id is required.", printerName);
  }

  if (!imagePath) {
    return errorResponse(400, false, "image_path is required.", printerName);
  }

  if (!fileExists(imagePath)) {
    return errorResponse(404, false, "Image file was not found.", printerName);
  }

  if (!printerName) {
    return errorResponse(400, false, "DEFAULT_PRINTER_NAME is required.", printerName);
  }

  if (!isPrinterAllowed(printerName, config.allowedPrinterNames)) {
    return errorResponse(403, false, "Printer is not allowed by this bridge.", printerName);
  }

  console.log("print_job_requested", { jobId, imagePath, requestedPrinterName, printerName, copies });
  try {
    await runPrintCommand(config.printCommand, buildPrintArgs(printerName, copies, imagePath, config.printOptions));
    console.log("print_job_submitted", { jobId, printerName, copies });
    return {
      statusCode: 200,
      body: {
        success: true,
        retryable: false,
        message: "Print job submitted.",
        printer_name: printerName,
        operation_id: crypto.randomUUID()
      }
    };
  } catch (error) {
    console.error("print_job_failed", { jobId, printerName, message: error?.message || "OS print command failed." });
    return {
      statusCode: 502,
      body: {
        success: false,
        retryable: true,
        message: error?.message || "OS print command failed.",
        printer_name: printerName,
        operation_id: null
      }
    };
  }
}

function execFilePromise(command, args) {
  return new Promise((resolve, reject) => {
    execFile(command, args, { timeout: 30000 }, (error, stdout, stderr) => {
      if (error) {
        const message = stderr || stdout || error.message;
        reject(new Error(message.trim()));
        return;
      }

      resolve({ stdout, stderr });
    });
  });
}

function buildPrintArgs(printerName, copies, imagePath, printOptions = defaultPrintOptions()) {
  const args = ["-d", printerName, "-n", String(copies)];
  for (const option of printOptions) {
    args.push("-o", option);
  }

  args.push(imagePath);
  return args;
}

function defaultPrintOptions() {
  return [
    "PageSize=w4h6",
    "orientation-requested=3",
    "fit-to-page",
    "MediaMethod=Normal",
    "PaperType=LabelGaps",
    "GapsHeight=3",
    "PostAction=TearOff",
    "Occurrence=Every",
    "Brightness=0",
    "HalftoneType=Stucki",
    "Origin=Default",
    "MirrorImage=False",
    "NegativeImage=False",
    "PrintSpeed=2",
    "Darkness=13"
  ];
}

function parsePrintOptions(value) {
  if (!normalizeOptional(value)) {
    return defaultPrintOptions();
  }

  return String(value)
    .split(",")
    .map((option) => option.trim())
    .filter(Boolean);
}

function parseAllowedPrinters(value, defaultPrinterName) {
  const names = String(value || "")
    .split(",")
    .map((name) => name.trim())
    .filter(Boolean);

  if (names.length === 0 && defaultPrinterName) {
    names.push(defaultPrinterName);
  }

  return new Set(names);
}

function isPrinterAllowed(printerName, allowedPrinterNames) {
  return allowedPrinterNames.size === 0 || allowedPrinterNames.has(printerName);
}

function normalizeCopies(value) {
  const copies = Number.parseInt(value, 10);
  return Number.isFinite(copies) && copies > 0 ? copies : 1;
}

function normalizeOptional(value) {
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

function parseBoolean(value, defaultValue) {
  if (value === undefined || value === null || String(value).trim() === "") {
    return defaultValue;
  }

  return !["0", "false", "no", "off"].includes(String(value).trim().toLowerCase());
}

function errorResponse(statusCode, retryable, message, printerName) {
  return {
    statusCode,
    body: {
      success: false,
      retryable,
      message,
      printer_name: printerName || null,
      operation_id: null
    }
  };
}

module.exports = {
  buildPrintArgs,
  buildConfig,
  handlePrintJob,
  parseAllowedPrinters
};
