// Essai E7 — serveur statique local pour boot.html (copié de essais/e17-wine-dedie/server.mjs).
// - En-têtes COOP/COEP, Range, compteur d'octets servis (GET /__stats, POST /__reset).
// - PUT /__state/<nom> : dépose un fichier dans images/ (par morceaux : état sauvegardé, disque exporté).
// - Repli en lecture seule : /lib/* et /images/* absents ici sont cherchés dans essais/e17-wine-dedie/
//   puis dans essais/e6-reseau/images/ (image Alpine nue pour le tableau « sans agent »). Jamais modifiés.
// Usage : node server.mjs [port]   (défaut 8140)
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const FALLBACKS = [
  { prefix: "/lib/", dir: path.join(ROOT, "..", "e17-wine-dedie", "lib") },
  { prefix: "/images/", dir: path.join(ROOT, "..", "e17-wine-dedie", "images") },
  { prefix: "/images/", dir: path.join(ROOT, "..", "e6-reseau", "images") },
];
const PORT = Number(process.argv[2] || process.env.PORT || 8140);
const MIME = { ".html": "text/html; charset=utf-8", ".js": "text/javascript", ".mjs": "text/javascript", ".wasm": "application/wasm", ".json": "application/json", ".bin": "application/octet-stream", ".zst": "application/octet-stream", ".img": "application/octet-stream", ".png": "image/png" };
const stats = { requests: 0, bytes: 0, byExt: {} };

function send(res, status, body, headers = {}) { res.writeHead(status, { "Content-Type": "text/plain", ...headers }); res.end(body); }
function readBody(req) { return new Promise((resolve, reject) => { const chunks = []; req.on("data", (c) => chunks.push(c)); req.on("end", () => resolve(Buffer.concat(chunks))); req.on("error", reject); }); }

async function handleControl(req, res, url) {
  if (url.pathname === "/__stats") return send(res, 200, JSON.stringify(stats), { "Content-Type": "application/json", "Cache-Control": "no-store" });
  if (url.pathname === "/__reset") { stats.requests = 0; stats.bytes = 0; stats.byExt = {}; return send(res, 200, "ok"); }
  if (url.pathname.startsWith("/__state/") && req.method === "PUT") {
    const name = path.basename(url.pathname);
    if (!/^[\w.-]+$/.test(name)) return send(res, 400, "bad name");
    const body = await readBody(req);
    const offset = Number(url.searchParams.get("offset") || 0);
    const target = path.join(ROOT, "images", name);
    if (offset === 0) fs.writeFileSync(target, body); else fs.appendFileSync(target, body);
    return send(res, 200, String(fs.statSync(target).size));
  }
  return send(res, 404, "not found");
}

function serveFile(req, res, filePath, stat) {
  const ext = path.extname(filePath).toLowerCase();
  const noStore = /state[^/\\]*\.bin$/.test(filePath) || ext === ".html" || ext === ".img";
  const headers = { "Content-Type": MIME[ext] || "application/octet-stream", "Accept-Ranges": "bytes", "Cross-Origin-Opener-Policy": "same-origin", "Cross-Origin-Embedder-Policy": "require-corp", "Cross-Origin-Resource-Policy": "same-origin", "Cache-Control": noStore ? "no-store" : "max-age=3600" };
  let start = 0, end = stat.size - 1, status = 200;
  const range = req.headers.range;
  if (range) {
    const m = /^bytes=(\d*)-(\d*)$/.exec(range);
    if (m) {
      if (m[1]) start = Number(m[1]);
      if (m[2]) end = Number(m[2]);
      if (!m[1] && m[2]) { start = Math.max(0, stat.size - Number(m[2])); end = stat.size - 1; }
      end = Math.min(end, stat.size - 1); status = 206; headers["Content-Range"] = `bytes ${start}-${end}/${stat.size}`;
    }
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

function resolve(rel) {
  const local = path.normalize(path.join(ROOT, rel));
  if (!local.startsWith(ROOT)) return null;
  try { const s = fs.statSync(local); if (s.isFile()) return { filePath: local, stat: s }; } catch {}
  for (const f of FALLBACKS) {
    if (!rel.startsWith(f.prefix)) continue;
    const alt = path.normalize(path.join(f.dir, rel.slice(f.prefix.length)));
    if (!alt.startsWith(f.dir)) continue;
    try { const s = fs.statSync(alt); if (s.isFile()) return { filePath: alt, stat: s }; } catch {}
  }
  return null;
}

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, `http://localhost:${PORT}`);
    if (url.pathname.startsWith("/__")) return await handleControl(req, res, url);
    const rel = decodeURIComponent(url.pathname === "/" ? "/boot.html" : url.pathname);
    const r = resolve(rel);
    if (!r) return send(res, 404, "not found: " + rel);
    serveFile(req, res, r.filePath, r.stat);
  } catch (e) { console.error("server", e.message); if (!res.headersSent) send(res, 500, String(e)); else res.destroy(); }
});
server.listen(PORT, "127.0.0.1", () => console.log(`E7 server: http://localhost:${PORT}/boot.html`));
