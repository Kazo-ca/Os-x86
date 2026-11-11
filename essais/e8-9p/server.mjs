// Essai E8 — serveur statique local pour e8.html (dérivé du server.mjs de E1).
// - /lib/*            -> ../e1-wine-v86/lib (libv86.js, v86.wasm, BIOS), lecture seule
// - /images/alpine-*  -> ../e1-wine-v86/images (alpine-fs.json + alpine-rootfs-flat/), lecture seule
// - /images/disk.img  -> ./images/disk.img (ext4 256/512 Mio, servi avec Range pour hda async)
// - /guest/*, /e8.html -> ./ (scripts invités, page)
// - GET /__stats, POST /__reset : compteur d'octets servis.
// Usage : node server.mjs [port]   (défaut 8098)
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const E1 = path.resolve(ROOT, "..", "e1-wine-v86");
const PORT = Number(process.argv[2] || process.env.PORT || 8098);
const MIME = {
  ".html": "text/html; charset=utf-8", ".js": "text/javascript", ".mjs": "text/javascript",
  ".wasm": "application/wasm", ".json": "application/json", ".bin": "application/octet-stream",
  ".zst": "application/octet-stream", ".img": "application/octet-stream", ".sh": "text/plain",
};
const stats = { requests: 0, bytes: 0, byExt: {} };

function send(res, status, body, headers = {}) {
  res.writeHead(status, { "Content-Type": "text/plain", ...headers });
  res.end(body);
}

function resolve(rel) {
  // Aiguillage : E1 est servi en lecture seule, jamais modifié.
  if (rel.startsWith("/lib/")) return path.normalize(path.join(E1, rel));
  if (rel.startsWith("/images/alpine-")) return path.normalize(path.join(E1, rel));
  return path.normalize(path.join(ROOT, rel));
}

function serveFile(req, res, filePath, stat) {
  const ext = path.extname(filePath).toLowerCase();
  const headers = {
    "Content-Type": MIME[ext] || "application/octet-stream",
    "Accept-Ranges": "bytes",
    "Cross-Origin-Opener-Policy": "same-origin",
    "Cross-Origin-Embedder-Policy": "require-corp",
    "Cross-Origin-Resource-Policy": "same-origin",
    "Cache-Control": [".html", ".sh", ".mjs"].includes(ext) ? "no-store" : "max-age=3600",
  };
  let start = 0, end = stat.size - 1, status = 200;
  const m = req.headers.range && /^bytes=(\d*)-(\d*)$/.exec(req.headers.range);
  if (m) {
    if (m[1]) start = Number(m[1]);
    if (m[2]) end = Number(m[2]);
    if (!m[1] && m[2]) { start = Math.max(0, stat.size - Number(m[2])); end = stat.size - 1; }
    end = Math.min(end, stat.size - 1);
    status = 206;
    headers["Content-Range"] = `bytes ${start}-${end}/${stat.size}`;
  }
  const length = end - start + 1;
  headers["Content-Length"] = length;
  stats.requests++; stats.bytes += length; stats.byExt[ext] = (stats.byExt[ext] || 0) + length;
  res.writeHead(status, headers);
  if (req.method === "HEAD" || length <= 0) return res.end();
  const stream = fs.createReadStream(filePath, { start, end });
  stream.on("error", (e) => { console.error("stream", filePath, e.message); res.destroy(); });
  stream.pipe(res);
}

http.createServer((req, res) => {
  try {
    const url = new URL(req.url, `http://localhost:${PORT}`);
    if (url.pathname === "/__stats") return send(res, 200, JSON.stringify(stats), { "Content-Type": "application/json", "Cache-Control": "no-store" });
    if (url.pathname === "/__reset") { stats.requests = 0; stats.bytes = 0; stats.byExt = {}; return send(res, 200, "ok"); }
    const rel = decodeURIComponent(url.pathname === "/" ? "/e8.html" : url.pathname);
    const filePath = resolve(rel);
    if (!filePath.startsWith(ROOT) && !filePath.startsWith(E1)) return send(res, 403, "forbidden");
    let stat;
    try { stat = fs.statSync(filePath); } catch { return send(res, 404, "not found: " + rel); }
    if (!stat.isFile()) return send(res, 404, "not a file");
    serveFile(req, res, filePath, stat);
  } catch (e) {
    console.error("server", e.message);
    if (!res.headersSent) send(res, 500, String(e)); else res.destroy();
  }
}).listen(PORT, "127.0.0.1", () => console.log(`E8 server: http://localhost:${PORT}/e8.html`));
