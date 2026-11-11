// Essai E14 — serveur statique local (dérivé d'E17) : COOP/COEP, Range, compteur d'octets, PUT /__state/<nom>.
// Usage : node server.mjs [port]   (défaut 8160 ; E14 utilise 8160 à 8179)
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const PORT = Number(process.argv[2] || process.env.PORT || 8160);
const MIME = { ".html": "text/html; charset=utf-8", ".js": "text/javascript", ".mjs": "text/javascript", ".wasm": "application/wasm", ".json": "application/json", ".bin": "application/octet-stream", ".zst": "application/octet-stream", ".img": "application/octet-stream", ".png": "image/png" };
const stats = { requests: 0, bytes: 0 };

function send(res, status, body, headers = {}) { res.writeHead(status, { "Content-Type": "text/plain", ...headers }); res.end(body); }
function readBody(req) { return new Promise((resolve, reject) => { const c = []; req.on("data", (d) => c.push(d)); req.on("end", () => resolve(Buffer.concat(c))); req.on("error", reject); }); }

async function handleControl(req, res, url) {
  if (url.pathname === "/__stats") return send(res, 200, JSON.stringify(stats), { "Content-Type": "application/json", "Cache-Control": "no-store" });
  if (url.pathname === "/__reset") { stats.requests = 0; stats.bytes = 0; return send(res, 200, "ok"); }
  if (url.pathname.startsWith("/__state/") && req.method === "PUT") {
    const name = path.basename(url.pathname);
    if (!/^[\w.-]+$/.test(name)) return send(res, 400, "bad name");
    const body = await readBody(req);
    const target = path.join(ROOT, "images", name);
    if (Number(url.searchParams.get("offset") || 0) === 0) fs.writeFileSync(target, body); else fs.appendFileSync(target, body);
    return send(res, 200, String(fs.statSync(target).size));
  }
  return send(res, 404, "not found");
}

function serveFile(req, res, filePath, stat) {
  const ext = path.extname(filePath).toLowerCase();
  const headers = {
    "Content-Type": MIME[ext] || "application/octet-stream", "Accept-Ranges": "bytes",
    "Cross-Origin-Opener-Policy": "same-origin", "Cross-Origin-Embedder-Policy": "require-corp", "Cross-Origin-Resource-Policy": "same-origin",
    "Cache-Control": ext === ".html" || ext === ".js" || /\.bin$/.test(filePath) ? "no-store" : "max-age=3600",
  };
  let start = 0, end = stat.size - 1, status = 200;
  const m = /^bytes=(\d*)-(\d*)$/.exec(req.headers.range || "");
  if (m) {
    if (m[1]) start = Number(m[1]);
    if (m[2]) end = Number(m[2]);
    if (!m[1] && m[2]) { start = Math.max(0, stat.size - Number(m[2])); end = stat.size - 1; }
    end = Math.min(end, stat.size - 1); status = 206; headers["Content-Range"] = `bytes ${start}-${end}/${stat.size}`;
  }
  const length = end - start + 1;
  headers["Content-Length"] = length; stats.requests++; stats.bytes += length;
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
    const rel = decodeURIComponent(url.pathname === "/" ? "/terminal.html" : url.pathname);
    const local = path.normalize(path.join(ROOT, rel));
    if (!local.startsWith(ROOT)) return send(res, 403, "forbidden");
    let st; try { st = fs.statSync(local); } catch { return send(res, 404, "not found: " + rel); }
    if (!st.isFile()) return send(res, 404, "not found: " + rel);
    serveFile(req, res, local, st);
  } catch (e) { console.error("server", e.message); if (!res.headersSent) send(res, 500, String(e)); else res.destroy(); }
});
server.listen(PORT, "127.0.0.1", () => console.log(`E14 server: http://localhost:${PORT}/terminal.html`));
