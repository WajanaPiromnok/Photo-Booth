const http = require("node:http");
const { buildConfig, handlePrintJob } = require("./printBridge");

const config = buildConfig();

const server = http.createServer(async (req, res) => {
  if (req.method === "GET" && req.url === "/healthz") {
    sendJson(res, 200, { ok: true });
    return;
  }

  if (req.method !== "POST" || req.url !== "/api/print/jobs") {
    sendJson(res, 404, {
      success: false,
      retryable: false,
      message: "Not found.",
      printer_name: null,
      operation_id: null
    });
    return;
  }

  try {
    const body = await readJsonBody(req);
    const result = await handlePrintJob(body, config);
    sendJson(res, result.statusCode, result.body);
  } catch (error) {
    sendJson(res, 400, {
      success: false,
      retryable: false,
      message: error.message,
      printer_name: null,
      operation_id: null
    });
  }
});

server.listen(config.port, "127.0.0.1", () => {
  console.log(`Photo Booth PrintBridge listening on http://127.0.0.1:${config.port}`);
});

function readJsonBody(req) {
  return new Promise((resolve, reject) => {
    let body = "";
    req.setEncoding("utf8");
    req.on("data", (chunk) => {
      body += chunk;
      if (body.length > 1024 * 1024) {
        reject(new Error("Request body is too large."));
        req.destroy();
      }
    });
    req.on("end", () => {
      try {
        resolve(body ? JSON.parse(body) : {});
      } catch {
        reject(new Error("Request body must be valid JSON."));
      }
    });
    req.on("error", reject);
  });
}

function sendJson(res, statusCode, body) {
  res.writeHead(statusCode, {
    "Content-Type": "application/json",
    "Cache-Control": "no-store"
  });
  res.end(JSON.stringify(body));
}
