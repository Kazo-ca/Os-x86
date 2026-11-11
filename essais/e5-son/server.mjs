// Essai E5 — serveur statique local (copie de essais/e1-wine-v86/server.mjs, port 8733).
// - En-têtes COOP/COEP (isolation cross-origin : SharedArrayBuffer et
//   performance.measureUserAgentSpecificMemory()).
// - Support des requêtes Range (chargement async des images disque).
// - Compteur d'octets servis (GET /__stats, POST /__reset) pour mesurer le téléchargement.
// - PUT /__state/<nom> : dépose un état sauvegardé dans images/ (pour la mesure "restore après rechargement").
// Usage : node server.mjs [port]   (défaut 8086)
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const PORT = Number(process.argv[2] || process.env.PORT || 8733);
const MIME = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript",
  ".mjs": "text/javascript",
  ".wasm": "application/wasm",
  ".json": "application/json",
  ".bin": "application/octet-stream",
  ".wav": "audio/wav",
  ".zst": "application/octet-stream",
};

const stats = { requests: 0, bytes: 0, byExt: {} };

function send(res, status, body, headers = {}) {
  res.writeHead(status, { "Content-Type": "text/plain", ...headers });
  res.end(body);
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on("data", (c) => chunks.push(c));
    req.on("end", () => resolve(Buffer.concat(chunks)));
    req.on("error", reject);
  });
}

async function handleControl(req, res, url) {
  if (url.pathname === "/__stats") return send(res, 200, JSON.stringify(stats), { "Content-Type": "application/json", "Cache-Control": "no-store" });
  if (url.pathname === "/__reset") { stats.requests = 0; stats.bytes = 0; stats.byExt = {}; return send(res, 200, "ok"); }
  if (url.pathname.startsWith("/__state/") && req.method === "PUT") {
    const name = path.basename(url.pathname);
    if (!/^[\w.-]+$/.test(name)) return send(res, 400, "bad name");
    const body = await readBody(req);
    const offset = Number(url.searchParams.get("offset") || 0);
    const target = path.join(ROOT, "images", name);
    if (offset === 0) fs.writeFileSync(target, body);
    else fs.appendFileSync(target, body);
    return send(res, 200, String(fs.statSync(target).size));
  }
  return send(res, 404, "not found");
}

function serveFile(req, res, filePath, stat) {
  const ext = path.extname(filePath).toLowerCase();
  const type = MIME[ext] || "application/octet-stream";
  const noStore = filePath.includes(`${path.sep}images${path.sep}state`);
  const headers = {
    "Content-Type": type,
    "Accept-Ranges": "bytes",
    "Cross-Origin-Opener-Policy": "same-origin",
    "Cross-Origin-Embedder-Policy": "require-corp",
    "Cross-Origin-Resource-Policy": "same-origin",
    "Cache-Control": noStore ? "no-store" : "max-age=3600",
  };
  let start = 0;
  let end = stat.size - 1;
  let status = 200;
  const range = req.headers.range;
  if (range) {
    const m = /^bytes=(\d*)-(\d*)$/.exec(range);
    if (m) {
      if (m[1]) start = Number(m[1]);
      if (m[2]) end = Number(m[2]);
      if (!m[1] && m[2]) { start = Math.max(0, stat.size - Number(m[2])); end = stat.size - 1; }
      end = Math.min(end, stat.size - 1);
      status = 206;
      headers["Content-Range"] = `bytes ${start}-${end}/${stat.size}`;
    }
  }
  const length = end - start + 1;
  headers["Content-Length"] = length;
  stats.requests++;
  stats.bytes += length;
  stats.byExt[ext] = (stats.byExt[ext] || 0) + length;
  res.writeHead(status, headers);
  if (req.method === "HEAD" || length <= 0) return res.end();
  const stream = fs.createReadStream(filePath, { start, end });
  stream.on("error", (e) => { console.error("stream", filePath, e.message); res.destroy(); });
  stream.pipe(res);
}

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, `http://localhost:${PORT}`);
    if (url.pathname.startsWith("/__")) return await handleControl(req, res, url);
    const rel = decodeURIComponent(url.pathname === "/" ? "/boot.html" : url.pathname);
    const filePath = path.normalize(path.join(ROOT, rel));
    if (!filePath.startsWith(ROOT)) return send(res, 403, "forbidden");
    let stat;
    try { stat = fs.statSync(filePath); } catch { return send(res, 404, "not found: " + rel); }
    if (!stat.isFile()) return send(res, 404, "not a file");
    serveFile(req, res, filePath, stat);
  } catch (e) {
    console.error("server", e.message);
    if (!res.headersSent) send(res, 500, String(e)); else res.destroy();
  }
});

server.listen(PORT, "127.0.0.1", () => {
  console.log(`E5 server: http://localhost:${PORT}/boot.html`);
});
