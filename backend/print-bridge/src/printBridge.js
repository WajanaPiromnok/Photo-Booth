const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const { execFile } = require("node:child_process");

function buildConfig(env = process.env) {
  const defaultPrinterName = normalizeOptional(env.DEFAULT_PRINTER_NAME);
  const allowedPrinterNames = parseAllowedPrinters(env.ALLOWED_PRINTER_NAMES, defaultPrinterName);
  const platform = normalizeOptional(env.PRINT_PLATFORM) || process.platform;
  return {
    port: Number.parseInt(env.PORT || "18080", 10),
    defaultPrinterName,
    allowedPrinterNames,
    overrideRequestedPrinter: parseBoolean(env.OVERRIDE_REQUESTED_PRINTER, true),
    printOptions: parsePrintOptions(env.PRINT_OPTIONS),
    printCommand: normalizeOptional(env.PRINT_COMMAND) || defaultPrintCommand(platform),
    printPlatform: platform,
    dryRun: parseBoolean(env.PRINT_BRIDGE_DRY_RUN, false),
    openPreview: parseBoolean(env.PRINT_BRIDGE_OPEN_PREVIEW, false)
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

  const imageMetadata = readImageMetadata(imagePath);
  console.log("print_job_requested", {
    jobId,
    imagePath,
    fileName: path.basename(imagePath),
    requestedPrinterName,
    printerName,
    copies,
    image: imageMetadata
  });
  try {
    if (config.dryRun) {
      if (config.openPreview) {
        await openPreviewImage(imagePath, config.printPlatform, deps.execFile || execFilePromise);
      }

      console.log("print_job_previewed", { jobId, imagePath, printerName, openPreview: config.openPreview });
      return {
        statusCode: 200,
        body: {
          success: true,
          retryable: false,
          message: "Print preview generated. Dry-run mode did not send anything to the printer.",
          printer_name: printerName,
          operation_id: crypto.randomUUID()
        }
      };
    }

    for (let copy = 0; copy < copies; copy += 1) {
      await runPrintCommand(config.printCommand, buildPrintArgs(printerName, 1, imagePath, config.printOptions, config.printPlatform));
    }
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

async function openPreviewImage(imagePath, platform = process.platform, runCommand = execFilePromise) {
  if (isWindowsPlatform(platform)) {
    await runCommand("powershell.exe", ["-NoProfile", "-Command", "Start-Process -LiteralPath $args[0]", imagePath]);
    return;
  }

  if (String(platform || "").toLowerCase() === "darwin") {
    await runCommand("open", [imagePath]);
    return;
  }

  await runCommand("xdg-open", [imagePath]);
}

function buildPrintArgs(printerName, copies, imagePath, printOptions = defaultPrintOptions(), platform = process.platform) {
  if (isWindowsPlatform(platform)) {
    return [
      "-NoProfile",
      "-ExecutionPolicy",
      "Bypass",
      "-File",
      path.join(__dirname, "print-image.ps1"),
      "-ImagePath",
      imagePath,
      "-PrinterName",
      printerName,
      "-Copies",
      String(copies)
    ];
  }

  const args = ["-d", printerName, "-n", String(copies)];
  for (const option of printOptions) {
    args.push("-o", option);
  }

  args.push(imagePath);
  return args;
}

function defaultPrintCommand(platform = process.platform) {
  return isWindowsPlatform(platform) ? "powershell.exe" : "lp";
}

function isWindowsPlatform(platform) {
  return String(platform || "").toLowerCase().startsWith("win");
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

function readImageMetadata(imagePath) {
  try {
    const stat = fs.statSync(imagePath);
    const buffer = fs.readFileSync(imagePath);
    const dimensions = readImageDimensions(buffer);
    return {
      bytes: stat.size,
      width: dimensions?.width || null,
      height: dimensions?.height || null,
      format: dimensions?.format || path.extname(imagePath).replace(".", "").toLowerCase() || null
    };
  } catch (error) {
    return {
      error: error?.message || "Could not read image metadata."
    };
  }
}

function readImageDimensions(buffer) {
  if (!Buffer.isBuffer(buffer) || buffer.length < 10) {
    return null;
  }

  if (buffer[0] === 0x89
    && buffer[1] === 0x50
    && buffer[2] === 0x4e
    && buffer[3] === 0x47
    && buffer.length >= 24) {
    return {
      format: "png",
      width: buffer.readUInt32BE(16),
      height: buffer.readUInt32BE(20)
    };
  }

  if (buffer[0] === 0xff && buffer[1] === 0xd8) {
    let offset = 2;
    while (offset + 9 < buffer.length) {
      if (buffer[offset] !== 0xff) {
        offset += 1;
        continue;
      }

      const marker = buffer[offset + 1];
      if (marker === 0xd9 || marker === 0xda) {
        break;
      }

      const segmentLength = buffer.readUInt16BE(offset + 2);
      if (segmentLength < 2 || offset + 2 + segmentLength > buffer.length) {
        break;
      }

      if ((marker >= 0xc0 && marker <= 0xc3)
        || (marker >= 0xc5 && marker <= 0xc7)
        || (marker >= 0xc9 && marker <= 0xcb)
        || (marker >= 0xcd && marker <= 0xcf)) {
        return {
          format: "jpg",
          height: buffer.readUInt16BE(offset + 5),
          width: buffer.readUInt16BE(offset + 7)
        };
      }

      offset += 2 + segmentLength;
    }
  }

  return null;
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
  openPreviewImage,
  parseAllowedPrinters
};
