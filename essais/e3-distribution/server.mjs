// Essai E3 — serveur statique local (code jetable). Sert :
//   - les fichiers du dossier (dl.html, dl-worker.js) et dotnet/publish/<variante>/wwwroot/ sous /dotnet/<variante>/ ;
//   - /images/<fichier> avec CORS, Accept-Ranges, 206, ETag, If-None-Match (304), Cache-Control réglable ;
//   - /ctl?... pour piloter les pannes : cut=<Mio> (fermer la connexion après N Mio) & cuts=<n> (nombre de fois),
//     skip=<n> (laisser passer n réponses avant de couper), fail503=<n> (503 aux n prochains GET d'image), rate=<Mbit/s> (limite de débit, 0 = aucune),
//     coi=0|1 (COOP/COEP), cc=<Cache-Control pour /images>, reset=1 (compteurs et journal) ;
//   - /stats : journal des requêtes d'images (chemin, Range, statut, octets envoyés, If-None-Match).
// Usage : node server.mjs [--port 8098] [--coi 1]   (ou import { startServer })

import http from 'node:http';
import { createReadStream } from 'node:fs';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const MiB = 1024 * 1024;
const TYPES = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8',
  '.json': 'application/json', '.wasm': 'application/wasm', '.css': 'text/css', '.dat': 'application/octet-stream',
  '.dll': 'application/octet-stream', '.pdb': 'application/octet-stream', '.png': 'image/png', '.ico': 'image/x-icon',
  '.webcil': 'application/octet-stream', '.bin': 'application/octet-stream', '.txt': 'text/plain',
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export function startServer({ port = 8098, coi = true } = {}) {
  const state = { coi, cutMiB: 0, cutsLeft: 0, cutSkip: 0, fail503Left: 0, rateMbps: 0, cacheControl: 'public, max-age=3600', log: [] };

  function baseHeaders(extra = {}) {
    const h = { 'Access-Control-Allow-Origin': '*', 'Access-Control-Expose-Headers': 'Content-Length, Content-Range, ETag, Accept-Ranges', ...extra };
    if (state.coi) { h['Cross-Origin-Opener-Policy'] = 'same-origin'; h['Cross-Origin-Embedder-Policy'] = 'require-corp'; }
    return h;
  }

  async function serveImage(req, res, file) {
    const entry = { path: req.url, method: req.method, range: req.headers.range ?? null, ifNoneMatch: req.headers['if-none-match'] ?? null, status: 0, sent: 0, t: Date.now() };
    state.log.push(entry);
    let st;
    try { st = await stat(file); } catch { entry.status = 404; res.writeHead(404, baseHeaders()); res.end(); return; }
    const etag = `"${st.size.toString(16)}-${Math.floor(st.mtimeMs).toString(16)}"`;
    const headers = baseHeaders({ 'Accept-Ranges': 'bytes', ETag: etag, 'Cache-Control': state.cacheControl, 'Content-Type': 'application/octet-stream', 'Last-Modified': st.mtime.toUTCString() });
    if (state.fail503Left > 0 && req.method === 'GET') { state.fail503Left--; entry.status = 503; res.writeHead(503, baseHeaders({ 'Retry-After': '1' })); res.end('panne simulée'); return; }
    if (req.headers['if-none-match'] === etag && !req.headers.range) { entry.status = 304; res.writeHead(304, headers); res.end(); return; }
    let start = 0, end = st.size - 1, status = 200;
    const m = /^bytes=(\d*)-(\d*)$/.exec(req.headers.range ?? '');
    if (m && (m[1] || m[2])) {
      if (m[1]) { start = Number(m[1]); end = m[2] ? Math.min(Number(m[2]), st.size - 1) : st.size - 1; }
      else { start = Math.max(0, st.size - Number(m[2])); }
      if (start > end || start >= st.size) { entry.status = 416; res.writeHead(416, baseHeaders({ 'Content-Range': `bytes */${st.size}` })); res.end(); return; }
      status = 206; headers['Content-Range'] = `bytes ${start}-${end}/${st.size}`;
    }
    headers['Content-Length'] = String(end - start + 1);
    entry.status = status;
    res.writeHead(status, headers);
    if (req.method === 'HEAD') { res.end(); return; }
    // Coupure : après cutSkip réponses GET laissées passer, les cutsLeft suivantes sont fermées après cutMiB Mio.
    let cut = 0;
    if (state.cutsLeft > 0) { if (state.cutSkip > 0) state.cutSkip--; else { cut = state.cutMiB * MiB; state.cutsLeft--; } }
    const stream = createReadStream(file, { start, end, highWaterMark: 256 * 1024 });
    const t0 = Date.now();
    try {
      for await (const chunk of stream) {
        if (res.destroyed) break;
        if (cut && entry.sent + chunk.length > cut) { entry.cut = true; res.destroy(); break; }
        if (!res.write(chunk)) await new Promise((r) => res.once('drain', r));
        entry.sent += chunk.length;
        if (state.rateMbps > 0) {
          const due = t0 + (entry.sent * 8) / (state.rateMbps * 1e6) * 1000;
          const wait = due - Date.now();
          if (wait > 2) await sleep(wait);
        }
      }
    } catch { /* connexion fermée par le client */ }
    if (!res.destroyed) res.end();
    entry.ms = Date.now() - t0;
  }

  async function serveStatic(res, file) {
    try {
      const body = await readFile(file);
      res.writeHead(200, baseHeaders({ 'Content-Type': TYPES[path.extname(file).toLowerCase()] || 'application/octet-stream', 'Cache-Control': 'no-store' }));
      res.end(body);
    } catch { res.writeHead(404, baseHeaders()); res.end('introuvable'); }
  }

  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, 'http://127.0.0.1');
    const p = decodeURIComponent(url.pathname);
    if (req.method === 'OPTIONS') { res.writeHead(204, baseHeaders({ 'Access-Control-Allow-Methods': 'GET, HEAD, OPTIONS', 'Access-Control-Allow-Headers': 'Range, If-None-Match, If-Range' })); res.end(); return; }
    if (p === '/ctl') {
      const q = url.searchParams;
      if (q.has('reset')) { state.log = []; state.cutsLeft = 0; state.cutSkip = 0; state.fail503Left = 0; }
      if (q.has('coi')) state.coi = q.get('coi') === '1';
      if (q.has('cut')) state.cutMiB = Number(q.get('cut'));
      if (q.has('cuts')) state.cutsLeft = Number(q.get('cuts'));
      if (q.has('skip')) state.cutSkip = Number(q.get('skip'));
      if (q.has('fail503')) state.fail503Left = Number(q.get('fail503'));
      if (q.has('rate')) state.rateMbps = Number(q.get('rate'));
      if (q.has('cc')) state.cacheControl = q.get('cc');
      res.writeHead(200, baseHeaders({ 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }));
      res.end(JSON.stringify({ ...state, log: undefined, logLength: state.log.length }));
      return;
    }
    if (p === '/stats') {
      res.writeHead(200, baseHeaders({ 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }));
      res.end(JSON.stringify({ coi: state.coi, rateMbps: state.rateMbps, cacheControl: state.cacheControl, log: state.log }));
      return;
    }
    if (p === '/favicon.ico') { res.writeHead(204, baseHeaders()); res.end(); return; }
    if (p.startsWith('/images/')) {
      const file = path.normalize(path.join(ROOT, 'images', p.slice('/images/'.length)));
      if (!file.startsWith(path.join(ROOT, 'images'))) { res.writeHead(403); res.end(); return; }
      if (file.endsWith('.json')) return serveStatic(res, file);
      return serveImage(req, res, file);
    }
    let file;
    if (p.startsWith('/dotnet/')) {
      const [, , variant, ...rest] = p.split('/');
      file = path.normalize(path.join(ROOT, 'dotnet', 'publish', variant, 'wwwroot', rest.join('/') || 'index.html'));
    } else {
      file = path.normalize(path.join(ROOT, p === '/' ? 'dl.html' : p));
    }
    if (!file.startsWith(ROOT)) { res.writeHead(403); res.end(); return; }
    return serveStatic(res, file);
  });
  server.keepAliveTimeout = 65_000;
  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, '127.0.0.1', () => resolve({ server, port: server.address().port, state }));
  });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i >= 0 ? process.argv[i + 1] : d; };
  const { port } = await startServer({ port: Number(arg('port', 8098)), coi: arg('coi', '1') === '1' });
  process.stdout.write(`http://127.0.0.1:${port}/dl.html\n`);
}
