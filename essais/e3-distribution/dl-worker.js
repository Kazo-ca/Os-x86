// Essai E3 — distribution des images. Code jetable de mesure, exécuté dans un Web Worker.
// Modes : chunked (téléchargement par morceaux Range → SHA-256 → OPFS, reprenable), verify (relecture + SHA-256
// d'un fichier OPFS présent), whole (fetch d'un seul tenant avec integrity), range-demand (lectures Range aléatoires
// comme v86), hash-bench (SubtleCrypto et SHA-256 en JS pur sur 64 Mio), cleanup (supprime le dossier OPFS e3).

const KiB = 1024, MiB = 1024 * KiB;
const DIR = 'e3';
const post = (type, data) => self.postMessage({ type, ...data });
const log = (msg) => post('log', { msg });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const hex = (buf) => [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, '0')).join('');
const b64 = (hexStr) => btoa(String.fromCharCode(...hexStr.match(/../g).map((h) => parseInt(h, 16))));

async function opfsDir() {
  const root = await navigator.storage.getDirectory();
  return root.getDirectoryHandle(DIR, { create: true });
}
async function readState(dir, name) {
  try {
    const fh = await dir.getFileHandle(name + '.state.json');
    return JSON.parse(await (await fh.getFile()).text());
  } catch { return null; }
}
async function writeState(dir, name, state) {
  const fh = await dir.getFileHandle(name + '.state.json', { create: true });
  const h = await fh.createSyncAccessHandle();
  const bytes = new TextEncoder().encode(JSON.stringify(state));
  h.truncate(0); h.write(bytes, { at: 0 }); h.flush(); h.close();
}

// Lit un corps de réponse dans un tampon préalloué ; retourne le nombre d'octets reçus (même en cas d'erreur).
async function readBody(res, target) {
  const reader = res.body.getReader();
  let n = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      if (n + value.length > target.length) throw new Error(`corps trop long (${n + value.length} > ${target.length})`);
      target.set(value, n); n += value.length;
    }
  } catch (e) { e.received = n; throw e; }
  return n;
}

async function chunked({ url, manifestUrl, chunkSize, name, retry = true, maxRetries = 20, verifyAfter = true }) {
  const t0 = performance.now();
  const manifest = await (await fetch(manifestUrl, { cache: 'no-store' })).json();
  const hashes = manifest.chunks[chunkSize];
  if (!hashes) throw new Error('pas de manifeste pour cette taille de morceau');
  const size = manifest.size;
  const nChunks = Math.ceil(size / chunkSize);
  const head = await fetch(url, { method: 'HEAD', cache: 'no-store' });
  const etag = head.headers.get('etag'), acceptRanges = head.headers.get('accept-ranges');
  const dir = await opfsDir();
  let state = await readState(dir, name);
  const resumed = !!(state && state.etag === etag && state.size === size && state.chunkSize === chunkSize);
  if (!resumed) state = { etag, size, chunkSize, verified: [] };
  const verifiedSet = new Set(state.verified);
  const fh = await dir.getFileHandle(name, { create: true });
  const h = await fh.createSyncAccessHandle();
  if (h.getSize() !== size) h.truncate(size);
  const buf = new Uint8Array(chunkSize);
  const r = { mode: 'chunked', chunkSize, nChunks, size, etag, acceptRanges, resumed, verifiedAtStart: verifiedSet.size,
    downloaded: 0, bytesReceived: 0, wastedBytes: 0, retries: 0, errors: [], net_ms: 0, hash_ms: 0, write_ms: 0, state_ms: 0,
    chunk_ms: [], firstByte_ms: null };
  for (let i = 0; i < nChunks; i++) {
    if (verifiedSet.has(i)) continue;
    const start = i * chunkSize, end = Math.min(size, start + chunkSize) - 1, len = end - start + 1;
    let attempt = 0;
    for (;;) {
      const tc = performance.now();
      try {
        const res = await fetch(url, { headers: { Range: `bytes=${start}-${end}` }, cache: 'no-store' });
        if (res.status !== 206) throw new Error(`statut ${res.status} au lieu de 206`);
        if (r.firstByte_ms === null) r.firstByte_ms = performance.now() - t0;
        const n = await readBody(res, buf);
        if (n !== len) throw new Error(`reçu ${n} octets au lieu de ${len}`);
        r.net_ms += performance.now() - tc; r.bytesReceived += n;
        const th = performance.now();
        const digest = hex(await crypto.subtle.digest('SHA-256', buf.subarray(0, len)));
        r.hash_ms += performance.now() - th;
        if (digest !== hashes[i]) throw new Error(`SHA-256 du morceau ${i} incorrect`);
        const tw = performance.now();
        h.write(buf.subarray(0, len), { at: start });
        r.write_ms += performance.now() - tw;
        const ts = performance.now();
        verifiedSet.add(i); state.verified = [...verifiedSet];
        await writeState(dir, name, state);
        r.state_ms += performance.now() - ts;
        r.downloaded++; r.chunk_ms.push(performance.now() - tc);
        break;
      } catch (e) {
        const received = e.received ?? 0;
        r.wastedBytes += received; r.bytesReceived += received;
        r.errors.push({ chunk: i, attempt, received, error: String(e.message || e) });
        if (!retry || attempt >= maxRetries) { h.flush(); h.close(); r.aborted = true; r.total_ms = performance.now() - t0; return r; }
        attempt++; r.retries++;
        await sleep(Math.min(2000, 100 * attempt));
      }
    }
    if (i % 4 === 0) log(`morceau ${i + 1}/${nChunks}`);
  }
  const tf = performance.now();
  h.flush();
  r.flush_ms = performance.now() - tf;
  r.total_ms = performance.now() - t0;
  r.MiBps = (r.downloaded * chunkSize / MiB) / (r.total_ms / 1000);
  if (verifyAfter) {
    const tv = performance.now();
    const v = verifyHandle(h, hashes, chunkSize, size, buf);
    r.verify = { ...(await v), ms: performance.now() - tv };
  }
  h.close();
  return r;
}

async function verifyHandle(h, hashes, chunkSize, size, buf) {
  let read_ms = 0, hash_ms = 0, ok = 0, bad = 0;
  for (let i = 0; i * chunkSize < size; i++) {
    const start = i * chunkSize, len = Math.min(chunkSize, size - start);
    const tr = performance.now();
    const n = h.read(buf.subarray(0, len), { at: start });
    read_ms += performance.now() - tr;
    const th = performance.now();
    const d = hex(await crypto.subtle.digest('SHA-256', buf.subarray(0, n)));
    hash_ms += performance.now() - th;
    if (d === hashes[i]) ok++; else bad++;
  }
  return { read_ms, hash_ms, ok, bad, MiBps: (size / MiB) / ((read_ms + hash_ms) / 1000) };
}

async function verify({ manifestUrl, chunkSize, name }) {
  const t0 = performance.now();
  const manifest = await (await fetch(manifestUrl, { cache: 'no-store' })).json();
  const dir = await opfsDir();
  const h = await (await dir.getFileHandle(name)).createSyncAccessHandle();
  const r = await verifyHandle(h, manifest.chunks[chunkSize], chunkSize, manifest.size, new Uint8Array(chunkSize));
  h.close();
  return { mode: 'verify', chunkSize, ...r, total_ms: performance.now() - t0 };
}

async function whole({ url, manifestUrl, name, integrity = true }) {
  const t0 = performance.now();
  const manifest = await (await fetch(manifestUrl, { cache: 'no-store' })).json();
  const opts = { cache: 'no-store' };
  if (integrity) opts.integrity = 'sha256-' + b64(manifest.sha256);
  const res = await fetch(url, opts);
  const tHeaders = performance.now() - t0;
  const ab = await res.arrayBuffer();
  const tBody = performance.now() - t0;
  const dir = await opfsDir();
  const h = await (await dir.getFileHandle(name, { create: true })).createSyncAccessHandle();
  h.truncate(0); h.write(new Uint8Array(ab), { at: 0 }); h.flush(); h.close();
  const total_ms = performance.now() - t0;
  return { mode: 'whole', integrity, size: ab.byteLength, headers_ms: tHeaders, body_ms: tBody, write_ms: total_ms - tBody, total_ms, MiBps: (ab.byteLength / MiB) / (total_ms / 1000) };
}

async function rangeDemand({ url, size, blockSize = 64 * KiB, count = 500 }) {
  const lat = [];
  const blocks = Math.floor(size / blockSize);
  const t0 = performance.now();
  for (let i = 0; i < count; i++) {
    const start = Math.floor(Math.random() * blocks) * blockSize;
    const a = performance.now();
    const res = await fetch(url, { headers: { Range: `bytes=${start}-${start + blockSize - 1}` }, cache: 'no-store' });
    const ab = await res.arrayBuffer();
    if (res.status !== 206 || ab.byteLength !== blockSize) throw new Error(`lecture Range : statut ${res.status}, ${ab.byteLength} octets`);
    lat.push(performance.now() - a);
  }
  const total_ms = performance.now() - t0;
  const s = Float64Array.from(lat).sort();
  const q = (p) => s[Math.min(s.length - 1, Math.floor(p * (s.length - 1)))];
  return { mode: 'range-demand', blockSize, count, median_ms: q(0.5), p99_ms: q(0.99), mean_ms: total_ms / count, total_ms, MiBps: (count * blockSize / MiB) / (total_ms / 1000) };
}

// SHA-256 en JS pur (implémentation compacte et classique), pour comparaison avec SubtleCrypto et .NET.
function sha256js(data) {
  const K = new Uint32Array([0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2]);
  const H = new Uint32Array([0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]);
  const W = new Uint32Array(64);
  const len = data.length, bitLen = len * 8;
  const padded = new Uint8Array(((len + 9 + 63) >> 6) << 6);
  padded.set(data); padded[len] = 0x80;
  new DataView(padded.buffer).setUint32(padded.length - 4, bitLen >>> 0); new DataView(padded.buffer).setUint32(padded.length - 8, Math.floor(bitLen / 2 ** 32));
  const dv = new DataView(padded.buffer);
  for (let off = 0; off < padded.length; off += 64) {
    for (let i = 0; i < 16; i++) W[i] = dv.getUint32(off + i * 4);
    for (let i = 16; i < 64; i++) {
      const w15 = W[i - 15], w2 = W[i - 2];
      const s0 = ((w15 >>> 7) | (w15 << 25)) ^ ((w15 >>> 18) | (w15 << 14)) ^ (w15 >>> 3);
      const s1 = ((w2 >>> 17) | (w2 << 15)) ^ ((w2 >>> 19) | (w2 << 13)) ^ (w2 >>> 10);
      W[i] = (W[i - 16] + s0 + W[i - 7] + s1) | 0;
    }
    let a = H[0], b = H[1], c = H[2], d = H[3], e = H[4], f = H[5], g = H[6], h = H[7];
    for (let i = 0; i < 64; i++) {
      const S1 = ((e >>> 6) | (e << 26)) ^ ((e >>> 11) | (e << 21)) ^ ((e >>> 25) | (e << 7));
      const ch = (e & f) ^ (~e & g);
      const t1 = (h + S1 + ch + K[i] + W[i]) | 0;
      const S0 = ((a >>> 2) | (a << 30)) ^ ((a >>> 13) | (a << 19)) ^ ((a >>> 22) | (a << 10));
      const maj = (a & b) ^ (a & c) ^ (b & c);
      const t2 = (S0 + maj) | 0;
      h = g; g = f; f = e; e = (d + t1) | 0; d = c; c = b; b = a; a = (t1 + t2) | 0;
    }
    H[0] += a; H[1] += b; H[2] += c; H[3] += d; H[4] += e; H[5] += f; H[6] += g; H[7] += h;
  }
  const out = new Uint8Array(32); const ov = new DataView(out.buffer);
  for (let i = 0; i < 8; i++) ov.setUint32(i * 4, H[i]);
  return out.buffer;
}

async function hashBench({ sizeMiB = 64, passes = 3 }) {
  const buf = new Uint8Array(sizeMiB * MiB);
  const u32 = new Uint32Array(buf.buffer);
  let x = 0x9e3779b9;
  for (let i = 0; i < u32.length; i++) { x ^= x << 13; x >>>= 0; x ^= x >>> 17; x ^= x << 5; x >>>= 0; u32[i] = x; }
  const subtle = [], js = [];
  let d1, d2;
  for (let n = 0; n < passes; n++) { const t = performance.now(); d1 = hex(await crypto.subtle.digest('SHA-256', buf)); subtle.push(performance.now() - t); }
  for (let n = 0; n < passes; n++) { const t = performance.now(); d2 = hex(sha256js(buf)); js.push(performance.now() - t); }
  const MBps = (ms) => sizeMiB * 1.048576 / (ms / 1000);
  return { mode: 'hash-bench', sizeMiB, passes, subtle_ms: subtle, subtle_MBps_best: MBps(Math.min(...subtle)), js_ms: js, js_MBps_best: MBps(Math.min(...js)), same: d1 === d2, sha256: d1 };
}

async function cleanup() {
  const root = await navigator.storage.getDirectory();
  await root.removeEntry(DIR, { recursive: true }).catch(() => {});
  let gone = false;
  try { await root.getDirectoryHandle(DIR); } catch (e) { gone = e.name === 'NotFoundError'; }
  return { mode: 'cleanup', gone };
}

const MODES = { chunked, verify, whole, 'range-demand': rangeDemand, 'hash-bench': hashBench, cleanup };
self.onmessage = async (ev) => {
  const { mode, ...params } = ev.data;
  try {
    const results = await MODES[mode](params);
    post('done', { results });
  } catch (e) {
    post('done', { results: { mode, error: String(e && e.message || e), stack: String(e && e.stack || '') } });
  }
};
